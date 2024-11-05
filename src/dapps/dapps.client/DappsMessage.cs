using System.Security.Cryptography;

namespace dapps.client;

public class DappsMessage
{
    public string Id => ComputeHash(Payload, Timestamp)[..7];

    public Dictionary<string, string> Kvps = [];
    public string Destination { get; init; } = "";
    public long? Timestamp { get; init; }
    public byte[] Payload { get; init; } = [];

    public MessageFormat Format { get; init; } = MessageFormat.Plain;

    public enum MessageFormat
    {
        Deflate,
        Plain
    }

    public static string ComputeHash(byte[] data, long? timestamp)
    {
        byte[] toHash;
        if (timestamp != null)
        {
            var tsBytes = BitConverter.GetBytes(timestamp.Value);
            toHash = [.. tsBytes, .. data];
        }
        else
        {
            toHash = data;
        }
        byte[] hashBytes = SHA1.HashData(toHash);
        var str = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
        return str[..7];
    }
}