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
    o.Host = options.Single(o => o.Option == "NodeHost").Value;
    o.BpqFbbPort = int.Parse(options.Single(o => o.Option == "FbbPort").Value);
    o.BpqFbbUser = options.Single(o => o.Option == "FbbUser").Value;
    o.BpqFbbPassword = options.Single(o => o.Option == "FbbPassword").Value;
    o.Callsign = options.Single(o => o.Option == "Callsign").Value;

    logger.LogInformation($"Callsign: {o.Callsign}");
    logger.LogInformation($"BPQ node: {o.BpqFbbUser}@{o.Host}:{o.BpqFbbPort}");
});

builder.Services.AddSingleton<InboundConnectionHandlerFactory>();
builder.Services.AddHostedService<BpqConnectionListener>();
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<OptionsRepo>();
builder.Services.AddSingleton<OutboundMessageManager>();
builder.Services.AddSingleton<BpqFbbPortClient>();
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

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.MapScalarApiReference(options => {
        options.OpenApiRoutePattern = "../swagger/v1/swagger.json";
    });
}
app.UseAuthorization();
app.MapControllers();

app.Run();
