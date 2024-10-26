namespace dapps.Models;

/// <summary>
/// The on-air unit of data representing the body of a MSG command
/// </summary>
public class DappsMessage
{
    public required DateTime Timestamp { get; set; }
    public required string AppName { get; set; }
    public required byte[] Payload { get; set; }
    public required string SourceCall { get; set; }
}
