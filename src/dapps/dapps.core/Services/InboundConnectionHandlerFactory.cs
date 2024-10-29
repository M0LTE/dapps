using System.Net.Sockets;

namespace dapps.core.Services;

public class InboundConnectionHandlerFactory(ILoggerFactory loggerFactory)
{
    internal InboundConnectionHandler Create(TcpClient tcpClient)
    {
        return new InboundConnectionHandler(tcpClient, loggerFactory);
    }
}
