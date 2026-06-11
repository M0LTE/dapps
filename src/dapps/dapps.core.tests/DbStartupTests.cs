using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// DbStartup.EnsureSchemaAndSeed is the system's startup config seam:
/// it creates schema, seeds defaults from env vars (Plan C2),
/// re-applies set env vars at every start (deployment-managed config),
/// derives a callsign from a pdn host's PDN_NODE_CALLSIGN, and warns
/// on a placeholder callsign. These tests drive each path against a
/// fresh SQLite file.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class DbStartupTests : IAsyncLifetime
{
    private string dbPath = null!;
    private readonly Dictionary<string, string?> savedEnv = [];

    private static readonly string[] EnvKeys =
    [
        "DAPPS_NODE_HOST",
        "DAPPS_AGW_PORT",
        "DAPPS_DEFAULT_BEARER_PORT",
        "DAPPS_CALLSIGN",
        "DAPPS_MQTT_PORT",
        "DAPPS_SSID",
        "PDN_NODE_CALLSIGN",
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
    public void EnsureSchemaAndSeed_NoEnvVars_StartsWithPlaceholderCallsign()
    {
        // Drop-the-binary-and-go-to-the-dashboard install flow: the
        // daemon must start cleanly with no env vars and the seeded
        // placeholder callsign. Inbound bearer services and the
        // forwarder gate themselves on a real callsign at runtime;
        // /Health reports CallsignConfigured=false until /Setup or
        // /Config configures one.
        var act = () => DbStartup.EnsureSchemaAndSeed();
        act.Should().NotThrow();

        using var c = DbInfo.GetConnection();
        var row = c.Find<DbSystemOption>("Callsign");
        row.Should().NotBeNull();
        row!.Value.Should().Be("N0CALL");
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
        Environment.SetEnvironmentVariable("DAPPS_DEFAULT_BEARER_PORT", "2");
        Environment.SetEnvironmentVariable("DAPPS_MQTT_PORT", "1884");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("G0TST");
        c.Find<DbSystemOption>("NodeHost")!.Value.Should().Be("bpq.local");
        c.Find<DbSystemOption>("AgwPort")!.Value.Should().Be("8001");
        c.Find<DbSystemOption>("DefaultBearerPort")!.Value.Should().Be("2");
        c.Find<DbSystemOption>("MqttPort")!.Value.Should().Be("1884");
    }

    [Fact]
    public void EnsureSchemaAndSeed_ExistingRow_EnvSet_AppliedAtEveryStart()
    {
        // Deployment-managed config: a SET env var wins over the stored
        // row at every start, not just the first - the pdn supervised-
        // app case, where the host's app config is authoritative.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "M0LTE-3" });
        }
        Environment.SetEnvironmentVariable("DAPPS_CALLSIGN", "G0TST-7");

        DbStartup.EnsureSchemaAndSeed();

        using var conn = DbInfo.GetConnection();
        conn.Find<DbSystemOption>("Callsign")!.Value.Should().Be("G0TST-7",
            "a set env var is deployment-managed and re-applied over the stored row at every start");
    }

    [Fact]
    public void EnsureSchemaAndSeed_ExistingRow_NoEnv_LeftAlone()
    {
        // The standalone flow: no DAPPS_* env set, so the stored
        // (dashboard-configured) value must survive every restart
        // byte-for-byte.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "M0LTE-3" });
        }

        DbStartup.EnsureSchemaAndSeed();

        using var conn = DbInfo.GetConnection();
        conn.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M0LTE-3",
            "unset env vars must never touch stored config");
    }

    [Fact]
    public void EnsureSchemaAndSeed_SeedOnceViaEnvThenUnset_DashboardEditSticks()
    {
        // A standalone operator who seeds once via env then unsets it
        // keeps dashboard control exactly as before this change.
        Environment.SetEnvironmentVariable("DAPPS_CALLSIGN", "G0TST");
        DbStartup.EnsureSchemaAndSeed();
        Environment.SetEnvironmentVariable("DAPPS_CALLSIGN", null);

        // Restart without the env var: seeded value sticks.
        DbStartup.EnsureSchemaAndSeed();
        using (var c = DbInfo.GetConnection())
        {
            c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("G0TST");
            // Dashboard edit (as /Config POST would persist it).
            c.Execute("update systemoptions set value=? where option=?", "M0LTE-3", "Callsign");
        }

        // Next restart: the edit survives.
        DbStartup.EnsureSchemaAndSeed();
        using var conn = DbInfo.GetConnection();
        conn.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M0LTE-3");
    }

    [Fact]
    public void EnsureSchemaAndSeed_PdnNodeCallsign_DerivesCallsignWithDefaultSsid()
    {
        // pdn-hosted fresh install: no DAPPS_CALLSIGN, host injects
        // PDN_NODE_CALLSIGN -> DAPPS takes up residence at SSID -7 of
        // the node callsign.
        Environment.SetEnvironmentVariable("PDN_NODE_CALLSIGN", "M9YYY");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-7");
        c.Find<DbSystemOption>("Ssid")!.Value.Should().Be("7", "the SSID knob is seeded alongside");
    }

    [Fact]
    public void EnsureSchemaAndSeed_PdnNodeCallsignWithSsid_StripsItBeforeComposing()
    {
        Environment.SetEnvironmentVariable("PDN_NODE_CALLSIGN", "m9yyy-2");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-7",
            "the node's own SSID is stripped (and the base upper-cased) before composing");
    }

    [Fact]
    public void EnsureSchemaAndSeed_DappsSsidEnv_OverridesDerivationSsid()
    {
        Environment.SetEnvironmentVariable("PDN_NODE_CALLSIGN", "M9YYY");
        Environment.SetEnvironmentVariable("DAPPS_SSID", "4");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-4");
    }

    [Fact]
    public void EnsureSchemaAndSeed_ExplicitDappsCallsign_WinsOverDerivation()
    {
        Environment.SetEnvironmentVariable("PDN_NODE_CALLSIGN", "M9YYY");
        Environment.SetEnvironmentVariable("DAPPS_CALLSIGN", "G0TST-1");

        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("G0TST-1",
            "an explicit DAPPS_CALLSIGN always wins over derivation");
    }

    [Fact]
    public void EnsureSchemaAndSeed_RealStoredCallsign_WinsOverDerivation()
    {
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "M0LTE-3" });
        }
        Environment.SetEnvironmentVariable("PDN_NODE_CALLSIGN", "M9YYY");

        DbStartup.EnsureSchemaAndSeed();

        using var conn = DbInfo.GetConnection();
        conn.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M0LTE-3",
            "a real stored callsign always wins over derivation");
    }

    [Fact]
    public void EnsureSchemaAndSeed_StoredPlaceholder_PdnNodeCallsign_Derives()
    {
        // A pdn host whose DAPPS db predates a callsign config (or was
        // reset to the placeholder) picks up the derived identity on
        // the next start.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "N0CALL" });
        }
        Environment.SetEnvironmentVariable("PDN_NODE_CALLSIGN", "M9YYY");

        DbStartup.EnsureSchemaAndSeed();

        using var conn = DbInfo.GetConnection();
        conn.Find<DbSystemOption>("Callsign")!.Value.Should().Be("M9YYY-7");
    }

    [Fact]
    public void EnsureSchemaAndSeed_NoPdnEnv_PlaceholderUnchanged()
    {
        // Standalone install: no PDN_NODE_CALLSIGN, no DAPPS_CALLSIGN -
        // the placeholder stays and the daemon boots into the existing
        // setup-required flow (configure via /Setup // /Config).
        DbStartup.EnsureSchemaAndSeed();

        using var c = DbInfo.GetConnection();
        c.Find<DbSystemOption>("Callsign")!.Value.Should().Be("N0CALL");
    }

    [Fact]
    public void EnsureSchemaAndSeed_ExistingPlaceholderCallsign_StartsWithWarning()
    {
        // Operator left N0CALL in the DB. Daemon starts (so the
        // dashboard becomes reachable for /Setup); inbound bearer +
        // forwarder gate themselves at runtime - frames stamped with
        // the placeholder never go on the air.
        using (var c = DbInfo.GetConnection())
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "N0CALL" });
        }

        var act = () => DbStartup.EnsureSchemaAndSeed();

        act.Should().NotThrow();
    }
}
