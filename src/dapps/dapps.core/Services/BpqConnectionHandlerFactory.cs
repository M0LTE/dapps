using System.Net.Sockets;

namespace dapps.core.Services;

public class BpqConnectionHandlerFactory(ILoggerFactory loggerFactory)
{
    internal BpqConnectionHandler Create(TcpClient tcpClient)
    {
        return new BpqConnectionHandler(tcpClient, loggerFactory);
    }
}
