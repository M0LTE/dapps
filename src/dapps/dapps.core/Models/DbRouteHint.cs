using SQLite;

namespace dapps.core.Models;

[Table("routehints")]
public class DbRouteHint
{
    [PrimaryKey]
    public string Destination { get; set; } = "";
    public string NextHop { get; set; } = "";
}

[Table("neighbours")]
public class DbNeighbour
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Unique = true)]
    public string Callsign { get; set; } = "";

    /// <summary>
    /// Optional override for which BPQ port byte (0-indexed) to use when
    /// connecting to this neighbour. Null falls back to
    /// SystemOptions.DefaultBpqPort.
    /// </summary>
    public int? BpqPort { get; set; }

    /// <summary>
    /// Optional UDP endpoint (<c>host:port</c>) for the datagram
    /// backhaul (Plan A0.4 stand-in for MeshCore-style bearers). When
    /// set, the UDP backhaul handles forwarding to this neighbour and
    /// the AGW path is not used. Null = use AGW.
    /// </summary>
    public string? UdpEndpoint { get; set; }
}
