using System.Security.Cryptography;
using System.Text;

namespace dapps.client;

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
        var sb = new StringBuilder();
        sb.Append($"ihave {Message.Id} len={Message.Payload.Length} fmt={(Message.Format == DappsMessage.MessageFormat.Deflate ? 'd' : 'p')} ts={Message.Timestamp}");
        if (Message.Kvps.Count > 0)
        {
            sb.Append($" {string.Join(" ", Message.Kvps.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
        }

        sb.Append($" dst={Message.Destination}");

        var chk = Checksum(sb.ToString());
        sb.Append($" chk={chk}");
        var msg = sb.ToString();
        return msg;
    }
}
