using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// DbStartup.EnsureSchemaAndSeed is the system's first-run config seam:
/// it creates schema, seeds defaults from env vars (Plan C2), and
/// refuses to start on a placeholder callsign. These tests drive each
/// path against a fresh SQLite file.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class DbStartupTests : IAsyncLifetime
{
    private string dbPath = null!;
    private readonly Dictionary<string, string?> savedEnv = [];

    private static readonly string[] EnvKeys =
    [
        "DAPPS_NODE_TYPE",
        "DAPPS_NODE_HOST",
        "DAPPS_AGW_PORT",
        "DAPPS_DEFAULT_BPQ_PORT",
        "DAPPS_CALLSIGN",
        "DAPPS_MQTT_PORT",
    ];

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-startup-test-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;

        // Snapshot env so tests don't bleed into each other or into the host.
        foreach (var k in EnvKeys)
        {
            savedEnv[k] = Environment.GetEnvironmentVariable(k);
            Environment.SetEnvironmentVariable(k, null);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var (k, v) in savedEnv)
        {
            Environment.SetEnvironmentVariable(k, v);
        }
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void EnsureSchemaAndSeed_NoEnvVars_ThrowsOnPlaceholderCallsign()
    {
        var act = () => DbStartup.EnsureSchemaAndSeed();

        var ex = act.Should().Throw<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Callsign", "the error must point the operator at the actual missing config");
        ex.Which.Message.Should().Contain("DAPPS_CALLSIGN");
    }

    [Fact]
    public void EnsureSchemaAndSeed_CallsignFromEnvVar_SeedsAndStarts()
    {
        Environment.SetEnvironmentVariable("DAPPS_CALLSIGN", "G0TST");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        var row = c.Find<DbSystemOption>("Callsign");
        row.Should().NotBeNull();
        row!.Value.Should().Be("G0TST");
    }

    [Fact]
    public void EnsureSchemaAndSeed_AllEnvVars_SeedsEachOption()
    {
        Environment.SetEnvironmentVariable("DAPPS_CALLSIGN", "G0TST");
        Environment.SetEnvironmentVariable("DAPPS_NODE_HOST", "bpq.local");
        Environment.SetEnvironmentVariable("DAPPS_AGW_PORT", "8001");
        Environment.SetEnvironmentVariable("DAPPS_DEFAULT_BPQ_PORT", "2");
        Environment.SetEnvironmentVariable("DAPPS_MQTT_PORT", "1884");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("G0TST");
        c.Find<DbSystemOption>("NodeHost")!.Value.Should().Be("bpq.local");
        c.Find<DbSystemOption>("AgwPort")!.Value.Should().Be("8001");
        c.Find<DbSystemOption>("DefaultBpqPort")!.Value.Should().Be("2");
        c.Find<DbSystemOption>("MqttPort")!.Value.Should().Be("1884");
    }

    [Fact]
    public void EnsureSchemaAndSeed_ExistingRow_NotOverwrittenByEnv()
    {
        // Pre-seed a manually-configured callsign as if /Config POST had set it.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "M0LTE-3" });
        }
        Environment.SetEnvironmentVariable("DAPPS_CALLSIGN", "DIFFERENT-CALL");

        DbStartup.EnsureSchemaAndSeed();

        using var conn = DbInfo.GetConnection();
        conn.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M0LTE-3",
            "existing rows MUST NOT be overwritten by env vars on subsequent starts");
    }

    [Fact]
    public void EnsureSchemaAndSeed_ExistingPlaceholderCallsign_StillThrows()
    {
        // Operator left N0CALL in the DB somehow - must still error,
        // not silently transmit on it.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "N0CALL" });
        }

        var act = () => DbStartup.EnsureSchemaAndSeed();

        act.Should().Throw<InvalidOperationException>();
    }
}
