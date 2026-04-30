using SQLite;

namespace dapps.core.Models;

/// <summary>
/// One row per (callsign, bearer) combination. The same peer can be
/// heard on multiple bearers — e.g. a node N0BBB-9 reachable both via
/// AGW (BPQ port 2) and UDP multicast (10.0.0.5:1880) — and we record
/// each so the router can pick the bearer that's right for the
/// destination.
/// </summary>
[Table("discoveredpeers")]
public sealed class DbDiscoveredPeer
{
    /// <summary>Composite key formed as <c>{Callsign}|{Bearer}</c>.
    /// SQLite-net doesn't do composite primary keys cleanly, so we
    /// synthesise one. Callers should use
    /// <see cref="MakeKey"/> rather than building it by hand.</summary>
    [PrimaryKey]
    public string PeerKey { get; set; } = "";

    [Indexed]
    public string Callsign { get; set; } = "";

    /// <summary>One of "agw", "udp" (matches
    /// <c>BeaconBearerHint.Kind</c>).</summary>
    public string Bearer { get; set; } = "";

    public int Hops { get; set; }

    /// <summary>Freshness window in seconds the originator advertised.
    /// The discovery service ages the row out when
    /// <c>now - LastSeen &gt; TtlSeconds</c>.</summary>
    public int TtlSeconds { get; set; }

    /// <summary>BPQ port byte the beacon arrived on (only meaningful
    /// when <see cref="Bearer"/> is "agw").</summary>
    public int? BpqPort { get; set; }

    /// <summary>UDP host:port the beacon datagram was sourced from
    /// (only meaningful when <see cref="Bearer"/> is "udp").</summary>
    public string? UdpEndpoint { get; set; }

    public DateTime LastSeen { get; set; }

    public static string MakeKey(string callsign, string bearer)
        => $"{callsign}|{bearer}";
}
