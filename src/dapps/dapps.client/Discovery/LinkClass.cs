namespace dapps.client.Discovery;

/// <summary>
/// Coarse classification of a discovery / forwarding link's character.
/// Used both for routing-cost defaults and for cadence-policy defaults.
///
/// DAPPS is an amateur-radio-first project: when a peer is reachable
/// over RF, that's preferred even if an internet path also exists.
/// Internet bridges are a fallback to glue isolated RF islands together
/// — useful, but not the design centre. The cost defaults below put RF
/// classes at the cheap end and IP classes at the expensive end so the
/// resolver picks the in-spirit channel automatically.
///
/// The class is the operator's hint about *what kind of channel this is*.
/// Each class implies sensible defaults for cost / cadence / advertised
/// TTL; the operator can override any of them per-channel.
/// </summary>
public enum LinkClass
{
    Unknown = 0,

    /// <summary>VHF/UHF FM packet (1200 / 9600 baud, occasionally faster).
    /// Line-of-sight inside the radio horizon, point-to-point in
    /// practice, ~always-on. The default first choice when reachable.</summary>
    VhfUhfFm = 1,

    /// <summary>MeshCore — slow LoRa-style mesh, unacked at the link
    /// layer, fluid topology, "first-delivery-by-flood-then-learn".
    /// Mid-range geographically (kilometres), mid-latency. DAPPS
    /// treats it as a fire-and-forget bearer with reliability layered
    /// on top.</summary>
    MeshCore = 2,

    /// <summary>HF — slow (300 baud typical for AX.25), continental to
    /// inter-continental coverage but heavily dependent on propagation
    /// and time-of-day. Treat as PART-TIME: a peer may be reachable for
    /// a few hours a day on this channel and unreachable the rest. Long
    /// advertised TTL so a peer doesn't age out between propagation
    /// windows.</summary>
    Hf = 3,

    /// <summary>UDP/IP multicast on a LAN segment — useful for
    /// integration testing where multiple instances live on the same
    /// box / bridge domain. Not RF, so the resolver prefers any genuine
    /// RF channel over this. Test ergonomics, not a production-routing
    /// preference.</summary>
    LanMulticast = 4,

    /// <summary>Wired / internet IP — AXIP-UDP between BPQs over the
    /// Internet, anything always-on with kbps+ of headroom. Last-resort
    /// fallback to bridge between RF islands. Cheap to operate, but
    /// designing for it would mean designing a system that works well
    /// over IP and not over the unreliable RF that's the actual
    /// project goal.</summary>
    InternetIp = 5,
}

/// <summary>
/// Defaults derived from <see cref="LinkClass"/>. Operators can override
/// any of these per-channel when the row is created. The resolver
/// consults the per-row values; the discovery service consults the
/// per-row beacon cadence + advertised TTL.
/// </summary>
public static class LinkClassDefaults
{
    /// <summary>Lower number = preferred. Ordered RF-first per the
    /// project's amateur-radio identity. Future cost contributors
    /// (RSSI, recent loss rate, congestion) can add small numbers
    /// without crossing class boundaries.</summary>
    public static int CostHint(LinkClass linkClass) => linkClass switch
    {
        LinkClass.VhfUhfFm => 1,        // RF, line-of-sight, full-time — preferred
        LinkClass.MeshCore => 3,        // RF mesh, slow but in-spirit
        LinkClass.Hf => 5,              // RF continental, propagation-locked
        LinkClass.LanMulticast => 8,    // IP, scoped to a bridge domain — testing
        LinkClass.InternetIp => 10,     // IP, last-resort bridge
        _ => 100,
    };

    /// <summary>Default seconds between our outgoing beacons on this
    /// class of channel. Reflects bandwidth and channel-sharing
    /// politeness, not preference. RF channels beacon less often than
    /// IP regardless of which we'd rather route over.</summary>
    public static int BeaconIntervalSeconds(LinkClass linkClass) => linkClass switch
    {
        LinkClass.LanMulticast => 60,
        LinkClass.InternetIp => 300,
        LinkClass.VhfUhfFm => 1800,
        LinkClass.MeshCore => 3600,
        LinkClass.Hf => 7200,
        _ => 1800,
    };

    /// <summary>Default freshness window we advertise on this class of
    /// channel. Generally 3× the beacon interval; HF is much longer
    /// because a peer may go silent for an entire propagation cycle and
    /// still be very much "there".</summary>
    public static int AdvertisedTtlSeconds(LinkClass linkClass) => linkClass switch
    {
        LinkClass.LanMulticast => 180,
        LinkClass.InternetIp => 900,
        LinkClass.VhfUhfFm => 5400,
        LinkClass.MeshCore => 10800,
        LinkClass.Hf => 86400,    // 24h — propagation closes overnight
        _ => 5400,
    };
}
