using Microsoft.Extensions.Logging;
using System.Text;
using static dapps.client.DappsMessage;

namespace dapps.client;

public class DappsFbbClient(string host, int port, ILoggerFactory loggerFactory)
    : FbbPortClient(host, port, loggerFactory)
{
    private bool connectedToDapps;
    private readonly ILogger logger = loggerFactory.CreateLogger<DappsFbbClient>();

    /// <summary>
    /// Execute the login sequence, expect DAPPS prompt
    /// </summary>
    /// <param name="connectScript"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public async Task<bool> ConnectToDappsInstance(string[] connectScript)
    {
        State.AssertIsLoggedInToLocalBpq();

        logger.LogInformation("Connecting to DAPPS instance using connect script: {0}", string.Join(", ", connectScript));

        foreach (var scriptLine in connectScript)
        {
            if (scriptLine.StartsWith("PAUSE ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = scriptLine.Split(" ");
                if (parts.Length == 2 && int.TryParse(parts[1], out var pause))
                {
                    await Task.Delay(pause);
                }
                else
                {
                    logger.LogWarning("Could not parse PAUSE command: '{0}'", scriptLine);
                    return false;
                }
            }
                
            await networkStream!.WriteUtf8AndFlush(scriptLine + "\r");
        }

        var (gotPrompt, _) = networkStream!.Expect("DAPPSv1>\n");

        connectedToDapps = gotPrompt;

        return gotPrompt;
    }

    /// <summary>
    /// Send ihave, expect send
    /// </summary>
    /// <param name="id"></param>
    /// <param name="timestamp"></param>
    /// <param name="destination"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public async Task<bool> OfferMessage(string id, long? timestamp, MessageFormat messageFormat, string destination, int len)
    {
        connectedToDapps.AssertTruth("Not connected to DAPPS");
        logger.LogInformation("Offering message with ID {id} to remote DAPPS...", id);
        var commandBuilder = new StringBuilder($"ihave {id} len={len} fmt={messageFormat.ToString().ToLower()[0]} dst={destination}");
        if (timestamp.HasValue)
        {
            commandBuilder.Append($" ts={timestamp}");
        }

        var command = commandBuilder.ToString();

        if (messageFormat == MessageFormat.Plain)
        {
            await networkStream!.WriteUtf8AndFlush(command + "\n");
        }
        else
        {
            throw new NotImplementedException("Deflate format not yet ported to this library");
        }

        var ihaveResponse = networkStream!.ReadUntil(new Dictionary<string, bool> { { $"send {id}\n", true } });
        return ihaveResponse;
    }

    /// <summary>
    /// Send payload, expect ACK
    /// </summary>
    /// <param name="id"></param>
    /// <param name="payload"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public async Task<bool> SendMessage(string id, byte[] payload)
    {
        connectedToDapps.AssertTruth("Not connected to DAPPS");
        logger.LogInformation("Sending message with ID {0}...", id);
        await networkStream!.WriteUtf8AndFlush("data " + id + "\n");
        await networkStream!.WriteAndFlush(payload);
        var dataResponse = networkStream!.ReadUntil(new Dictionary<string, bool> {
            { $"ack {id}", true },
            { $"bad {id}", false },
        });
        return dataResponse;
    }

    public Task Disconnect()
    {
        networkStream!.Socket.Close();
        return Task.CompletedTask;
    }
}
