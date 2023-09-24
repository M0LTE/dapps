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

    public async Task Handle(NetworkStream stream)
    {
        try
        {
            using var streamWriter = new StreamWriter(stream);
            using var streamReader = new StreamReader(stream);
            using var binaryReader = new BinaryReader(stream);

            var callsign = await streamReader.ReadLineAsync();
            logger.LogInformation($"Accepted connection from {callsign}");
            streamWriter.Write("This is DAPPS\r");
            streamWriter.Flush();

            int i = 0;
            while (true)
            {
                var next = binaryReader.Read();
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

                logger.LogInformation("Received '{data}'", data.ToPrintableString());

                if (messageType == DappsCommandType.Message)
                {
                    var request = DappsMessage.FromOnAirFormat(data);

                    try
                    {
                        await messagesTableRepository.Save(request.Timestamp, request.SourceCall, request.AppName, request.Payload);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Could not save");
                        logger.LogInformation("Sending back ERROR");
                        streamWriter.Write("ERROR\r");
                        streamWriter.Flush();
                    }

                    logger.LogInformation("Saved, sending back OK");
                    streamWriter.Write($"OK{++i}\r");
                    streamWriter.Flush();
                }
                else
                {
                    logger.LogInformation("Unrecognised message type " + (byte)messageType);
                    streamWriter.Write("?" + (byte)messageType + "\r");
                    streamWriter.Flush();
                }
            }
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    public enum DappsCommandType : byte
    {
        Message = 1,
    }
}
