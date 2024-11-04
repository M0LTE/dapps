
using Microsoft.Extensions.Logging;
using System.Net.Sockets;

namespace DappsClientLib;

public class FbbPortClient(string host, int port, ILoggerFactory loggerFactory)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<FbbPortClient>();
    private readonly TcpClient client = new();
    protected NetworkStream? networkStream;

    public BpqSessionState State { get; private set; } = BpqSessionState.PreLogin;

    /// <summary>
    /// Connect and log in to a local BPQ node's FBB port via TCP/IP
    /// </summary>
    /// <param name="user"></param>
    /// <param name="password"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task<bool> Login(string user, string password)
    {
        if (State != BpqSessionState.PreLogin)
        {
            throw new InvalidOperationException("Already logged in");
        }

        if (0 == port)
        {
            throw new InvalidOperationException("BpqFbbPort is not configured");
        }

        logger.LogInformation("Connecting to BPQ: {user}@{node}:{port}", user, host, port);

        await client.ConnectAsync(host, port);
        client.ReceiveTimeout = 50000;
        networkStream = client.GetStream();

        await networkStream.WriteUtf8AndFlush($"{user}\r{password}\rBPQTERMTCP\r");

        var (success, _) = networkStream.Expect("Connected to TelnetServer\r"); // lies

        if (!success)
        {
            return false;
        }

        State = BpqSessionState.LoggedIn;
        return true;
    }
}

public enum BpqSessionState
{
    PreLogin, LoggedIn,
}