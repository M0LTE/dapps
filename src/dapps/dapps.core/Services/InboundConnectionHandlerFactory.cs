using Microsoft.Extensions.Options;
using System.Net.Sockets;
using dapps.core.Models;

namespace dapps.core.Services;

public class InboundConnectionHandlerFactory(
    ILoggerFactory loggerFactory,
    Database database,
    MqttBrokerService mqtt,
    IOptionsMonitor<SystemOptions> options)
{
    internal InboundConnectionHandler Create(TcpClient tcpClient)
    {
        return new InboundConnectionHandler(tcpClient, loggerFactory, database, mqtt, options);
    }
}
