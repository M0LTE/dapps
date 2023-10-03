using System.Text;
using System.Text.Json;

namespace dapps.Models;

public class DappsMessage
{
    public required DateTime Timestamp { get; set; }
    public required string AppName { get; set; }
    public required byte[] Payload { get; set; }
    public required string SourceCall { get; set; }
}
