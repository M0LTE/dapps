using dapps.client.Backhaul;
using dapps.client.Backhaul.Datagram;
using dapps.client.Discovery;
using dapps.client.Transport;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
// OpenAPI / Scalar dropped in the .NET 8 rollback — the native
// OpenAPI generation (AddOpenApi / MapOpenApi) is a .NET 9+ API.
// To revisit once we're back on a newer .NET runtime.

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddRazorPages();
builder.Services.AddHostedService<DbStartup>();
builder.Services.AddOptions<SystemOptions>().Configure<OptionsRepo, ILogger<SystemOptions>>(async (o, db, logger) =>
{
    var options = await db.GetOptions();
    o.NodeHost = options.Single(o => o.Option == "NodeHost").Value;
    o.AgwPort = int.Parse(options.Single(o => o.Option == "AgwPort").Value);
    o.DefaultBpqPort = int.Parse(options.Single(o => o.Option == "DefaultBpqPort").Value);
    o.Callsign = options.Single(o => o.Option == "Callsign").Value;
    o.MqttPort = int.Parse(options.Single(o => o.Option == "MqttPort").Value);
    o.UdpListenPort = int.TryParse(
        options.SingleOrDefault(opt => opt.Option == "UdpListenPort")?.Value, out var udpPort) ? udpPort : 0;
    o.AuthRequired = bool.TryParse(
        options.SingleOrDefault(opt => opt.Option == "AuthRequired")?.Value, out var auth) && auth;

    logger.LogInformation($"Callsign: {o.Callsign}");
    logger.LogInformation($"BPQ AGW: {o.NodeHost}:{o.AgwPort} (default port byte {o.DefaultBpqPort})");
    logger.LogInformation($"MQTT broker: localhost:{o.MqttPort}");
    logger.LogInformation($"UDP datagram listener: {(o.UdpListenPort > 0 ? $":{o.UdpListenPort}" : "disabled")}");
    logger.LogInformation($"App-interface auth required: {o.AuthRequired}");
});

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
builder.Services.AddSingleton<IDappsBackhaul, Dappsv1SessionBackhaul>();
builder.Services.AddSingleton<IBackhaulInbox, DatabaseAndMqttInbox>();
builder.Services.AddHostedService<UdpDatagramListener>();

// DiscoveryService constructs its bearers itself in StartAsync (rather
// than receiving them via IEnumerable<IDiscoveryBearer>), because the
// bearer factory needs to dereference SystemOptions, which queries the
// systemoptions table — which DbStartup hasn't created at the moment
// the DI container materialises hosted services.
builder.Services.AddHostedService<DiscoveryService>();
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

app.Run();
