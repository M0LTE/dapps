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

    public InboundConnectionHandlerService(ILogger<InboundConnectionHandlerService> logger, MessagesTableRepository messagesTableRepository)
    {
        this.logger = logger;
        this.messagesTableRepository = messagesTableRepository;
    }

    public async Task Handle(string sourceCallsign, NetworkStream stream, CancellationToken stoppingToken)
    {
        try
        {
            var streamWriter = new StreamWriter(stream) { AutoFlush = true };
            var streamReader = new StreamReader(stream);
            var binaryReader = new BinaryReader(stream);

            streamWriter.Write("This is DAPPS\n");
            //streamWriter.Flush();

            int i = 0;
            while (true)
            {
                /*var next = binaryReader.Read();
                if (next == -1)
                {
                    logger.LogInformation("Client went away");
                    return;
                }

                if (next != 'd')
                {
                    logger.LogInformation("Got unexpected character " + ((byte)next).ToHex());
                }

                logger.LogInformation("Waiting for message length");

                var messageLength = binaryReader.ReadInt16();
                logger.LogInformation("Read message length: {length}", messageLength);

                logger.LogInformation("Waiting for message type byte");

                var messageType = (DappsCommandType)binaryReader.ReadByte();

                logger.LogInformation("Message type " + messageType);

                logger.LogInformation($"Waiting for {messageLength} bytes");

                var data = binaryReader.ReadBytes(messageLength);

                logger.LogInformation("Received '{data}'", data.ToPrintableString());*/

                (DappsCommandType messageType, string[]? parameters) = ReadCommand(stream);

                if (messageType == DappsCommandType.Message)
                {
                    await HandleMessageCommand(stream, parameters![0], int.Parse(parameters[1]));
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

    private async Task HandleMessageCommand(Stream stream, string destCallsign, int messageLength)
    {
        var streamWriter = new StreamWriter(stream) { AutoFlush = true };
        var binaryReader = new BinaryReader(stream);

        streamWriter.Write("OK\n");

        var bytes = binaryReader.ReadBytes(messageLength);

        var request = DappsMessage.FromOnAirFormat(bytes);

        try
        {
            await messagesTableRepository.Save(request.Timestamp, request.SourceCall, request.AppName, request.Payload);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not save");
            logger.LogInformation("Sending back ERROR");
            streamWriter.Write("ERROR\r");
        }

        logger.LogInformation("Saved, sending back OK");
        streamWriter.Write($"MSG OK\n");
    }

    private static (DappsCommandType messageType, string[]? parameters) ReadCommand(Stream stream)
    {
        var streamReader = new StreamReader(stream);
        var streamWriter = new StreamWriter(stream) { AutoFlush = true };

        if (!streamReader.WaitToReceive("\n", TimeSpan.FromSeconds(60), out var received))
        {
            throw new DappsProtocolException("Timeout waiting for command");
        }

        var parts = received.Trim().Split(" ");
        if (parts.Length == 3 && parts[0] == "MSG")
        {
            return (DappsCommandType.Message, parts[1..3]);
        }

        streamWriter.Write("EH?\n");
        return (DappsCommandType.Invalid, default);
    }

    public enum DappsCommandType
    {
        Invalid,
        Message
    }
}

internal class DappsProtocolException : Exception
{
    public DappsProtocolException(string message) : base(message)
    {
    }
}
