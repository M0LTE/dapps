namespace dapps.client.Discovery;

/// <summary>
/// Coarse classification of a discovery / forwarding link's character.
/// Used both for routing-cost defaults and for cadence-policy defaults
/// — beaconing every 5 minutes is fine on AXIP, antisocial on 1200-baud
/// VHF, possibly inappropriate on a propagation-restricted HF channel.
///
/// The class is the operator's hint about *what kind of channel this is*.
/// Each class implies sensible defaults for cost / cadence / advertised
/// TTL; the operator can override any of them per-channel.
/// </summary>
public enum LinkClass
{
    Unknown = 0,

    /// <summary>Wired/internet IP — AXIP-UDP between BPQs over the
    /// Internet, IP transit, anything always-on with kbps+ of headroom.
    /// Cheap to use, beacon often, advertise a long freshness window.</summary>
    InternetIp = 1,

    /// <summary>UDP/IP multicast on a LAN segment — the cheapest channel
    /// of all when it works, but scope is one bridge domain.</summary>
    LanMulticast = 2,

    /// <summary>VHF/UHF FM packet (1200 / 9600 baud, occasionally faster).
    /// Line-of-sight inside the radio horizon, point-to-point in
    /// practice, full-duplex availability is ~always-on. Bandwidth
    /// budget is small so beaconing should be infrequent.</summary>
    VhfUhfFm = 3,

    /// <summary>HF — slow (300 baud typical for AX.25), continental to
    /// inter-continental coverage but heavily dependent on propagation
    /// and time-of-day. Treat as PART-TIME: a peer may be reachable for
    /// a few hours a day on this channel and unreachable the rest. Long
    /// advertised TTL so a peer doesn't age out between propagation
    /// windows; high cost so the resolver only picks HF when nothing
    /// faster is available.</summary>
    Hf = 4,

    /// <summary>MeshCore — slow LoRa-style mesh, unacked at the link
    /// layer, fluid topology, "first-delivery-by-flood-then-learn".
    /// Mid-range geographically (kilometres), mid-latency, the bearer
    /// itself is responsible for repetition / floor / forward decisions.
    /// DAPPS treats it as a fire-and-forget bearer with our own
    /// reliability layered on top.</summary>
    MeshCore = 5,
}

/// <summary>
/// Defaults derived from <see cref="LinkClass"/>. Operators can override
/// any of these per-channel when the row is created. The resolver and
/// the discovery service consult <see cref="DiscoveryChannelDefaults"/>
/// when channel rows leave a field unset.
/// </summary>
public static class LinkClassDefaults
{
    /// <summary>Lower number = preferred. Multiplicative weight, not
    /// strictly absolute, so future cost contributors (RSSI, recent loss
    /// rate, congestion) can multiply / add cleanly.</summary>
    public static int CostHint(LinkClass linkClass) => linkClass switch
    {
        LinkClass.LanMulticast => 1,
        LinkClass.InternetIp => 2,
        LinkClass.VhfUhfFm => 5,
        LinkClass.MeshCore => 8,
        LinkClass.Hf => 10,
        _ => 100,
    };

    /// <summary>Default seconds between our outgoing beacons on this
    /// class of channel. Internet links can chatter; RF links should
    /// not. HF gets a long interval reflecting propagation timescales.</summary>
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
