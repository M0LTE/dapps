using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Options;

namespace dapps.core.Routing;

/// <summary>
/// <see cref="IRoutingContext"/> backed by the SQLite-resident node
/// state. The default and only context implementation today;
/// alternative contexts (in-memory / test stubs) plug in here for
/// algorithms that want to be exercised without a database.
/// </summary>
public sealed class DatabaseRoutingContext(
    Database database,
    IOptionsMonitor<SystemOptions> options) : IRoutingContext
{
    public string LocalCallsign => options.CurrentValue.Callsign;

    public int DefaultBpqPort => options.CurrentValue.DefaultBpqPort;

    public async Task<IReadOnlyList<DbNeighbour>> GetNeighboursAsync(CancellationToken ct)
        => (await database.GetNeighbours()).ToList();

    public async Task<IReadOnlyList<DbDiscoveredPeer>> GetDiscoveredPeersAsync(CancellationToken ct)
        => await database.GetDiscoveredPeers();

    public async Task<DbNeighbour?> ResolveRouteHintAsync(string destinationBaseCallsign, CancellationToken ct)
    {
        var hint = await database.GetRouteHint(destinationBaseCallsign)
            ?? await database.GetRouteHint("*");
        if (hint is null) return null;
        return await database.GetNeighbour(hint.NextHop);
    }

    public async Task<DbNeighbour?> GetNeighbourByCallsignAsync(string callsign, CancellationToken ct)
        => await database.GetNeighbour(callsign);

    public Task UpsertLearnedRouteAsync(string destinationBaseCallsign, string nextHopCallsign, CancellationToken ct)
        => database.UpsertLearnedRouteAsync(destinationBaseCallsign, nextHopCallsign, DateTime.UtcNow);

    public Task<DbLearnedRoute?> GetLearnedRouteAsync(string destinationBaseCallsign, CancellationToken ct)
        => database.GetLearnedRouteAsync(destinationBaseCallsign);

    public Task RecordLearnedRouteSuccessAsync(string destinationBaseCallsign, CancellationToken ct)
        => database.RecordLearnedRouteSuccessAsync(destinationBaseCallsign, DateTime.UtcNow);

    public Task<int> RecordLearnedRouteFailureAsync(string destinationBaseCallsign, int invalidationThreshold, CancellationToken ct)
        => database.RecordLearnedRouteFailureAsync(destinationBaseCallsign, invalidationThreshold);

    public Task<bool> HasSeenFloodAsync(string messageId, string linkSourceCallsign, CancellationToken ct)
        => database.HasSeenFloodAsync(messageId, linkSourceCallsign);

    public Task RecordFloodSeenAsync(string messageId, string linkSourceCallsign, CancellationToken ct)
        => database.RecordFloodSeenAsync(messageId, linkSourceCallsign, DateTime.UtcNow);

    public Task UpsertDiscoveredPathAsync(string destinationBaseCallsign, IReadOnlyList<string> intermediates, CancellationToken ct)
        => database.UpsertDiscoveredPathAsync(destinationBaseCallsign, intermediates, DateTime.UtcNow);

    public Task<DbDiscoveredPath?> GetDiscoveredPathAsync(string destinationBaseCallsign, CancellationToken ct)
        => database.GetDiscoveredPathAsync(destinationBaseCallsign);

    public Task RecordDiscoveredPathSuccessAsync(string destinationBaseCallsign, CancellationToken ct)
        => database.RecordDiscoveredPathSuccessAsync(destinationBaseCallsign, DateTime.UtcNow);

    public Task<int> RecordDiscoveredPathFailureAsync(string destinationBaseCallsign, int invalidationThreshold, CancellationToken ct)
        => database.RecordDiscoveredPathFailureAsync(destinationBaseCallsign, invalidationThreshold);
}
