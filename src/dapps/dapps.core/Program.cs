using dapps.core.Models;
using dapps.core.Services;
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
    o.FbbPort = int.Parse(options.Single(o => o.Option == "FbbPort").Value);
    o.FbbUser = options.Single(o => o.Option == "FbbUser").Value;
    o.FbbPassword = options.Single(o => o.Option == "FbbPassword").Value;
    o.Callsign = options.Single(o => o.Option == "Callsign").Value;

    logger.LogInformation($"Callsign: {o.Callsign}");
    logger.LogInformation($"BPQ node: {o.FbbUser}@{o.NodeHost}:{o.FbbPort}");
});

builder.Services.AddSingleton<InboundConnectionHandlerFactory>();
builder.Services.AddHostedService<BpqConnectionListener>();
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<OptionsRepo>();
builder.Services.AddSingleton<OutboundMessageManager>();
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
