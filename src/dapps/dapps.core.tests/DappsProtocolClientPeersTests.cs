using System.Text;
using AwesomeAssertions;
using dapps.client;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// Plan B6.1 Phase 2 — client-side parser for the new <c>peers</c>
/// response. Drives <see cref="DappsProtocolClient.RequestPeersAsync"/>
/// against canned receiver bytes covering the wire shape's happy path
/// and tolerated edge cases.
/// </summary>
public sealed class DappsProtocolClientPeersTests
{
    [Fact]
    public async Task RequestPeers_EmptyList_ReturnsEmpty()
    {
        // A peer with no neighbours / discovered peers replies with
        // just "end\n" — that's a valid response, not an error.
        var stream = new FakeDuplexStream("end\n"u8.ToArray());
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var result = await client.RequestPeersAsync(CancellationToken.None);

        result.Should().BeEmpty();
        Encoding.UTF8.GetString(stream.WriteCapture.ToArray()).Should().Be("peers\n");
    }

    [Fact]
    public async Task RequestPeers_OnePeerWithFullMeta_ParsesAllFields()
    {
        var stream = new FakeDuplexStream("peer N0THEM-9 source=n port=1\nend\n"u8.ToArray());
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var result = await client.RequestPeersAsync(CancellationToken.None);

        result.Should().ContainSingle();
        var p = result.Single();
        p.Callsign.Should().Be("N0THEM-9");
        p.Source.Should().Be("n");
        p.BpqPort.Should().Be(1);
    }

    [Fact]
    public async Task RequestPeers_PeerWithoutPort_PortIsNull()
    {
        // Discovered-via-beacon peers don't always know their BPQ port
        // (UDP-bearer rows omit it entirely). Server may send "peer
        // CALL source=d" with no port=; client must tolerate the
        // absence rather than rejecting the line.
        var stream = new FakeDuplexStream("peer N0BCN-7 source=d\nend\n"u8.ToArray());
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var result = await client.RequestPeersAsync(CancellationToken.None);

        result.Should().ContainSingle();
        result.Single().Callsign.Should().Be("N0BCN-7");
        result.Single().Source.Should().Be("d");
        result.Single().BpqPort.Should().BeNull();
    }

    [Fact]
    public async Task RequestPeers_MultiplePeers_PreservesOrder()
    {
        var bytes = "peer N0AAA-9 source=n port=0\n" +
                    "peer N0BBB-9 source=n port=1\n" +
                    "peer N0CCC-9 source=d port=1\n" +
                    "end\n";
        var stream = new FakeDuplexStream(Encoding.UTF8.GetBytes(bytes));
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var result = await client.RequestPeersAsync(CancellationToken.None);

        result.Select(p => p.Callsign).Should().Equal("N0AAA-9", "N0BBB-9", "N0CCC-9");
        result.Select(p => p.Source).Should().Equal("n", "n", "d");
    }

    [Fact]
    public async Task RequestPeers_UnknownLineBeforeEnd_TolerantlySkipped()
    {
        // Forward-compatibility: future servers may send extra info
        // (e.g. a "summary 3 peers" header) alongside the peer rows.
        // Lines that aren't peer/end shouldn't break the parse.
        var bytes = "summary 1 peer\npeer N0AAA-9 source=n\nend\n";
        var stream = new FakeDuplexStream(Encoding.UTF8.GetBytes(bytes));
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var result = await client.RequestPeersAsync(CancellationToken.None);

        result.Should().ContainSingle();
        result.Single().Callsign.Should().Be("N0AAA-9");
    }

    [Fact]
    public async Task RequestPeers_EofBeforeEnd_ReturnsWhatWeHave()
    {
        // Connection drop mid-response shouldn't throw — return what we
        // got. NodeProber treats a partial peers list as best-effort
        // anyway, and the probe itself already succeeded.
        var stream = new FakeDuplexStream("peer N0AAA-9 source=n\n"u8.ToArray());
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var result = await client.RequestPeersAsync(CancellationToken.None);

        result.Should().ContainSingle();
        result.Single().Callsign.Should().Be("N0AAA-9");
    }

    [Fact]
    public async Task RequestPeers_PortValueOutOfRange_IgnoredForSafety()
    {
        // BPQ port byte is one octet; reject anything outside 0..255.
        // Don't let a misbehaving server poison our probe scheduler with
        // negative or oversized port hints.
        var stream = new FakeDuplexStream("peer N0BAD-9 source=n port=999\nend\n"u8.ToArray());
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        var result = await client.RequestPeersAsync(CancellationToken.None);

        result.Should().ContainSingle();
        result.Single().Callsign.Should().Be("N0BAD-9");
        result.Single().BpqPort.Should().BeNull();
    }
}
