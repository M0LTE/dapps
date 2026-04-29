using System.Buffers.Binary;
using System.Text;

namespace dapps.client.Transport.Agw;

/// <summary>
/// A single SV2AGW frame: a 36-byte header followed by 0..N payload bytes.
/// See linbpq/docs/protocols/apps-interface.md for the layout.
/// </summary>
public sealed record AgwFrame(
    byte Port,
    char Kind,
    byte Pid,
    string CallFrom,
    string CallTo,
    byte[] Payload)
{
    public const int HeaderLength = 36;
    public const int CallsignFieldLength = 10;

    public byte[] ToBytes()
    {
        var payload = Payload ?? [];
        var buffer = new byte[HeaderLength + payload.Length];
        WriteHeader(buffer.AsSpan(0, HeaderLength));
        if (payload.Length > 0)
        {
            payload.CopyTo(buffer.AsSpan(HeaderLength));
        }
        return buffer;
    }

    public void WriteHeader(Span<byte> buffer)
    {
        if (buffer.Length < HeaderLength)
        {
            throw new ArgumentException($"buffer must be at least {HeaderLength} bytes", nameof(buffer));
        }

        buffer.Clear();
        buffer[0] = Port;
        // bytes 1..3 filler (zero)
        buffer[4] = (byte)Kind;
        // byte 5 filler
        buffer[6] = Pid;
        // byte 7 filler
        WriteCallsign(buffer.Slice(8, CallsignFieldLength), CallFrom);
        WriteCallsign(buffer.Slice(18, CallsignFieldLength), CallTo);
        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.Slice(28, 4),
            Payload?.Length ?? 0);
        // bytes 32..35 reserved (zero)
    }

    /// <summary>
    /// Read the DataLength field from a 36-byte header without parsing the
    /// rest. Useful when you need to size the payload buffer before reading.
    /// </summary>
    public static int ReadDataLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderLength)
        {
            throw new ArgumentException($"header must be at least {HeaderLength} bytes", nameof(header));
        }
        return BinaryPrimitives.ReadInt32LittleEndian(header.Slice(28, 4));
    }

    public static AgwFrame ParseHeader(ReadOnlySpan<byte> header, byte[] payload)
    {
        if (header.Length < HeaderLength)
        {
            throw new ArgumentException($"header must be at least {HeaderLength} bytes", nameof(header));
        }

        return new AgwFrame(
            Port: header[0],
            Kind: (char)header[4],
            Pid: header[6],
            CallFrom: ReadCallsign(header.Slice(8, CallsignFieldLength)),
            CallTo: ReadCallsign(header.Slice(18, CallsignFieldLength)),
            Payload: payload);
    }

    private static void WriteCallsign(Span<byte> dest, string callsign)
    {
        var c = callsign ?? "";
        var bytes = Encoding.ASCII.GetBytes(c);
        if (bytes.Length > CallsignFieldLength)
        {
            throw new ArgumentException($"callsign too long ({bytes.Length} > {CallsignFieldLength}): '{c}'");
        }
        bytes.CopyTo(dest);
        // remaining bytes already cleared by the caller
    }

    private static string ReadCallsign(ReadOnlySpan<byte> source)
    {
        var nul = source.IndexOf((byte)0);
        var len = nul == -1 ? source.Length : nul;
        return Encoding.ASCII.GetString(source[..len]);
    }
}
