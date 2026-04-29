using System.Buffers.Binary;
using System.Security.Cryptography;

namespace dapps.client;

public class DappsMessage
{
    public string Id => ComputeHash(Payload, Salt)[..7];

    public Dictionary<string, string> Kvps = [];
    public string Destination { get; init; } = "";
    public long? Salt { get; init; }
    public byte[] Payload { get; init; } = [];

    public MessageFormat Format { get; init; } = MessageFormat.Plain;

    public enum MessageFormat
    {
        Deflate,
        Plain
    }

    /// <summary>
    /// Compute the SHA1 of (8-byte LE salt prefix when supplied) followed by
    /// payload bytes, rendered as lowercase hex. Callers slice the first 7
    /// chars for the wire message id.
    /// </summary>
    public static string ComputeHash(byte[] data, long? salt)
    {
        byte[] toHash;
        if (salt != null)
        {
            var saltBytes = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(saltBytes, salt.Value);
            toHash = [.. saltBytes, .. data];
        }
        else
        {
            toHash = data;
        }
        byte[] hashBytes = SHA1.HashData(toHash);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
