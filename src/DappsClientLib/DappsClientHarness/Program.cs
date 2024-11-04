using DappsClientLib;
using Microsoft.Extensions.Logging;
using System.Text;

try
{
    var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

    var logger = loggerFactory.CreateLogger<Program>();

    var client = new DappsFbbClient("debian1", 8011, loggerFactory);

    if (!await client.Login("telnetuser", "telnetpass"))
    {
        logger.LogError("Failed to log in");
        return;
    }

    logger.LogInformation("Logged in successfully");

    if (!await client.ConnectToDappsInstance(["C 1 q0bbb", "DAPPS"]))
    {
        logger.LogError("Failed to connect to DAPPS instance");
        return;
    }

    logger.LogInformation("Connected to remote DAPPS instance");

    var payload = Encoding.UTF8.GetBytes("hello world");
    var timestamp = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
    var id = DappsMessage.ComputeHash(payload, timestamp);

    if (!await client.OfferMessage(id: id, timestamp: timestamp, destination: "testqueue@gb7rdg", len: payload.Length))
    {
        logger.LogError("Failed to offer message with ID {id}", id);
        return;
    }

    logger.LogInformation("Offered message with ID {id}", id);

    if (!await client.SendMessage(id, payload: "Hello, World!"))
    {
        logger.LogError("Failed to send message with ID {id}", id);
    }

    logger.LogInformation("Sent message with ID {id}", id);

}
finally
{
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey(true);
}