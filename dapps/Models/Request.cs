using System.Text;
using System.Text.Json;

namespace dapps.Models;

public class DappsMessage
{
    public required DateTime Timestamp { get; set; }
    public required string AppName { get; set; }
    public required byte[] Payload { get; set; }
    public required string SourceCall { get; set; }

    internal static DappsMessage FromOnAirFormat(byte[] data)
    {
        var json = Encoding.UTF8.GetString(data);
        var obj = JsonSerializer.Deserialize<DappsMessage>(json);
        return obj!;
    }

    internal byte[] ToOnAirFormat()
    {
        var json = JsonSerializer.Serialize(this);
        var bytes = Encoding.UTF8.GetBytes(json);
        return bytes;
    }
}
