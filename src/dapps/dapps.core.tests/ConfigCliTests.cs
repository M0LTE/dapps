using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using SQLite;

namespace dapps.core.tests;

/// <summary>
/// Plan B7 follow-up — `dapps --show-config`. Reads the persisted
/// systemoptions table and prints DAPPS_SCREAMING_SNAKE=value pairs,
/// without booting the host. These tests drive the writer against a
/// fresh SQLite file and assert exit code + stdout shape.
///
/// The CLI is in <c>dapps.core/Updater/ConfigCli.cs</c>, marked
/// internal — exposed to the test assembly via
/// <c>[assembly: InternalsVisibleTo]</c> in the project's csproj.
/// </summary>
[Collection(SqliteOverridePathCollection.Name)]
public sealed class ConfigCliTests : IAsyncLifetime
{
    private string dbPath = null!;

    public ValueTask InitializeAsync()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"dapps-cfgcli-{Guid.NewGuid():N}.db");
        DbInfo.OverridePath = dbPath;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DbInfo.OverridePath = null;
        try { File.Delete(dbPath); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void ShowConfig_FreshDb_PrintsHeaderAndExitsZero()
    {
        var (exit, stdout, _) = RunShowConfig();
        exit.Should().Be(0);
        stdout.Should().Contain("(no rows in systemoptions");
    }

    [Fact]
    public void ShowConfig_WithSeededRows_PrintsScreamingSnakeAssignments()
    {
        using (var c = new SQLiteConnection(dbPath))
        {
            c.CreateTable<DbSystemOption>();
            c.Insert(new DbSystemOption { Option = "Callsign", Value = "G0TST-9" });
            c.Insert(new DbSystemOption { Option = "DiscoveryAirtimeBudgetSecondsPerHour", Value = "120" });
            c.Insert(new DbSystemOption { Option = "ProbeStrategy", Value = "Overnight" });
        }

        var (exit, stdout, _) = RunShowConfig();
        exit.Should().Be(0);
        stdout.Should().Contain("DAPPS_CALLSIGN=G0TST-9");
        stdout.Should().Contain("DAPPS_DISCOVERY_AIRTIME_BUDGET_SECONDS_PER_HOUR=120");
        stdout.Should().Contain("DAPPS_PROBE_STRATEGY=Overnight");
    }

    private static (int exit, string stdout, string stderr) RunShowConfig()
    {
        var savedOut = Console.Out;
        var savedErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = dapps.core.Updater.ConfigCli.ShowConfig();
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
        }
    }
}
