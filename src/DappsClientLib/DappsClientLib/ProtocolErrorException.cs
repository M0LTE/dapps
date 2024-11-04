namespace DappsClientLib;

public class ProtocolErrorException(string? message) : Exception(message)
{
    public ProtocolErrorException(string? message, string bufferContents) : this(message)
    {
        BufferContents = bufferContents;
    }

    public string? BufferContents { get; }
}