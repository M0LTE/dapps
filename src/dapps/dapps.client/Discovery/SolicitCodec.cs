using System.Text;

namespace dapps.client.Discovery;

/// <summary>
/// Encodes and parses the solicit wire form. Same KV style as
/// <see cref="BeaconCodec"/> - both for extensibility and so an
/// operator monitoring AX.25 UI traffic can read it. Distinguished
/// from a beacon by the literal <c>solicit</c> keyword in the
/// fixed prefix.
///
/// Wire form:
/// <code>
///   DAPPS v1 solicit callsign=M0LTE-9
/// </code>
///
/// One line, no trailing newline. ASCII-only. The asker's callsign
/// is stamped on the wire - receivers use it for logging only;
/// replies always go to the channel's broadcast address (the same
/// place beacons go), not unicast back to the asker.
/// </summary>
public static class SolicitCodec
{
    public const string Magic = "DAPPS v1 solicit";

    public static byte[] Encode(SolicitFrame solicit)
    {
        var line = $"{Magic} callsign={solicit.Callsign}";
        return Encoding.ASCII.GetBytes(line);
    }

    /// <summary>
    /// Parse a solicit. Returns false if the payload doesn't lead
    /// with the solicit magic - beacons (which lead with the shorter
    /// <c>"DAPPS v1"</c> magic but have no <c>solicit</c> keyword)
    /// hit this case.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out SolicitFrame? solicit)
    {
        solicit = null;
        if (payload.Length < Magic.Length) return false;

        var text = Encoding.ASCII.GetString(payload).TrimEnd('\r', '\n');
        if (!text.StartsWith(Magic + " ", StringComparison.Ordinal)
            && !text.Equals(Magic, StringComparison.Ordinal)) return false;

        // Strict byte-for-byte: beacon parser sees "DAPPS v1 callsign="
        // and would otherwise grab a "solicit" KV field as a callsign.
        // Anchoring on "DAPPS v1 solicit " keeps the two unambiguous.
        var rest = text.Length == Magic.Length ? "" : text[(Magic.Length + 1)..];

        string? callsign = null;
        foreach (var token in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0 || eq == token.Length - 1) return false;
            var key = token[..eq];
            var value = token[(eq + 1)..];
            if (key == "callsign") callsign = value;
            // Unknown keys ignored - forward-compat.
        }

        if (string.IsNullOrEmpty(callsign)) return false;

        solicit = new SolicitFrame(callsign);
        return true;
    }
}
