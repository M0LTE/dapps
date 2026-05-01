using System.Buffers.Binary;
using System.Text;

namespace dapps.client.Backhaul.Datagram;

/// <summary>
/// Self-describing binary codec for <see cref="BackhaulMessage"/>. Used
/// by datagram-shaped bearers (UDP today, MeshCore Companion / KISS
/// later) where the streamed DAPPSv1 <c>ihave</c>/<c>data</c> exchange
/// doesn't fit. Stays in <c>dapps.client</c> so any bearer impl can
/// reuse it without taking a dependency on the AGW path.
///
/// Wire format (all integers little-endian). Version 2 (current) adds
/// the <c>src=</c> originator field for F1 end-to-end source tracking;
/// version 1 messages still decode (Originator is always null).
/// <code>
///   [1]  version           = 2 (encoder); decoder also accepts 1
///   [1]  flags             bit0=salt, bit1=ttl, bit2=headers, bit3=originator (v2+)
///   [7]  id (UTF-8 ASCII, 7-char hex from DappsMessage.ComputeHash)
///   [8]  salt              (only when flags bit0)
///   [4]  ttl seconds       (only when flags bit1)
///   [2]  destination len
///   [N]  destination       (UTF-8)
///   [2]  originator len    (only when flags bit3, v2+)
///   [O]  originator        (UTF-8, v2+)
///   [2]  headers count     (only when flags bit2)
///   per header:
///     [2] key len, [K] key (UTF-8), [2] value len, [V] value (UTF-8)
///   [4]  payload len
///   [P]  payload bytes
/// </code>
/// Length-prefixed throughout — no escapes, binary-safe.
/// </summary>
public static class BackhaulMessageCodec
{
    /// <summary>Version this encoder writes. Decoder accepts both v1 and v2.</summary>
    public const byte Version = 2;
    public const int IdLength = 7;

    [Flags]
    private enum Flags : byte
    {
        None = 0,
        HasSalt = 1 << 0,
        HasTtl = 1 << 1,
        HasHeaders = 1 << 2,
        HasOriginator = 1 << 3,     // v2+
    }

    public static byte[] Encode(BackhaulMessage message)
    {
        if (message.Id.Length != IdLength)
        {
            throw new ArgumentException($"id must be exactly {IdLength} characters; got '{message.Id}'", nameof(message));
        }

        var idBytes = Encoding.ASCII.GetBytes(message.Id);
        var dstBytes = Encoding.UTF8.GetBytes(message.Destination);
        var origBytes = string.IsNullOrEmpty(message.Originator)
            ? []
            : Encoding.UTF8.GetBytes(message.Originator!);
        var headerBytes = message.Headers is { Count: > 0 }
            ? EncodeHeaders(message.Headers)
            : [];

        var flags = Flags.None;
        if (message.Salt.HasValue) flags |= Flags.HasSalt;
        if (message.Ttl.HasValue) flags |= Flags.HasTtl;
        if (headerBytes.Length > 0) flags |= Flags.HasHeaders;
        if (origBytes.Length > 0) flags |= Flags.HasOriginator;

        var size = 1 + 1 + IdLength
            + (message.Salt.HasValue ? 8 : 0)
            + (message.Ttl.HasValue ? 4 : 0)
            + 2 + dstBytes.Length
            + (origBytes.Length > 0 ? 2 + origBytes.Length : 0)
            + headerBytes.Length
            + 4 + message.Payload.Length;

        var buffer = new byte[size];
        var offset = 0;

        buffer[offset++] = Version;
        buffer[offset++] = (byte)flags;

        idBytes.CopyTo(buffer.AsSpan(offset));
        offset += IdLength;

        if (message.Salt.HasValue)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), message.Salt.Value);
            offset += 8;
        }
        if (message.Ttl.HasValue)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), message.Ttl.Value);
            offset += 4;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)dstBytes.Length);
        offset += 2;
        dstBytes.CopyTo(buffer.AsSpan(offset));
        offset += dstBytes.Length;

        if (origBytes.Length > 0)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)origBytes.Length);
            offset += 2;
            origBytes.CopyTo(buffer.AsSpan(offset));
            offset += origBytes.Length;
        }

        headerBytes.CopyTo(buffer.AsSpan(offset));
        offset += headerBytes.Length;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), (uint)message.Payload.Length);
        offset += 4;
        message.Payload.CopyTo(buffer.AsSpan(offset));

        return buffer;
    }

    public static BackhaulMessage Decode(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 1 + 1 + IdLength + 2 + 4)
        {
            throw new InvalidDataException("buffer too short for any valid backhaul message");
        }

        var offset = 0;
        var version = buffer[offset++];
        // Decoder accepts both v1 (pre-F1) and v2 (current). v1 messages
        // never set the originator-flag bit, so the decode path falls
        // through naturally without per-version branching after this.
        if (version != 1 && version != Version)
        {
            throw new InvalidDataException($"unsupported codec version {version}; expected 1 or {Version}");
        }
        var flags = (Flags)buffer[offset++];

        var id = Encoding.ASCII.GetString(buffer.Slice(offset, IdLength));
        offset += IdLength;

        long? salt = null;
        if ((flags & Flags.HasSalt) != 0)
        {
            salt = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset, 8));
            offset += 8;
        }

        int? ttl = null;
        if ((flags & Flags.HasTtl) != 0)
        {
            ttl = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4));
            offset += 4;
        }

        var dstLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
        offset += 2;
        var destination = Encoding.UTF8.GetString(buffer.Slice(offset, dstLen));
        offset += dstLen;

        string? originator = null;
        if ((flags & Flags.HasOriginator) != 0)
        {
            var origLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
            offset += 2;
            originator = Encoding.UTF8.GetString(buffer.Slice(offset, origLen));
            offset += origLen;
        }

        IReadOnlyDictionary<string, string>? headers = null;
        if ((flags & Flags.HasHeaders) != 0)
        {
            var count = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
            offset += 2;
            var dict = new Dictionary<string, string>(count);
            for (var i = 0; i < count; i++)
            {
                var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
                offset += 2;
                var key = Encoding.UTF8.GetString(buffer.Slice(offset, keyLen));
                offset += keyLen;
                var valLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));
                offset += 2;
                var val = Encoding.UTF8.GetString(buffer.Slice(offset, valLen));
                offset += valLen;
                dict[key] = val;
            }
            headers = dict;
        }

        var payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));
        offset += 4;
        var payload = buffer.Slice(offset, (int)payloadLen).ToArray();

        return new BackhaulMessage(id, destination, salt, ttl, payload, headers, originator);
    }

    private static byte[] EncodeHeaders(IReadOnlyDictionary<string, string> headers)
    {
        // Two-pass: size first, then write. Avoids list-of-byte-arrays churn.
        var pairs = headers.Select(kv => (Key: Encoding.UTF8.GetBytes(kv.Key), Val: Encoding.UTF8.GetBytes(kv.Value))).ToArray();
        var size = 2 + pairs.Sum(p => 2 + p.Key.Length + 2 + p.Val.Length);
        var buf = new byte[size];
        var off = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off, 2), (ushort)pairs.Length);
        off += 2;
        foreach (var (key, val) in pairs)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off, 2), (ushort)key.Length);
            off += 2;
            key.CopyTo(buf.AsSpan(off));
            off += key.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off, 2), (ushort)val.Length);
            off += 2;
            val.CopyTo(buf.AsSpan(off));
            off += val.Length;
        }
        return buf;
    }
}
