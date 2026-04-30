namespace dapps.client.Discovery;

/// <summary>
/// Immutable snapshot of a discovery channel's bearer-relevant
/// configuration. Bearers receive a list of these at <see cref="IDiscoveryBearer.StartAsync"/>;
/// they decide internally how to operate (one socket monitoring N AGW
/// ports, or one UDP socket per multicast group, or one MeshCore radio
/// handle per channel).
/// </summary>
public sealed record DiscoveryChannelInfo(
    int Id,
    string Bearer,
    string ChannelKey,
    LinkClass LinkClass,
    int BeaconIntervalSeconds,
    int AdvertisedTtlSeconds,
    int CostHint);

/// <summary>
/// A beacon yielded by a bearer's listen stream, tagged with the channel
/// it arrived on. The bearer is the authority on which channel a frame
/// belongs to (it knows AGW port bytes / UDP groups / MeshCore radios);
/// the daemon only joins by <see cref="DiscoveryChannelInfo.ChannelKey"/>.
/// </summary>
public sealed record ReceivedBeacon(BeaconFrame Beacon, string ChannelKey);

/// <summary>
/// A channel over which DAPPS nodes announce themselves and overhear
/// each other's announcements. One implementation per bearer kind
/// ("agw", "udp", "meshcore"); each implementation can serve many
/// channels (BPQ port bytes / multicast groups / MeshCore radios)
/// from the same connection.
/// </summary>
public interface IDiscoveryBearer : IAsyncDisposable
{
    /// <summary>Stable identifier matching <see cref="DiscoveryChannelInfo.Bearer"/>.</summary>
    string Name { get; }

    /// <summary>
    /// Bring the bearer online with the given channel set. The bearer
    /// is responsible for opening whatever sockets / radios it needs
    /// and starting any internal read loops. Idempotent — can be
    /// called once at startup.
    /// </summary>
    Task StartAsync(IReadOnlyList<DiscoveryChannelInfo> channels, CancellationToken ct);

    /// <summary>Emit a beacon on the named channel. The daemon
    /// schedules these per channel according to the channel's
    /// <see cref="DiscoveryChannelInfo.BeaconIntervalSeconds"/>.</summary>
    Task AnnounceAsync(BeaconFrame beacon, string channelKey, CancellationToken ct);

    /// <summary>Stream of beacons heard on any of this bearer's
    /// channels, each tagged with the channel key it arrived on.</summary>
    IAsyncEnumerable<ReceivedBeacon> ListenAsync(CancellationToken ct);
}
