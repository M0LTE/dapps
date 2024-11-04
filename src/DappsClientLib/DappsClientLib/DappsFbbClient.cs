using Microsoft.Extensions.Logging;

namespace DappsClientLib;

public class DappsFbbClient(string host, int port, ILoggerFactory loggerFactory)
    : FbbPortClient(host, port, loggerFactory)
{
    private bool connectedToDapps;

    /// <summary>
    /// Execute the login sequence, expect DAPPS prompt
    /// </summary>
    /// <param name="connectScript"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public async Task<bool> ConnectToDappsInstance(string[] connectScript)
    {
        State.AssertIsLoggedInToLocalBpq();

        foreach (var scriptLine in connectScript)
        {
            await networkStream!.WriteUtf8AndFlush(scriptLine);
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
    public async Task<bool> OfferMessage(string id, long timestamp, string destination, int len)
    {
        connectedToDapps.AssertTruth("Not connected to DAPPS");
        var command = $"ihave {id} len={len} fmt=p ts={timestamp} dst={destination}";
        await networkStream!.WriteUtf8AndFlush(command);
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
    public async Task<bool> SendMessage(string id, string payload)
    {
        connectedToDapps.AssertTruth("Not connected to DAPPS");
        await networkStream!.WriteUtf8AndFlush("data " + id + "\n");
        await networkStream!.WriteUtf8AndFlush(payload);
        var dataResponse = networkStream!.ReadUntil(new Dictionary<string, bool> {
                { $"ack {id}", true },
                { $"nack {id}", false },
            });
        return dataResponse;
    }
}
