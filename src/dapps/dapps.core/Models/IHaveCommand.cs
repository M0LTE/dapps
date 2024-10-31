using System.Security.Cryptography;
using System.Text;

namespace dapps.core.Models;

public class IHaveCommand
{
    public DappsMessage Message { get; init; } = new();

    public static string Checksum(string ihave)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(ihave));
        return BitConverter.ToString(hash).Replace("-", "").ToLower()[..2];
    }

    public override string ToString()
    {
        var ihave = $"ihave {Message.Id} len={Message.Payload.Length} fmt={(Message.Format == DappsMessage.MessageFormat.Deflate ? 'd' : 'p')} ts={Message.Timestamp} {string.Join(" ", Message.Kvps.Select(kvp => $"{kvp.Key}={kvp.Value}"))} dst={Message.Destination}";
        var chk = Checksum(ihave);
        return $"{ihave} {chk}";
    }
}
