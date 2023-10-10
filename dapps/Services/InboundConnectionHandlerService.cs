using dapps.DataAccess;
using dapps.Models;
using System.Diagnostics;
using System.Net.Sockets;

namespace dapps.Services;

/// <summary>
/// Accept connections from BPQ or a BPQ-like service, extract commands from them, and orchestrate running them
/// </summary>
internal class InboundConnectionHandlerService
{
    private readonly ILogger<InboundConnectionHandlerService> logger;
    private readonly MessagesTableRepository messagesTableRepository;
    private readonly NodeConnectionsManager nodeConnectionsManager;

    public InboundConnectionHandlerService(ILogger<InboundConnectionHandlerService> logger, MessagesTableRepository messagesTableRepository, NodeConnectionsManager nodeConnectionsManager)
    {
        this.logger = logger;
        this.messagesTableRepository = messagesTableRepository;
        this.nodeConnectionsManager = nodeConnectionsManager;
    }

    public async Task Handle(string connectedStation, NetworkStream stream, CancellationToken stoppingToken)
    {
        try
        {
            var streamWriter = new StreamWriter(stream) { AutoFlush = true };
            var streamReader = new StreamReader(stream);
            var binaryReader = new BinaryReader(stream);

            streamWriter.WriteNewline("This is DAPPS");

            while (true)
            {
                (DappsCommandType messageType, string[]? parameters) = ParseCommand(stream);

                if (messageType == DappsCommandType.Invalid)
                {
                    logger.LogInformation("Unrecognised command rejected");
                }
                else if (messageType == DappsCommandType.Message && parameters?.Length == 2 && int.TryParse(parameters[1], out var messageLength) && messageLength > 0)
                {
                    await HandleMessageCommand(stream, messageLength, connectedStation, destCallsign: parameters![0]);
                }
            }
        }
        catch (DappsProtocolException ex)
        {
            logger.LogInformation(ex.Message + ", connection ending.");
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    private async Task HandleMessageCommand(Stream stream, int messageLength, string receivedFrom, string destCallsign)
    {
        var streamWriter = new StreamWriter(stream) { AutoFlush = true };
        var binaryReader = new BinaryReader(stream);

        streamWriter.WriteNewline("OK");

        var bytes = binaryReader.ReadBytes(messageLength);

        DappsMessage request = DappsMessageFormatter.FromOnAirFormat(bytes);

        try
        {
            await messagesTableRepository.Save(request.Timestamp, request.SourceCall, request.AppName, request.Payload);
            await nodeConnectionsManager.SignalMessageReceivedFor(destCallsign);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not save, sending back ERROR");
            streamWriter.WriteNewline("ERROR");
            return;
        }

        logger.LogInformation("Saved, sending back OK");
        streamWriter.WriteNewline($"MSG OK");
    }

    private static (DappsCommandType messageType, string[]? parameters) ParseCommand(Stream stream)
    {
        var streamReader = new StreamReader(stream);
        var streamWriter = new StreamWriter(stream) { AutoFlush = true };

        if (!streamReader.WaitToReceive("\n", TimeSpan.FromSeconds(60), out var received))
        {
            throw new DappsProtocolException("Timeout waiting for command");
        }

        var parts = received.Trim().Split(" ");
        var command = parts[0];

        switch (command)
        {
            case "MSG" when parts.Length == 3:
                return (DappsCommandType.Message, parts[1..3]);
            case "CANFWD" when parts.Length == 2:
                return (DappsCommandType.ForwardingClaim, parts[1..2]);
            case "NOFWD" when parts.Length == 2:
                return (DappsCommandType.ForwardingDisclaim, parts[1..2]);
        }

        streamWriter.WriteNewline("EH?");
        return (DappsCommandType.Invalid, default);
    }

    public enum DappsCommandType
    {
        Invalid,
        Message,
        ForwardingClaim,
        ForwardingDisclaim
    }
}

internal class DappsProtocolException : Exception
{
    public DappsProtocolException(string message) : base(message)
    {
    }
}
