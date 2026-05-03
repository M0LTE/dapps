using System.ComponentModel;
using dapps.core.Models;
using dapps.core.Services;
using ModelContextProtocol.Server;

namespace dapps.core.Mcp;

/// <summary>
/// Plan M PR-A - read-only tools covering the "who's out there?"
/// surface: configured neighbours, peers heard via discovery, the
/// discovery-channel configuration. Wraps existing <see cref="Database"/>
/// reads; same JSON shapes the dashboard already loads.
/// </summary>
[McpServerToolType]
public sealed class DappsNetworkTools(Database database)
{
    [McpServerTool(Name = "list_neighbours")]
    [Description(
        "Manual neighbour entries - peers the operator has explicitly configured as forwarding partners. " +
        "Each row has Callsign + BpqPort (AGW path) and/or UdpEndpoint (UDP datagram path). Distinct from " +
        "discovered peers (heard via beacons but not yet wired up); see list_discovered_peers for those.")]
    public async Task<IReadOnlyList<DbNeighbour>> ListNeighboursAsync()
        => (await database.GetNeighbours()).ToList();

    [McpServerTool(Name = "list_discovered_peers")]
    [Description(
        "Peers heard via discovery beacons (B1-B4) - one row per (callsign, bearer, channel) tuple. " +
        "Includes LinkClass, CostHint, observed BPQ port or UDP endpoint, hop count, advertised TTL, last-seen " +
        "timestamp. Stale rows age out automatically per the channel's TTL. A heard peer is not necessarily " +
        "a configured neighbour - list_neighbours returns the manually-configured set.")]
    public async Task<IReadOnlyList<DbDiscoveredPeer>> ListDiscoveredPeersAsync()
        => await database.GetDiscoveredPeers();

    [McpServerTool(Name = "list_discovery_channels")]
    [Description(
        "Discovery-channel configuration: one row per (bearer, channel-key) pair the daemon listens on. " +
        "Bearer is 'agw' or 'udp'; ChannelKey is the BPQ port byte (AGW) or multicast endpoint (UDP). " +
        "Each carries beacon cadence, advertised TTL, link-class cost hint, optional per-channel airtime " +
        "budget (B7), optional scheduled-solicit interval (B6.2), enabled flag, and free-form notes.")]
    public async Task<IReadOnlyList<DbDiscoveryChannel>> ListDiscoveryChannelsAsync()
        => await database.GetDiscoveryChannels();
}
