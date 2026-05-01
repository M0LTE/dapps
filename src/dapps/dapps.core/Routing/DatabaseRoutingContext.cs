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
}
