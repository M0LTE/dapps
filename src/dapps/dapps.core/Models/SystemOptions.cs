using SQLite;

namespace dapps.core.Models;

[Table("systemoptions")]
public class DbSystemOption
{
    [PrimaryKey]
    public string Option { get; set; } = "";
    public string Value { get; set; } = "";
}

public class SystemOptions
{
    /// <summary>Hostname or IP of the local packet node BPQ instance.</summary>
    public string NodeHost { get; set; } = "";

    /// <summary>TCP port the node's AGW listener is on (BPQ default: 8000).</summary>
    public int AgwPort { get; set; }

    /// <summary>
    /// Default BPQ port byte (0-indexed) to use when originating a connection
    /// to a neighbour DAPPS instance via AGW. Individual neighbours can
    /// override this with DbNeighbour.BpqPort.
    /// </summary>
    public int DefaultBpqPort { get; set; }

    /// <summary>This DAPPS instance's local callsign + SSID, used as `callfrom` on outbound AGW connects.</summary>
    public string Callsign { get; set; } = "";

    /// <summary>TCP port the embedded MQTT broker listens on for app-interface clients.</summary>
    public int MqttPort { get; set; }

    /// <summary>
    /// UDP port the datagram-backhaul listener binds on. Default 0 =
    /// disabled. Plan A0.4: a stand-in datagram bearer, validating the
    /// backhaul seam against fragmentation / reassembly before MeshCore
    /// lands.
    /// </summary>
    public int UdpListenPort { get; set; }

    /// <summary>
    /// When true, app-interface clients (MQTT and REST) must present a
    /// valid token; topic / endpoint scope is also enforced against the
    /// authenticated app. When false, the app interface is open to
    /// anyone reachable on those ports — fine for single-host loopback,
    /// not fine for shared nodes. Plan A4.
    /// </summary>
    public bool AuthRequired { get; set; }

    /// <summary>
    /// When true, the discovery service runs an AGW UI-frame bearer
    /// using the configured BPQ port (sends/listens on AX.25 UI frames
    /// via AGW 'M' / 'm'). Default false; opt in once the operator is
    /// happy that beaconing into the local RF is appropriate. Plan B.
    /// </summary>
    public bool AgwDiscovery { get; set; }

    /// <summary>
    /// IP multicast group ("host:port", e.g. "239.42.42.42:1881") that
    /// the UDP discovery bearer joins. Empty = disabled. Useful for
    /// LAN dev/testing — every DAPPS instance on the same subnet sees
    /// every other instance's beacons within seconds. Plan B.
    /// </summary>
    public string MulticastGroup { get; set; } = "";

    /// <summary>Seconds between beacon emissions. Default 300 (5 min)
    /// keeps a quiet test environment chatty enough for end-to-end
    /// observability without pollluting busy channels. Operators on
    /// shared RF should bump this to 1800+ before enabling AGW
    /// discovery.</summary>
    public int DiscoveryBeaconIntervalSeconds { get; set; } = 300;
}
