namespace dapps.client.Discovery;

/// <summary>
/// A channel over which DAPPS nodes announce themselves and overhear
/// each other's announcements. Bearer-specific (AX.25 UI frames over
/// AGW; UDP multicast for LAN dev / testing; future MeshCore bcast).
/// The seam is parallel to <see cref="Backhaul.IDappsBackhaul"/> — the
/// daemon doesn't know which bearer a beacon came from beyond the
/// hint stamped on the <see cref="BeaconFrame"/>.
/// </summary>
public interface IDiscoveryBearer : IAsyncDisposable
{
    /// <summary>Short identifier (e.g. "agw", "udp-multicast") for logs
    /// and the discovered-peer table's bearer column.</summary>
    string Name { get; }

    /// <summary>
    /// Bring the bearer online. Idempotent — the daemon may call this
    /// at startup before any announce/listen activity. For AGW this
    /// opens the dedicated TCP socket and enables monitor mode; for
    /// UDP multicast it joins the group.
    /// </summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Emit our own beacon on this bearer.</summary>
    Task AnnounceAsync(BeaconFrame beacon, CancellationToken ct);

    /// <summary>
    /// Stream of beacons heard on this bearer. The bearer stamps its
    /// own <see cref="BeaconBearerHint"/> on each yielded frame.
    /// </summary>
    IAsyncEnumerable<BeaconFrame> ListenAsync(CancellationToken ct);
}
