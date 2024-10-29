using System.Net.Sockets;

namespace dapps.core.Services;

public class InboundConnectionHandlerFactory(ILoggerFactory loggerFactory, Database database)
{
    internal InboundConnectionHandler Create(TcpClient tcpClient)
    {
        return new InboundConnectionHandler(tcpClient, loggerFactory, database);
    }
}
