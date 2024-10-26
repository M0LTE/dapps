using dapps.DataAccess;
using dapps.Services;

namespace dapps;

public class Program
{
    public static void Main(string[] args)
    {
        IHost host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<MqttPublisher>();
                services.AddHostedService<MqttListener>();
                services.AddHostedService<BpqApplicationListener>();
                services.AddHostedService<DbStartupService>();
                services.AddSingleton<DbConnectionFactory>();
                services.AddSingleton<InboundConnectionHandlerService>();
                services.AddTransient<MessagesTableRepository>();
                services.AddSingleton<NodeConnectionsManager>();
                services.Configure<ServiceConfig>(context.Configuration);
            })
            .Build();

        host.Run();
    }
}