using dapps.client.Transport;
using dapps.client.Transport.Agw;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHostedService<DbStartup>();
builder.Services.AddOptions<SystemOptions>().Configure<OptionsRepo, ILogger<SystemOptions>>(async (o, db, logger) =>
{
    var options = await db.GetOptions();
    o.NodeHost = options.Single(o => o.Option == "NodeHost").Value;
    o.AgwPort = int.Parse(options.Single(o => o.Option == "AgwPort").Value);
    o.DefaultBpqPort = int.Parse(options.Single(o => o.Option == "DefaultBpqPort").Value);
    o.Callsign = options.Single(o => o.Option == "Callsign").Value;
    o.MqttPort = int.Parse(options.Single(o => o.Option == "MqttPort").Value);

    logger.LogInformation($"Callsign: {o.Callsign}");
    logger.LogInformation($"BPQ AGW: {o.NodeHost}:{o.AgwPort} (default port byte {o.DefaultBpqPort})");
    logger.LogInformation($"MQTT broker: localhost:{o.MqttPort}");
});

builder.Services.AddSingleton<InboundConnectionHandlerFactory>();
builder.Services.AddSingleton<MqttBrokerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttBrokerService>());
builder.Services.AddHostedService<BpqConnectionListener>();
builder.Services.AddHostedService<TtlSweeperService>();
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<OptionsRepo>();
builder.Services.AddSingleton<OutboundMessageManager>();
builder.Services.AddSingleton<IDappsOutboundTransport>(sp =>
{
    var opts = sp.GetRequiredService<IOptionsMonitor<SystemOptions>>().CurrentValue;
    var lf = sp.GetRequiredService<ILoggerFactory>();
    return new AgwOutboundTransport(opts.NodeHost, opts.AgwPort, lf);
});
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

app.UseSwagger();
app.UseSwaggerUI();
app.MapScalarApiReference(options => {
    options.OpenApiRoutePattern = "../swagger/v1/swagger.json";
});
app.UseAuthorization();
app.MapControllers();

app.Run();
