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
}
