using System.Reflection;
using Microsoft.Extensions.Logging;

namespace dapps.core.Updater;

/// <summary>
/// Plan C5.2 — the side-door CLI flags on the dapps binary that don't
/// boot the host. Recognised before <c>WebApplication.CreateBuilder</c>
/// runs:
/// <list type="bullet">
/// <item><c>--version</c> — print version, exit 0.</item>
/// <item><c>--check-update</c> — query GitHub Releases, print the
/// available tag (or "none"), exit 0.</item>
/// <item><c>--apply-update</c> — privileged: download the latest
/// asset for our RID, swap it in, restart the service, verify, roll
/// back on any failure. ExecStart-ed by <c>dapps-updater.service</c>.
/// Exit 0 on success / no-update-needed, 1 on rolled-back, 2 on
/// failed-without-swap.</item>
/// <item><c>--rollback</c> — privileged: restore <c>dapps.previous</c>
/// over <c>dapps</c> + restart. Manual operator rescue from SSH when
/// the dashboard isn't enough.</item>
/// </list>
/// All four exit before any host plumbing runs, so they work even
/// when the on-disk dapps.db is incompatible / a port is wedged / the
/// callsign is unset.
/// </summary>
public static class UpdaterCli
{
    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0) return false;

        switch (args[0])
        {
            case "--version":
                Console.WriteLine(ResolveCurrentVersion());
                return true;

            case "--check-update":
                exitCode = CheckUpdateAsync(CancellationToken.None).GetAwaiter().GetResult();
                return true;

            case "--apply-update":
                exitCode = ApplyUpdateAsync(CancellationToken.None).GetAwaiter().GetResult();
                return true;

            case "--rollback":
                exitCode = RollBackAsync(CancellationToken.None).GetAwaiter().GetResult();
                return true;

            default:
                return false;
        }
    }

    public static string ResolveCurrentVersion()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
    }

    private static async Task<int> CheckUpdateAsync(CancellationToken ct)
    {
        using var lf = LoggerFactory.Create(b => b.AddSimpleConsole());
        var (downloader, _) = BuildDownloader(lf);
        var rid = UpdaterOrchestrator.ResolveDefaultRid();
        var current = ResolveCurrentVersion();

        try
        {
            var latest = await downloader.GetLatestAsync(rid, ct);
            if (latest is null)
            {
                Console.WriteLine($"current={current} latest=none rid={rid}");
                return 0;
            }
            var tag = latest.Tag.TrimStart('v');
            var status = string.Equals(tag, current, StringComparison.Ordinal) ? "up-to-date" : "available";
            Console.WriteLine($"current={current} latest={tag} status={status} rid={rid}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"check-update failed: {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> ApplyUpdateAsync(CancellationToken ct)
    {
        using var lf = LoggerFactory.Create(b => b.AddSimpleConsole());
        var (downloader, _) = BuildDownloader(lf);
        var orch = new UpdaterOrchestrator(
            UpdaterPaths.Default,
            new RealUpdaterFileSystem(),
            downloader,
            new RealUpdaterProcess(),
            lf.CreateLogger<UpdaterOrchestrator>(),
            ResolveCurrentVersion());
        return await orch.ApplyUpdateAsync(ct);
    }

    private static async Task<int> RollBackAsync(CancellationToken ct)
    {
        using var lf = LoggerFactory.Create(b => b.AddSimpleConsole());
        var (downloader, _) = BuildDownloader(lf);
        var orch = new UpdaterOrchestrator(
            UpdaterPaths.Default,
            new RealUpdaterFileSystem(),
            downloader,
            new RealUpdaterProcess(),
            lf.CreateLogger<UpdaterOrchestrator>(),
            ResolveCurrentVersion());
        return await orch.RollBackAsync(ct);
    }

    private static (IUpdaterDownloader, IHttpClientFactory) BuildDownloader(ILoggerFactory lf)
    {
        // CLI side-doors don't have access to the DI container the host
        // would build, so spin up a minimal HttpClientFactory just for
        // this run. .NET ships ServicesHttpClientFactory we could use,
        // but a static singleton suffices for two short-lived calls.
        var factory = new SimpleHttpClientFactory();
        return (new RealUpdaterDownloader(factory, lf.CreateLogger<RealUpdaterDownloader>()), factory);
    }

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
