using System.Net.Sockets;
using dapps.client.Backhaul;

namespace dapps.core.Services;

public class InboundConnectionHandlerFactory(
    ILoggerFactory loggerFactory,
    Database database,
    IBackhaulInbox inbox)
{
    internal InboundConnectionHandler Create(TcpClient tcpClient)
    {
        return new InboundConnectionHandler(tcpClient, loggerFactory, database, inbox);
    }
}
