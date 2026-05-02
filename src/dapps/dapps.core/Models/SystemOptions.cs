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
    /// When true, dapps periodically polls the GitHub Releases API and
    /// surfaces "v0.X.Y available" in the dashboard. Outbound HTTPS
    /// only; no operator-identifying data leaks (User-Agent is just
    /// <c>dapps/&lt;version&gt;</c>). Set to false for nodes that are
    /// firewalled off the public internet, or to opt out of the
    /// notification entirely. Plan C5.1.
    /// </summary>
    public bool UpdateCheckEnabled { get; set; } = true;

    /// <summary>
    /// Plan B6.1 — connected-mode probe-and-map. When true, the
    /// <c>ProbeSchedulerService</c> walks known peers (manual
    /// neighbours + AGW-bearer discovered peers, less opt-outs) on
    /// a slow cadence and records reachability in
    /// <see cref="dapps.core.Models.DbProbedNode"/>. Off by default —
    /// probing uses other operators' airtime, so opt-in.
    /// </summary>
    public bool ProbingEnabled { get; set; } = false;

    /// <summary>
    /// Interval between full sweeps when <see cref="ProbingEnabled"/>
    /// is true. Default 24 hours — once a day is enough to spot a
    /// neighbour going dark without saturating slow links.
    /// </summary>
    public int ProbeIntervalHours { get; set; } = 24;

    /// <summary>
    /// Plan F2 — payloads strictly larger than this byte count are
    /// fragmented into chunks at submit time. The originator splits
    /// a 50 KB payload into ⌈50 KB / threshold⌉ rows; the receiver
    /// reassembles. End-to-end (intermediate hops forward fragments
    /// as opaque single messages). Bearers do their own framing
    /// underneath, so this knob is about *resumability* — the unit of
    /// retransmission after a link drop or crash mid-transfer — not
    /// about MTU adaptation. 0 disables fragmentation entirely.
    /// </summary>
    public int FragmentThresholdBytes { get; set; } = 4096;

    /// <summary>
    /// Plan F2 — drop incomplete reassembly buffer rows older than
    /// this. Default 7 days because HF / mesh propagation gaps can
    /// cleanly close for multiple days mid-transmission and we'd
    /// rather hold the partial work than throw it away. Operators on
    /// always-on links can shorten this aggressively.
    /// </summary>
    public int FragmentReassemblyTimeoutSeconds { get; set; } = 7 * 24 * 3600;

    /// <summary>
    /// Plan F3 — opportunistic poll on every successful push. After
    /// <see cref="dapps.client.Backhaul.Dappsv1SessionBackhaul"/>
    /// finishes pushing a message, send <c>rev</c> on the same session
    /// to drain anything the remote has queued for us. Free in
    /// connection-time terms (the link is already up) and turns every
    /// outbound session into a bidirectional drain — the difference
    /// between "B has my mail until B can reach me" and "B has my mail
    /// until I push to B." Default true; disable for nodes that want
    /// to push without ever pulling.
    /// </summary>
    public bool OpportunisticPollEnabled { get; set; } = true;

    /// <summary>
    /// Which routing algorithm composition to use. Two stacks are
    /// shipped today; both wrap <see cref="dapps.core.Routing.StaticRoutingAlgorithm"/>
    /// so operator overrides always win.
    ///
    /// <list type="bullet">
    /// <item><c>passive-flood</c> (default) — AODV-flavoured passive
    ///   learning of next-hop routes from F1 src= observations,
    ///   with bounded-flood as cold-start fallback. Stores per-
    ///   destination next-hop only.</item>
    /// <item><c>meshcore</c> — DSR-style source routing with passive
    ///   discovery. Stores the full path in
    ///   <see cref="dapps.core.Models.DbDiscoveredPath"/> so
    ///   subsequent sends embed the route on the wire instead of
    ///   re-resolving at each hop. First send to an unknown
    ///   destination triggers a flood-discovery whose accumulated
    ///   TraversedHops give every transiting node a path back to
    ///   the originator.</item>
    /// </list>
    ///
    /// Algorithm choice is global per-node and applied at startup;
    /// changing requires a restart.
    /// </summary>
    public string RoutingAlgorithm { get; set; } = "passive-flood";
}
