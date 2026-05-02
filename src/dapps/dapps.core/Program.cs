using dapps.client.Backhaul;
using dapps.client.Backhaul.Datagram;
using dapps.client.Discovery;
using dapps.client.Transport;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using dapps.core.Routing;
using dapps.core.Services;
using dapps.core.Updater;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using System.Net.Sockets;
// OpenAPI / Scalar dropped in the .NET 8 rollback — the native
// OpenAPI generation (AddOpenApi / MapOpenApi) is a .NET 9+ API.
// To revisit once we're back on a newer .NET runtime.

// Plan C5.2 — CLI side-doors that don't boot the host.
// Recognised: --version, --check-update, --apply-update, --rollback.
// Returning before CreateBuilder runs means these work even when the
// on-disk dapps.db is incompatible / a port is wedged / the callsign
// is unset, which is exactly when --rollback is most useful.
if (UpdaterCli.TryHandle(args, out var cliExitCode)) return cliExitCode;

// Seed the systemoptions table BEFORE host build. The
// SystemOptions Configure callback (just below) fires during eager
// hosted-service DI graph materialisation — UdpDatagramListener →
// IBackhaulInbox → IRoutingAlgorithm → IOptionsMonitor.CurrentValue —
// which would race a hosted-service seeder and lose, since hosted
// services are CONSTRUCTED in one pass before any of their StartAsync
// runs.
DbStartup.EnsureSchemaAndSeed();

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddRazorPages();
// Sync — Configure expects an Action, so an `async` lambda would
// compile as async-void, fire-and-forget the GetOptions read, and
// hand callers a SystemOptions instance with un-populated values.
// Block here so the first resolution is deterministic.
builder.Services.AddOptions<SystemOptions>().Configure<OptionsRepo, ILogger<SystemOptions>>((o, db, logger) =>
{
    var options = db.GetOptions().GetAwaiter().GetResult();
    o.NodeHost = options.Single(o => o.Option == "NodeHost").Value;
    o.AgwPort = int.Parse(options.Single(o => o.Option == "AgwPort").Value);
    o.DefaultBpqPort = int.Parse(options.Single(o => o.Option == "DefaultBpqPort").Value);
    o.Callsign = options.Single(o => o.Option == "Callsign").Value;
    o.MqttPort = int.Parse(options.Single(o => o.Option == "MqttPort").Value);
    o.UdpListenPort = int.TryParse(
        options.SingleOrDefault(opt => opt.Option == "UdpListenPort")?.Value, out var udpPort) ? udpPort : 0;
    o.AuthRequired = bool.TryParse(
        options.SingleOrDefault(opt => opt.Option == "AuthRequired")?.Value, out var auth) && auth;
    o.UpdateCheckEnabled = !bool.TryParse(
        options.SingleOrDefault(opt => opt.Option == "UpdateCheckEnabled")?.Value, out var uce) || uce;
    o.RoutingAlgorithm = options.SingleOrDefault(opt => opt.Option == "RoutingAlgorithm")?.Value
        is { Length: > 0 } ra
        ? ra
        : "passive-flood";
    o.ProbingEnabled = bool.TryParse(
        options.SingleOrDefault(opt => opt.Option == "ProbingEnabled")?.Value, out var probing) && probing;
    o.ProbeIntervalHours = int.TryParse(
        options.SingleOrDefault(opt => opt.Option == "ProbeIntervalHours")?.Value, out var probeInterval)
        && probeInterval > 0
        ? probeInterval
        : 24;
    o.FragmentThresholdBytes = int.TryParse(
        options.SingleOrDefault(opt => opt.Option == "FragmentThresholdBytes")?.Value, out var fragThr)
        && fragThr >= 0
        ? fragThr
        : 4096;
    o.FragmentReassemblyTimeoutSeconds = int.TryParse(
        options.SingleOrDefault(opt => opt.Option == "FragmentReassemblyTimeoutSeconds")?.Value, out var fragTimeout)
        && fragTimeout > 0
        ? fragTimeout
        : 7 * 24 * 3600;
    o.OpportunisticPollEnabled = !bool.TryParse(
        options.SingleOrDefault(opt => opt.Option == "OpportunisticPollEnabled")?.Value, out var opp) || opp;
    o.ScheduledPollEnabled = bool.TryParse(
        options.SingleOrDefault(opt => opt.Option == "ScheduledPollEnabled")?.Value, out var sched) && sched;
    o.PollIntervalHours = int.TryParse(
        options.SingleOrDefault(opt => opt.Option == "PollIntervalHours")?.Value, out var pollHours)
        && pollHours > 0
        ? pollHours
        : 6;
    o.DiscoveryAirtimeBudgetSecondsPerHour = int.TryParse(
        options.SingleOrDefault(opt => opt.Option == "DiscoveryAirtimeBudgetSecondsPerHour")?.Value, out var atb)
        && atb >= 0
        ? atb
        : 0;
    o.ProbeStrategy = Enum.TryParse<ProbeStrategy>(
        options.SingleOrDefault(opt => opt.Option == "ProbeStrategy")?.Value, ignoreCase: true, out var ps)
        ? ps
        : ProbeStrategy.FixedInterval;
    o.ProbeOvernightStartHour = int.TryParse(
        options.SingleOrDefault(opt => opt.Option == "ProbeOvernightStartHour")?.Value, out var psh)
        && psh is >= 0 and <= 23
        ? psh
        : 2;
    o.ProbeOvernightEndHour = int.TryParse(
        options.SingleOrDefault(opt => opt.Option == "ProbeOvernightEndHour")?.Value, out var peh)
        && peh is >= 0 and <= 23
        ? peh
        : 6;
    o.ProbeQuietWindowSeconds = int.TryParse(
        options.SingleOrDefault(opt => opt.Option == "ProbeQuietWindowSeconds")?.Value, out var pqws)
        && pqws > 0
        ? pqws
        : 300;
    o.HeartbeatEnabled = !bool.TryParse(
        options.SingleOrDefault(opt => opt.Option == "HeartbeatEnabled")?.Value, out var hb) || hb;
    o.HeartbeatIntervalSeconds = int.TryParse(
        options.SingleOrDefault(opt => opt.Option == "HeartbeatIntervalSeconds")?.Value, out var hbInt)
        && hbInt >= 10
        ? hbInt
        : 60;

    logger.LogInformation($"Callsign: {o.Callsign}");
    logger.LogInformation($"BPQ AGW: {o.NodeHost}:{o.AgwPort} (default port byte {o.DefaultBpqPort})");
    logger.LogInformation($"MQTT broker: localhost:{o.MqttPort}");
    logger.LogInformation($"UDP datagram listener: {(o.UdpListenPort > 0 ? $":{o.UdpListenPort}" : "disabled")}");
    logger.LogInformation($"App-interface auth required: {o.AuthRequired}");
    logger.LogInformation($"Update check: {(o.UpdateCheckEnabled ? "enabled" : "disabled")}");
    logger.LogInformation($"Routing algorithm: {o.RoutingAlgorithm}");
    logger.LogInformation($"Connected-mode probing: {(o.ProbingEnabled ? $"enabled (strategy={o.ProbeStrategy}, every {o.ProbeIntervalHours}h)" : "disabled")}");
    logger.LogInformation($"Discovery airtime budget: {(o.DiscoveryAirtimeBudgetSecondsPerHour > 0 ? $"{o.DiscoveryAirtimeBudgetSecondsPerHour}s/hour" : "unlimited")}");
});

builder.Services.AddHttpClient();

// Plan A polish — single TimeProvider injected everywhere
// cadence-sensitive code reads time. Tests substitute
// FakeTimeProvider (Microsoft.Extensions.TimeProvider.Testing) so
// `Advance(30s)` deterministically fast-forwards every service that
// uses it. Production wires the system clock.
builder.Services.AddSingleton(TimeProvider.System);

// Plan B7 — single airtime budget shared by every discovery-class
// transmission (beacons, solicit replies, probes). OutboundActivity-
// Tracker is the WhenQuiet probe-strategy oracle; the forwarder
// pings it on every successful send.
builder.Services.AddSingleton<AirtimeAccountant>();
builder.Services.AddSingleton<OutboundActivityTracker>();

// Plan C3 PR-B — operational snapshot composer used by both
// /Operational and HeartbeatPublisher. Singleton so the two
// consumers see consistent state.
builder.Services.AddSingleton<OperationalSnapshotBuilder>();
builder.Services.AddHostedService<HeartbeatPublisher>();

builder.Services.AddSingleton<UpdateChecker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateChecker>());

// Plan C5.2 — the unprivileged dapps daemon only writes the request
// marker / reads the status file. The actual update work runs in the
// privileged dapps-updater.service via `dapps --apply-update`.
builder.Services.AddSingleton<IUpdaterFileSystem, RealUpdaterFileSystem>();

builder.Services.AddSingleton<MqttBrokerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttBrokerService>());
builder.Services.AddHostedService<AgwInboundService>();
builder.Services.AddHostedService<TtlSweeperService>();
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<OptionsRepo>();
builder.Services.AddSingleton<AppTokenStore>();
builder.Services.AddSingleton<AdminPasswordStore>();
builder.Services.AddSingleton<InboundEventBus>();
builder.Services.AddSingleton<OperationalMetrics>();

// Cookie auth for the dashboard / admin endpoints. Long sliding
// expiry (90 days) — this is a sysop's home node, the cookie's
// "remember me indefinitely" by design. /AppApi/* doesn't use this
// scheme; it has its own bearer-token middleware.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "dapps.admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/Login";
        options.LogoutPath = "/Logout";
        options.ExpireTimeSpan = TimeSpan.FromDays(90);
        options.SlidingExpiration = true;
    });
builder.Services.AddSingleton<OutboundMessageManager>();
// B5 routing seam — IRoutingAlgorithm is the strategy, IRoutingContext
// is the slice of node state it reads. Two stacks shipped today;
// SystemOptions.RoutingAlgorithm picks one at startup. Both wrap
// StaticRoutingAlgorithm so manual operator overrides always win.
//
//   passive-flood (default): FloodFallbackAlgorithm →
//     PassiveLearningAlgorithm → StaticRoutingAlgorithm. Stores per-
//     destination next-hop only; floods on cold-start.
//
//   meshcore: MeshCoreLikeRoutingAlgorithm → StaticRoutingAlgorithm.
//     Stores full discovered paths in DbDiscoveredPath; subsequent
//     sends embed the route on the wire as SourceRoute.
builder.Services.AddSingleton<IRoutingContext, DatabaseRoutingContext>();
builder.Services.AddSingleton<StaticRoutingAlgorithm>();
builder.Services.AddSingleton<PassiveLearningAlgorithm>();
builder.Services.AddSingleton<IRoutingAlgorithm>(sp =>
{
    var optsValue = sp.GetRequiredService<IOptionsMonitor<SystemOptions>>().CurrentValue;
    var lf = sp.GetRequiredService<ILoggerFactory>();
    var startupLog = sp.GetRequiredService<ILogger<Program>>();
    var staticAlg = sp.GetRequiredService<StaticRoutingAlgorithm>();
    switch ((optsValue.RoutingAlgorithm ?? "passive-flood").ToLowerInvariant())
    {
        case "meshcore":
            startupLog.LogInformation("Routing stack: MeshCoreLikeRoutingAlgorithm → StaticRoutingAlgorithm");
            return new MeshCoreLikeRoutingAlgorithm(staticAlg, lf.CreateLogger<MeshCoreLikeRoutingAlgorithm>());
        case "passive-flood":
        default:
            // Unknown values fall through to the safe default rather
            // than failing startup — operators editing the option by
            // hand will see a recognisable algorithm running and a
            // log line they can grep for.
            if (!string.Equals(optsValue.RoutingAlgorithm, "passive-flood", StringComparison.OrdinalIgnoreCase))
            {
                startupLog.LogWarning(
                    "Unknown RoutingAlgorithm '{0}'; falling back to passive-flood",
                    optsValue.RoutingAlgorithm);
            }
            startupLog.LogInformation("Routing stack: FloodFallback → PassiveLearning → Static");
            return new FloodFallbackAlgorithm(
                sp.GetRequiredService<PassiveLearningAlgorithm>(),
                lf.CreateLogger<FloodFallbackAlgorithm>());
    }
});
// Auto-forwarder: ticks DoRun on a short cadence so submitted messages
// move without a manual /Message/dorun poke. Manual poke still works.
builder.Services.AddHostedService<OutboundForwarderService>();
builder.Services.AddSingleton<IDappsOutboundTransport>(sp =>
{
    var opts = sp.GetRequiredService<IOptionsMonitor<SystemOptions>>().CurrentValue;
    var lf = sp.GetRequiredService<ILoggerFactory>();
    return new AgwOutboundTransport(opts.NodeHost, opts.AgwPort, lf);
});
// Order of registration matters: OutboundMessageManager picks the first
// IDappsBackhaul whose CanHandle returns true for the route. UDP wins
// when the neighbour has a UdpEndpoint set; AGW handles everything else.
builder.Services.AddSingleton<UdpDatagramBackhaul>(sp =>
    new UdpDatagramBackhaul(sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IDappsBackhaul>(sp => sp.GetRequiredService<UdpDatagramBackhaul>());
builder.Services.AddSingleton<IDappsBackhaul>(sp => new Dappsv1SessionBackhaul(
    sp.GetRequiredService<IDappsOutboundTransport>(),
    sp.GetRequiredService<ILoggerFactory>(),
    // F3 opportunistic poll: hand the backhaul the inbox so it can
    // deliver any messages the remote has queued for us, plus a
    // live read of the operator toggle (re-checked per push so a
    // /Config flip takes effect on the next session).
    opportunisticInbox: sp.GetRequiredService<IBackhaulInbox>(),
    opportunisticEnabled: () => sp.GetRequiredService<IOptionsMonitor<SystemOptions>>().CurrentValue.OpportunisticPollEnabled));
builder.Services.AddSingleton<IBackhaulInbox, DatabaseAndMqttInbox>();
builder.Services.AddHostedService<UdpDatagramListener>();

// B6.1 — connected-mode probe-and-map. NodeProber is stateless and
// reuses the singleton AGW transport; the scheduler drives it on a
// slow cadence when SystemOptions.ProbingEnabled is true.
builder.Services.AddSingleton<NodeProber>();
builder.Services.AddSingleton<ProbeSchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProbeSchedulerService>());

// F3b — scheduled poll. NodePoller is stateless, opens a session,
// drains via rev. The scheduler walks neighbours when
// SystemOptions.ScheduledPollEnabled is true (off by default).
builder.Services.AddSingleton<NodePoller>();
builder.Services.AddSingleton<PollSchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PollSchedulerService>());

// DiscoveryService constructs its bearers itself in StartAsync rather
// than receiving them via IEnumerable<IDiscoveryBearer>, so the bearer
// factory's SystemOptions read happens after the host is fully running.
//
// Registered as a singleton so the /DiscoveryChannels controller can
// inject it for B6.2 on-demand solicits. AddHostedService binds the
// same instance to the host lifecycle.
builder.Services.AddSingleton<DiscoveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DiscoveryService>());
builder.Services.AddLogging(logging =>
{
    logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.UseUtcTimestamp = true;
        options.TimestampFormat = "HH:mm:ss.fff ";
    });
});
var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<BearerAuthMiddleware>();
app.UseMiddleware<AdminAuthMiddleware>();
app.MapControllers();
app.MapRazorPages();

try
{
    app.Run();
}
catch (Exception ex) when (IsFatalConfigError(ex))
{
    // Operationally-fatal config errors that won't fix themselves on
    // restart. Exit with code 78 — paired with
    // RestartPreventExitStatus=78 in the systemd unit (see
    // scripts/dapps.service) so systemd stops the crash-loop and
    // surfaces the actionable journal message instead. The host has
    // already logged a critical line via MqttBrokerService /
    // similar; we just translate the exit code.
    return 78;
}
return 0;

static bool IsFatalConfigError(Exception ex)
{
    // Walk the inner-exception chain — the host's RunAsync wraps
    // service-startup exceptions, so the SocketException can be one
    // or two levels deep.
    for (var e = ex; e is not null; e = e.InnerException)
    {
        if (e is SocketException se && se.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return true;
        }
    }
    return false;
}
