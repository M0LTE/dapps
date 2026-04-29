namespace dapps.core.Services;

/// <summary>
/// Helpers for the `app@callsign` destination format.
/// </summary>
public static class DestinationParser
{
    public static (string app, string callsign) Parse(string destination)
    {
        var at = destination.IndexOf('@');
        if (at < 0) return ("", "");
        return (destination[..at], destination[(at + 1)..]);
    }

    /// <summary>
    /// True when the destination's callsign part matches our local callsign,
    /// SSID-insensitive (so `app@N0CALL-3` matches when our local is `N0CALL`,
    /// and vice versa).
    /// </summary>
    public static bool IsLocal(string destination, string localCallsign)
    {
        var (_, dst) = Parse(destination);
        if (dst.Length == 0) return false;
        var dstBase = dst.Split('-')[0];
        var localBase = localCallsign.Split('-')[0];
        return string.Equals(dstBase, localBase, StringComparison.OrdinalIgnoreCase);
    }
}
