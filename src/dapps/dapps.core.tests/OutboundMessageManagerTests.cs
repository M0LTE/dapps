using System.Text;
using AwesomeAssertions;
using dapps.client.Transport;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Drives <see cref="OutboundMessageManager.DoRun"/> with a fake transport
/// that scripts the receiving side of the DAPPS protocol. Lets the test
/// assert what the manager wrote on the wire (residual ttl, in particular)
/// without standing up a real BPQ.
///
/// This is the seam where A1 is decided — the manager reads
/// <c>DbMessage.Ttl</c> and <c>CreatedAt</c>, computes residual, and either
/// drops or threads it to the protocol client. Pure-function tests on
/// <see cref="TtlMath"/> cover the arithmetic; this covers the actual
/// integration of "expired row → no transport call + DB delete" and
/// "fresh row → transport call with smaller ttl on wire".
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class OutboundMessageManagerTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;
    private FakeOutboundTransport transport = null!;
    private OutboundMessageManager manager = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-omm-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbOffer>();
            c.CreateTable<DbMessage>();
            c.CreateTable<DbNeighbour>();
            c.CreateTable<DbRouteHint>();
            // A neighbour entry alone is enough to route messages to
            // app@N0DEST (post-A2 resolver matches base callsigns).
            c.Insert(new DbNeighbour { Callsign = "N0DEST", BpqPort = 0 });
        }

        var optionsMonitor = new TestOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0CALL",
            DefaultBpqPort = 0,
        });
        database = new Database(NullLogger<Database>.Instance, optionsMonitor);
        transport = new FakeOutboundTransport();
        manager = new OutboundMessageManager(database, NullLoggerFactory.Instance, optionsMonitor, transport);

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task DoRun_MessageWithNoTtl_ForwardsWithoutTtlOnTheWire()
    {
        InsertMessage(id: "noex001", ttl: null, createdAt: DateTime.UtcNow);
        transport.ScriptHappyPath(messageId: "noex001");

        await manager.DoRun(TestContext.Current.CancellationToken);

        transport.OfferLineWritten.Should().NotBeNull();
        transport.OfferLineWritten!.Should().NotContain("ttl=");
    }

    [Fact]
    public async Task DoRun_MessageWithTtl_ForwardsWithDecrementedTtl()
    {
        // 30s in queue; 60s ttl → wire should carry ttl=30 (or 29-30 depending
        // on how the elapsed rounds — assert ≤30 and >0).
        InsertMessage(id: "fresh01", ttl: 60, createdAt: DateTime.UtcNow.AddSeconds(-30));
        transport.ScriptHappyPath(messageId: "fresh01");

        await manager.DoRun(TestContext.Current.CancellationToken);

        transport.OfferLineWritten.Should().NotBeNull();
        var written = transport.OfferLineWritten!;
        written.Should().Contain("ttl=");
        var ttlValue = ExtractTtl(written);
        ttlValue.Should().BeInRange(25, 30);
    }

    [Fact]
    public async Task DoRun_ExpiredMessage_DropsWithoutCallingTransport()
    {
        // 120s in queue with ttl=60 → expired by 60s.
        InsertMessage(id: "expired1", ttl: 60, createdAt: DateTime.UtcNow.AddSeconds(-120));

        await manager.DoRun(TestContext.Current.CancellationToken);

        transport.ConnectCalls.Should().Be(0);
        // Row should be deleted so it doesn't get retried.
        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("expired1").Should().BeNull();
    }

    [Fact]
    public async Task DoRun_ExactlyAtTtlBoundary_DropsAsExpired()
    {
        // 60s in queue with ttl=60 → residual is 0, which the spec says
        // MUST be dropped.
        InsertMessage(id: "ontime1", ttl: 60, createdAt: DateTime.UtcNow.AddSeconds(-60));

        await manager.DoRun(TestContext.Current.CancellationToken);

        transport.ConnectCalls.Should().Be(0);
        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("ontime1").Should().BeNull();
    }

    [Fact]
    public async Task DoRun_DestinationSsidDiffersFromNeighbourSsid_StillRoutesViaBaseCallsignMatch()
    {
        // Default fixture has a neighbour with callsign "N0DEST" (no SSID).
        // A message addressed to "app@N0DEST-7" should still match it: SSIDs
        // describe links, the neighbour entry describes a peer DAPPS instance
        // and is reachable on whichever SSID the row recorded.
        using (var c = DbInfo.GetConnection())
        {
            c.Insert(new DbMessage
            {
                Id = "ssid001",
                Payload = "x"u8.ToArray(),
                Salt = 1L,
                Destination = "app@N0DEST-7",
                SourceCallsign = "N0CALL",
                AdditionalProperties = "{}",
            });
        }
        transport.ScriptHappyPath(messageId: "ssid001");

        await manager.DoRun(TestContext.Current.CancellationToken);

        transport.ConnectCalls.Should().Be(1);
        transport.OfferLineWritten.Should().NotBeNull();
        transport.OfferLineWritten!.Should().Contain("dst=app@N0DEST-7");
    }

    [Fact]
    public async Task DoRun_NoMatchingNeighbour_LeavesMessageUnforwarded()
    {
        InsertMessage(id: "noroute", ttl: null, createdAt: DateTime.UtcNow);
        // Override default destination by inserting a row destined elsewhere.
        using (var c = DbInfo.GetConnection())
        {
            c.Execute("update messages set destination=? where id=?", "app@N0OTHER", "noroute");
        }

        await manager.DoRun(TestContext.Current.CancellationToken);

        transport.ConnectCalls.Should().Be(0);
        using var conn = DbInfo.GetConnection();
        var row = conn.Find<DbMessage>("noroute");
        row.Should().NotBeNull();
        row!.Forwarded.Should().BeFalse("no neighbour matched, message should sit in queue until ttl expires");
    }

    [Fact]
    public async Task DoRun_MixedExpiredAndFresh_DropsExpiredAndForwardsFresh()
    {
        InsertMessage(id: "expir02", ttl: 5, createdAt: DateTime.UtcNow.AddSeconds(-3600));
        InsertMessage(id: "fresh02", ttl: 600, createdAt: DateTime.UtcNow.AddSeconds(-1));
        transport.ScriptHappyPath(messageId: "fresh02");

        await manager.DoRun(TestContext.Current.CancellationToken);

        transport.ConnectCalls.Should().Be(1);
        transport.OfferLineWritten.Should().NotBeNull();
        transport.OfferLineWritten!.Should().Contain("fresh02");

        using var c = DbInfo.GetConnection();
        c.Find<DbMessage>("expir02").Should().BeNull();
        // fresh02 should still exist but marked Forwarded=1.
        var fresh = c.Find<DbMessage>("fresh02");
        fresh.Should().NotBeNull();
        fresh!.Forwarded.Should().BeTrue();
    }

    private static int ExtractTtl(string offerLine)
    {
        var marker = "ttl=";
        var idx = offerLine.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) throw new InvalidOperationException($"no ttl= in {offerLine}");
        var rest = offerLine[(idx + marker.Length)..];
        var space = rest.IndexOf(' ');
        var newline = rest.IndexOf('\n');
        var end = (space, newline) switch
        {
            (< 0, < 0) => rest.Length,
            (< 0, _) => newline,
            (_, < 0) => space,
            _ => Math.Min(space, newline),
        };
        return int.Parse(rest[..end]);
    }

    private static void InsertMessage(string id, int? ttl, DateTime createdAt)
    {
        using var c = DbInfo.GetConnection();
        c.Insert(new DbMessage
        {
            Id = id,
            Payload = Encoding.UTF8.GetBytes("payload-" + id),
            Salt = 1L,
            Destination = "app@N0DEST",
            SourceCallsign = "N0CALL",
            AdditionalProperties = "{}",
            Ttl = ttl,
            CreatedAt = createdAt,
        });
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>
    /// IDappsOutboundTransport that hands back a stream pre-loaded with the
    /// canned receiver-side replies (DAPPSv1> prompt, send id, ack id) so
    /// DappsProtocolClient drives through happily, and captures the bytes
    /// the SUT writes for assertion.
    /// </summary>
    private sealed class FakeOutboundTransport : IDappsOutboundTransport
    {
        public int ConnectCalls;
        private byte[] cannedReceiverBytes = [];
        public string? OfferLineWritten { get; private set; }

        public void ScriptHappyPath(string messageId)
        {
            cannedReceiverBytes = Encoding.UTF8.GetBytes(
                $"DAPPSv1>\nsend {messageId}\nack {messageId}\n");
        }

        public Task<IDappsConnection> ConnectAsync(string localCallsign, string remoteCallsign, int bpqPortNumber, CancellationToken stoppingToken)
        {
            ConnectCalls++;
            var stream = new CapturingStream(cannedReceiverBytes, line => OfferLineWritten = line);
            return Task.FromResult<IDappsConnection>(new FakeConnection(stream));
        }

        private sealed class FakeConnection : IDappsConnection
        {
            public FakeConnection(Stream stream) { Stream = stream; }
            public Stream Stream { get; }
            public ValueTask DisposeAsync()
            {
                Stream.Dispose();
                return ValueTask.CompletedTask;
            }
        }

        /// <summary>
        /// Wraps a FakeDuplexStream and additionally pulls out the first
        /// `ihave ...` line that the SUT writes — that's the line the test
        /// wants to assert on.
        /// </summary>
        private sealed class CapturingStream(byte[] preloaded, Action<string> onIhaveLine) : Stream
        {
            private readonly MemoryStream readBuffer = new(preloaded);
            private readonly MemoryStream writeBuffer = new();
            private bool ihaveCaptured;

            public override int Read(byte[] buffer, int offset, int count) => readBuffer.Read(buffer, offset, count);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => readBuffer.ReadAsync(buffer, offset, count, ct);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => readBuffer.ReadAsync(buffer, ct);

            public override void Write(byte[] buffer, int offset, int count)
            {
                writeBuffer.Write(buffer, offset, count);
                MaybeCaptureIhaveLine();
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                writeBuffer.Write(buffer, offset, count);
                MaybeCaptureIhaveLine();
                return Task.CompletedTask;
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            {
                writeBuffer.Write(buffer.Span);
                MaybeCaptureIhaveLine();
                return ValueTask.CompletedTask;
            }

            private void MaybeCaptureIhaveLine()
            {
                if (ihaveCaptured) return;
                var bytes = writeBuffer.ToArray();
                var newline = Array.IndexOf(bytes, (byte)'\n');
                if (newline < 0) return;
                var line = Encoding.UTF8.GetString(bytes, 0, newline);
                if (line.StartsWith("ihave ", StringComparison.Ordinal))
                {
                    onIhaveLine(line);
                    ihaveCaptured = true;
                }
            }

            public override void Flush() => writeBuffer.Flush();
            public override Task FlushAsync(CancellationToken ct) => writeBuffer.FlushAsync(ct);

            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
