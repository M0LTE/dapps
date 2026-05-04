using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Database-level tests for <see cref="DbProbedNode"/> CRUD that backs
/// the /Probes REST surface and the scheduler's persistence path.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class ProbedNodeCrudTests : IAsyncLifetime
{
    private string dbPath = null!;
    private Database database = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-probe-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbProbedNode>();
        }

        database = new Database(NullLogger<Database>.Instance,
            new TestOptionsMonitor<SystemOptions>(new SystemOptions { Callsign = "N0CALL" }));

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task UpsertProbedNode_FirstWrite_Inserts()
    {
        await database.UpsertProbedNode(new DbProbedNode
        {
            Callsign = "N0THEM-9",
            LastBearerPort = 1,
            LastProbedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            LastSuccessAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            SuccessCount = 1,
        });

        var rows = await database.GetProbedNodes();
        rows.Should().ContainSingle();
        var row = rows.Single();
        row.Callsign.Should().Be("N0THEM-9");
        row.LastBearerPort.Should().Be(1);
        row.SuccessCount.Should().Be(1);
        row.OptOut.Should().BeFalse();
    }

    [Fact]
    public async Task UpsertProbedNode_SameCallsignTwice_UpdatesInPlace()
    {
        await database.UpsertProbedNode(new DbProbedNode
        {
            Callsign = "N0THEM-9",
            ConsecutiveFailures = 1,
            LastError = "first attempt timed out",
        });
        var first = (await database.GetProbedNodes()).Single();
        first.ConsecutiveFailures = 0;
        first.LastError = "";
        first.SuccessCount = 1;

        await database.UpsertProbedNode(first);

        var rows = await database.GetProbedNodes();
        rows.Should().ContainSingle("upsert MUST NOT create duplicate rows for same callsign");
        rows.Single().ConsecutiveFailures.Should().Be(0);
        rows.Single().LastError.Should().BeEmpty();
        rows.Single().SuccessCount.Should().Be(1);
    }

    [Fact]
    public async Task GetProbedNode_Missing_ReturnsNull()
    {
        (await database.GetProbedNode("N0WHO")).Should().BeNull();
    }

    [Fact]
    public async Task GetProbedNodes_OrdersNeverProbedAfterMostRecent()
    {
        // Operators looking at the dashboard want "most recently
        // probed" first; never-probed rows (operator-created opt-outs)
        // sit at the bottom.
        await database.UpsertProbedNode(new DbProbedNode
        {
            Callsign = "N0OLD",
            LastProbedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await database.UpsertProbedNode(new DbProbedNode
        {
            Callsign = "N0NEW",
            LastProbedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await database.UpsertProbedNode(new DbProbedNode
        {
            Callsign = "N0NEVER",
            OptOut = true,
        });

        var rows = await database.GetProbedNodes();
        rows.Select(r => r.Callsign).Should().Equal("N0NEW", "N0OLD", "N0NEVER");
    }

    [Fact]
    public async Task RemoveProbedNode_Existing_ReturnsTrueAndDeletes()
    {
        await database.UpsertProbedNode(new DbProbedNode { Callsign = "N0BYE" });

        var removed = await database.RemoveProbedNode("N0BYE");

        removed.Should().BeTrue();
        (await database.GetProbedNodes()).Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveProbedNode_Absent_ReturnsFalse()
    {
        (await database.RemoveProbedNode("N0WHO")).Should().BeFalse();
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
