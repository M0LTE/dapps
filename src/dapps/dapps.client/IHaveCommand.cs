using System.Text;

namespace dapps.client;

public class IHaveCommand
{
    public DappsMessage Message { get; init; } = new();

    /// <summary>
    /// F1 end-to-end source tracking: the originating callsign. Optional.
    /// Set on outbound submissions and preserved across re-forwards;
    /// receivers expose it as the <c>dapps-origin</c> MQTT user property.
    /// </summary>
    public string? Originator { get; init; }

    /// <summary>Opt-in ordering: stream id (per sender), monotonic
    /// sequence, and gap timeout in seconds (0 = strict, &gt;0 = skip
    /// gap after N seconds). All three travel together or all three
    /// are absent.</summary>
    public string? StreamId { get; init; }
    public uint? StreamSeq { get; init; }
    public uint? StreamGapTimeoutSeconds { get; init; }

    public static string Checksum(string ihave) =>
        Crc16CcittFalse.ComputeHex(Encoding.UTF8.GetBytes(ihave));

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"ihave {Message.Id} len={Message.Payload.Length} fmt={(Message.Format == DappsMessage.MessageFormat.Deflate ? 'd' : 'p')} s={Message.Salt}");
        if (!string.IsNullOrEmpty(Originator))
        {
            sb.Append($" src={Originator}");
        }
        if (!string.IsNullOrEmpty(StreamId) && StreamSeq.HasValue && StreamGapTimeoutSeconds.HasValue)
        {
            sb.Append($" sid={StreamId} sn={StreamSeq} gt={StreamGapTimeoutSeconds}");
        }
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
