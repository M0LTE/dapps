using dapps.Models;
using System.Text;
using System.Text.Json;

namespace dapps.Services;

/// <summary>
/// For now these use JSON. This DEFINITELY won't be JSON on-air beyond testing.
/// </summary>
public static class DappsMessageFormatter
{
    internal static DappsMessage FromOnAirFormat(byte[] data)
    {
        var json = Encoding.UTF8.GetString(data);
        var obj = JsonSerializer.Deserialize<DappsMessage>(json);
        return obj!;
    }

    internal static byte[] ToOnAirFormat(DappsMessage message)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        return bytes;
    }
}