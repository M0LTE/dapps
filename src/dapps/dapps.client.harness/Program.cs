using dapps.client;
using Microsoft.Extensions.Logging;
using System.Text;

try
{
    var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options => { options.SingleLine = true; options.TimestampFormat = "HH:mm:ss.fff "; }));

    var logger = loggerFactory.CreateLogger<Program>();

    var client = new DappsFbbClient("debian1", 8011, loggerFactory);

    if (!await client.FbbLogin("telnetuser", "telnetpass"))
    {
        logger.LogError("Failed to log in to local BPQ's FBBPORT");
        return;
    }

    logger.LogInformation("Login success");

    // Need to understand the need for the pause here. Without it, BPQ says invalid command (presumably it is wrongly being interpreted by the local BPQ rather than passed to the remote one)
    if (!await client.ConnectToDappsInstance(["C 1 q0bbb", "PAUSE 1000", "DAPPS"]))   
    {
        logger.LogError("Failed to connect to remote DAPPS instance through local BPQ");
        return;
    }

    logger.LogInformation("Got remote DAPPS prompt");

    var payload = "hello world";
    var payloadBytes = Encoding.UTF8.GetBytes(payload);
    var timestamp = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
    var id = DappsMessage.ComputeHash(payloadBytes, timestamp);
    var dest = "testqueue@gb7rdg";

    if (!await client.OfferMessage(id: id, timestamp: timestamp, messageFormat: DappsMessage.MessageFormat.Plain, destination: dest, len: payload.Length))
    {
        logger.LogError("Failed to offer message with ID {id}", id);
        return;
    }

    logger.LogInformation("Offered message with ID {id}", id);

    if (!await client.SendMessage(id, payloadBytes))
    {
        logger.LogError("Failed to send message with ID {id}", id);
    }

    logger.LogInformation("Sent message with ID {id}", id);

    await client.Disconnect();
}
finally
{
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey(true);
}