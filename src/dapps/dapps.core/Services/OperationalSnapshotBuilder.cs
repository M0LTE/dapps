using System.Diagnostics;
using System.Net.Sockets;
using dapps.core.Models;
using dapps.core.Updater;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Plan C3 PR-B - single source of truth for the
/// "what's going on with this node?" aggregate snapshot consumed by
/// both <c>/Operational</c> (operator-facing JSON) and
/// <c>HeartbeatPublisher</c> (periodic MQTT publish).
///
/// Composes liveness probes (BPQ, MQTT), counters from
/// <see cref="OperationalMetrics"/>, queue / peer / channel /
/// neighbour counts from <see cref="Database"/>, and trailing-hour
/// airtime usage from <see cref="AirtimeAccountant"/>. Plus a
/// trimmed recent-events tail (last 20 - lighter than the dashboard's
/// full 100-entry ring; the heartbeat goes on the wire every minute
/// and shouldn't carry the full history).
/// </summary>
public sealed class OperationalSnapshotBuilder(
    IOptionsMonitor<SystemOptions> options,
    OperationalMetrics metrics,
    AirtimeAccountant airtime,
    Database database,
    TimeProvider timeProvider)
{
    private const string PlaceholderCallsign = "N0CALL";
    private const int RecentEventsInSnapshot = 20;

    public async Task<OperationalSnapshot> BuildAsync(CancellationToken ct = default)
    {
        var opts = options.CurrentValue;

        var callsignOk = !string.IsNullOrWhiteSpace(opts.Callsign)
            && !string.Equals(opts.Callsign, PlaceholderCallsign, StringComparison.OrdinalIgnoreCase);
        var bpqOk = await ProbeTcp(opts.NodeHost, opts.AgwPort, TimeSpan.FromMilliseconds(500));
        var mqttOk = await ProbeTcp("127.0.0.1", opts.MqttPort, TimeSpan.FromMilliseconds(250));

        var counters = metrics.Take();
        var pendingOutbound = await database.CountPendingOutbound();
        var undeliveredLocal = await database.CountUndeliveredLocal();
        var totalMessages = await database.CountMessages();
        var neighbours = (await database.GetNeighbours()).Count;
        var discoveredPeers = (await database.GetDiscoveredPeers()).Count;
        var discoveryChannels = (await database.GetDiscoveryChannels()).Count;

        return new OperationalSnapshot(
            Callsign: opts.Callsign,
            Version: UpdaterCli.ResolveCurrentVersion(),
            GeneratedAt: timeProvider.GetUtcNow().UtcDateTime,
            UptimeSeconds: (long)Math.Max(0, (timeProvider.GetUtcNow().UtcDateTime - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds),

            Status: (callsignOk && bpqOk && mqttOk) ? "healthy" : "degraded",
            CallsignConfigured: callsignOk,
            BpqAgwReachable: bpqOk,
            MqttBrokerUp: mqttOk,
            LastForwardSuccessAt: counters.LastForwardSuccessAt,

            ForwardAttempts: counters.ForwardAttempts,
            ForwardSuccess: counters.ForwardSuccess,
            ForwardFailure: counters.ForwardFailure,
            TtlExpiredDrops: counters.TtlExpiredDrops,
            NoRouteSkips: counters.NoRouteSkips,
            AgwReconnects: counters.AgwReconnects,
            AgwLastReconnectAt: counters.AgwLastReconnectAt,
            InboundConnects: counters.InboundConnects,
            HashMismatches: counters.HashMismatches,
            ProbeAttempts: counters.ProbeAttempts,
            ProbeSuccess: counters.ProbeSuccess,
            ProbeFailure: counters.ProbeFailure,
            PollAttempts: counters.PollAttempts,
            PollSuccess: counters.PollSuccess,
            PollFailure: counters.PollFailure,
            RoutesLearned: counters.RoutesLearned,
            PeersAgedOut: counters.PeersAgedOut,
            BudgetRefusals: counters.BudgetRefusals,

            PendingOutboundCount: pendingOutbound,
            UndeliveredLocalCount: undeliveredLocal,
            TotalMessagesCount: totalMessages,
            NeighbourCount: neighbours,
            DiscoveredPeerCount: discoveredPeers,
            DiscoveryChannelCount: discoveryChannels,

            AirtimeConsumedSecondsLastHour: airtime.ConsumedSecondsLastHour,
            AirtimeBudgetSecondsPerHour: airtime.BudgetSecondsPerHour,

            RecentEvents: counters.RecentEvents.Take(RecentEventsInSnapshot).ToList());
    }

    private static async Task<bool> ProbeTcp(string host, int port, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0) return false;
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(timeout);
            await tcp.ConnectAsync(host, port, cts.Token);
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record OperationalSnapshot(
    string Callsign,
    string Version,
    DateTime GeneratedAt,
    long UptimeSeconds,

    string Status,
    bool CallsignConfigured,
    bool BpqAgwReachable,
    bool MqttBrokerUp,
    DateTime? LastForwardSuccessAt,

    long ForwardAttempts,
    long ForwardSuccess,
    long ForwardFailure,
    long TtlExpiredDrops,
    long NoRouteSkips,
    long AgwReconnects,
    DateTime? AgwLastReconnectAt,
    long InboundConnects,
    long HashMismatches,
    long ProbeAttempts,
    long ProbeSuccess,
    long ProbeFailure,
    long PollAttempts,
    long PollSuccess,
    long PollFailure,
    long RoutesLearned,
    long PeersAgedOut,
    long BudgetRefusals,

    int PendingOutboundCount,
    int UndeliveredLocalCount,
    int TotalMessagesCount,
    int NeighbourCount,
    int DiscoveredPeerCount,
    int DiscoveryChannelCount,

    double AirtimeConsumedSecondsLastHour,
    int AirtimeBudgetSecondsPerHour,

    IReadOnlyList<OperationalMetrics.OperationalEvent> RecentEvents);
