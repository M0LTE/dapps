using System.Globalization;
using System.Text;

namespace dapps.client.Discovery;

/// <summary>
/// Encodes and parses the beacon wire form. Plan B2 sketches a positional
/// line; we use the same KV style as <c>ihave</c> instead - both for
/// extensibility (future fields don't reorder existing parsers) and so
/// hand-typing or grepping captured AX.25 UI traffic stays trivial.
///
/// Wire form:
/// <code>
///   DAPPS v1 callsign=M0LTE-9 hops=0 ttl=300
/// </code>
///
/// One line, no trailing newline. ASCII-only on the wire - callsigns
/// are ASCII anyway and the KV values are integers.
///
/// The bearer-specific source hint (<see cref="BeaconFrame.Bearer"/>) is
/// NOT carried in the wire form - it's stamped by the receive bearer
/// based on the channel the beacon arrived on. Including it on the wire
/// would let a misbehaving peer claim to be reachable on routes it
/// isn't.
/// </summary>
public static class BeaconCodec
{
    public const string Magic = "DAPPS v1";

    public static byte[] Encode(BeaconFrame beacon)
    {
        var line = $"{Magic} callsign={beacon.Callsign} hops={beacon.Hops} ttl={beacon.Ttl}";
        return Encoding.ASCII.GetBytes(line);
    }

    public static bool TryParse(ReadOnlySpan<byte> payload, BeaconBearerHint bearer, out BeaconFrame? beacon)
    {
        beacon = null;
        if (payload.Length < Magic.Length) return false;

        var text = Encoding.ASCII.GetString(payload).TrimEnd('\r', '\n');
        if (!text.StartsWith(Magic + " ", StringComparison.Ordinal)) return false;

        var rest = text[(Magic.Length + 1)..];
        string? callsign = null;
        int? hops = null;
        int? ttl = null;

        foreach (var token in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0 || eq == token.Length - 1) return false;
            var key = token[..eq];
            var value = token[(eq + 1)..];
            switch (key)
            {
                case "callsign":
                    callsign = value;
                    break;
                case "hops":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var h) || h < 0) return false;
                    hops = h;
                    break;
                case "ttl":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var t) || t <= 0) return false;
                    ttl = t;
                    break;
                // Unknown keys ignored - forward-compat with future fields.
            }
        }

        if (callsign is null || hops is null || ttl is null) return false;
        if (callsign.Length == 0) return false;

        beacon = new BeaconFrame(callsign, hops.Value, ttl.Value, bearer);
        return true;
    }
}
