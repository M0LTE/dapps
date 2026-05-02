using dapps.client.Discovery;
using SQLite;

namespace dapps.core.Models;

/// <summary>
/// One row per channel a DAPPS node beacons on / listens on. A
/// "channel" is a (bearer × physical or logical sub-port) pair —
/// e.g. AGW BPQ port 1 (VHF), AGW BPQ port 3 (AXIP-UDP),
/// MeshCore radio 0 channel "default", UDP multicast group
/// 239.42.42.42:1881.
///
/// Each channel has its own beacon cadence and advertised freshness
/// window because channel media differ wildly: chattering every 5
/// minutes is fine on AXIP-UDP, antisocial on 1200-baud VHF, and
/// inappropriate on HF where propagation might only be open for a
/// few hours a day.
///
/// CostHint orders the resolver's preferences when a peer is reachable
/// on multiple channels (lower wins).
/// </summary>
[Table("discoverychannels")]
public sealed class DbDiscoveryChannel
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>"agw" | "udp" | "meshcore". The string corresponds to
    /// <see cref="IDiscoveryBearer.Name"/>; the discovery service
    /// dispatches to the bearer of this name when it operates the
    /// channel.</summary>
    [Indexed]
    public string Bearer { get; set; } = "";

    /// <summary>Bearer-specific identifier. For "agw" this is the BPQ
    /// port byte stringified ("0", "1", …). For "udp" this is a
    /// multicast endpoint ("239.42.42.42:1881"). For "meshcore" it
    /// would be a radio + channel-name composite. The bearer parses.</summary>
    public string ChannelKey { get; set; } = "";

    public LinkClass LinkClass { get; set; } = LinkClass.Unknown;

    /// <summary>Seconds between outgoing beacons. Defaults from
    /// <see cref="LinkClassDefaults.BeaconIntervalSeconds"/>; operator
    /// can override here.</summary>
    public int BeaconIntervalSeconds { get; set; }

    /// <summary>The TTL we advertise in our outgoing beacons —
    /// receivers age us out after this many seconds without hearing.
    /// Defaults from <see cref="LinkClassDefaults.AdvertisedTtlSeconds"/>.</summary>
    public int AdvertisedTtlSeconds { get; set; }

    /// <summary>Lower wins when the resolver picks a channel for
    /// forwarding. Defaults from
    /// <see cref="LinkClassDefaults.CostHint"/>.</summary>
    public int CostHint { get; set; }

    /// <summary>Disabled rows are kept (so the operator's notes /
    /// previous configuration stick) but skipped by the discovery
    /// service.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Free-form operator notes. Useful for "this is the
    /// 144.800 channel, don't beacon faster than once an hour".</summary>
    public string Notes { get; set; } = "";

    /// <summary>
    /// Plan B7 follow-up — per-channel airtime ceiling that overrides
    /// <see cref="SystemOptions.DiscoveryAirtimeBudgetSecondsPerHour"/>
    /// for transmissions tagged with this channel's key. 0 (default)
    /// means "use the global budget"; a positive value means this
    /// channel has a tighter (or looser) per-channel cap. The accountant
    /// enforces both ceilings — a transmission must fit under BOTH the
    /// per-channel and the global cap when both are set.
    /// </summary>
    public int AirtimeBudgetSecondsPerHour { get; set; } = 0;

    /// <summary>Apply <see cref="LinkClassDefaults"/> to any field still
    /// at its zero value. Lets controllers write a row with just
    /// (Bearer, ChannelKey, LinkClass) and pick up sensible defaults
    /// automatically.</summary>
    public void ApplyClassDefaults()
    {
        if (BeaconIntervalSeconds <= 0) BeaconIntervalSeconds = LinkClassDefaults.BeaconIntervalSeconds(LinkClass);
        if (AdvertisedTtlSeconds <= 0) AdvertisedTtlSeconds = LinkClassDefaults.AdvertisedTtlSeconds(LinkClass);
        if (CostHint <= 0) CostHint = LinkClassDefaults.CostHint(LinkClass);
    }
}
