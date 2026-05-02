using System.Collections.Concurrent;

namespace dapps.core.Services;

/// <summary>
/// In-memory counters and recent-event ring for the dashboard's
/// observability surface (Plan C3). Cheap, no schema, no IO — every
/// hot path can call into this without worrying about latency.
///
/// Lifetimes: counters are process-lifetime (reset on restart),
/// recent events bounded at <see cref="MaxRecent"/>. If we eventually
/// want persistent metrics across restarts, this is the obvious
/// place to add a periodic flush — but most operational questions
/// ("did anything fail in the last hour?") are answered by the
/// in-memory copy.
///
/// Thread-safety: counters are <c>Interlocked</c>, the per-neighbour
/// dict is <see cref="ConcurrentDictionary{TKey,TValue}"/>, the
/// recent-events queue is <see cref="ConcurrentQueue{T}"/>. Every
/// recorder method is safe to call from any thread.
/// </summary>
public sealed class OperationalMetrics(TimeProvider timeProvider)
{
    public const int MaxRecent = 100;

    private readonly TimeProvider timeProvider = timeProvider;

    /// <summary>Convenience overload for tests / call sites that don't
    /// want to plumb a clock — uses the system clock. Production code
    /// should let DI inject the registered <see cref="TimeProvider"/>.</summary>
    public OperationalMetrics() : this(TimeProvider.System) { }

    private long _forwardAttempts;
    private long _forwardSuccess;
    private long _forwardFailure;
    private long _ttlExpired;
    private long _noRoute;
    private long _agwReconnects;
    private long _inboundConnects;
    private long _hashMismatches;

    private DateTime? _agwLastReconnectAt;
    private readonly ConcurrentDictionary<string, NeighbourMetrics> _neighbours = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<OperationalEvent> _events = new();

    public void RecordForwardSuccess(string callsign, int bytes)
    {
        Interlocked.Increment(ref _forwardAttempts);
        Interlocked.Increment(ref _forwardSuccess);
        var n = GetOrAdd(callsign);
        n.LastSuccessAt = timeProvider.GetUtcNow().UtcDateTime;
        n.LastError = null;
        Interlocked.Increment(ref n._successCount);
        Push("forward.ok", $"→ {callsign} ({bytes} B)");
    }

    public void RecordForwardFailure(string callsign, int bytes, string? error)
    {
        Interlocked.Increment(ref _forwardAttempts);
        Interlocked.Increment(ref _forwardFailure);
        var n = GetOrAdd(callsign);
        n.LastFailureAt = timeProvider.GetUtcNow().UtcDateTime;
        n.LastError = error;
        Interlocked.Increment(ref n._failureCount);
        Push("forward.fail", $"→ {callsign} ({bytes} B): {error}");
    }

    public void RecordTtlExpired(string id, string destination)
    {
        Interlocked.Increment(ref _ttlExpired);
        Push("ttl.expired", $"{id} for {destination}");
    }

    public void RecordNoRoute(string id, string destination)
    {
        Interlocked.Increment(ref _noRoute);
        Push("route.none", $"{id} for {destination}");
    }

    public void RecordAgwReconnect()
    {
        Interlocked.Increment(ref _agwReconnects);
        _agwLastReconnectAt = timeProvider.GetUtcNow().UtcDateTime;
        Push("agw.reconnect", "AGW socket connected + 'X' registered");
    }

    public void RecordInboundConnect(string remote)
    {
        Interlocked.Increment(ref _inboundConnects);
        Push("inbound.connect", $"L2 SABM from {remote}");
    }

    public void RecordHashMismatch(string id, string source)
    {
        Interlocked.Increment(ref _hashMismatches);
        Push("hash.mismatch", $"{id} from {source}");
    }

    public Snapshot Take()
    {
        return new Snapshot(
            ForwardAttempts: Interlocked.Read(ref _forwardAttempts),
            ForwardSuccess: Interlocked.Read(ref _forwardSuccess),
            ForwardFailure: Interlocked.Read(ref _forwardFailure),
            TtlExpiredDrops: Interlocked.Read(ref _ttlExpired),
            NoRouteSkips: Interlocked.Read(ref _noRoute),
            AgwReconnects: Interlocked.Read(ref _agwReconnects),
            AgwLastReconnectAt: _agwLastReconnectAt,
            InboundConnects: Interlocked.Read(ref _inboundConnects),
            HashMismatches: Interlocked.Read(ref _hashMismatches),
            Neighbours: _neighbours
                .Select(kv => new NeighbourSnapshot(
                    kv.Key,
                    kv.Value.LastSuccessAt,
                    kv.Value.LastFailureAt,
                    kv.Value.LastError,
                    Interlocked.Read(ref kv.Value._successCount),
                    Interlocked.Read(ref kv.Value._failureCount)))
                .OrderBy(n => n.Callsign)
                .ToList(),
            RecentEvents: _events.ToArray().Reverse().ToList());
    }

    private NeighbourMetrics GetOrAdd(string callsign) =>
        _neighbours.GetOrAdd(callsign, _ => new NeighbourMetrics());

    private void Push(string kind, string summary)
    {
        _events.Enqueue(new OperationalEvent(timeProvider.GetUtcNow().UtcDateTime, kind, summary));
        while (_events.Count > MaxRecent)
        {
            _events.TryDequeue(out _);
        }
    }

    private sealed class NeighbourMetrics
    {
        internal long _successCount;
        internal long _failureCount;
        public DateTime? LastSuccessAt;
        public DateTime? LastFailureAt;
        public string? LastError;
    }

    public sealed record OperationalEvent(DateTime At, string Kind, string Summary);

    public sealed record NeighbourSnapshot(
        string Callsign,
        DateTime? LastSuccessAt,
        DateTime? LastFailureAt,
        string? LastError,
        long SuccessCount,
        long FailureCount);

    public sealed record Snapshot(
        long ForwardAttempts,
        long ForwardSuccess,
        long ForwardFailure,
        long TtlExpiredDrops,
        long NoRouteSkips,
        long AgwReconnects,
        DateTime? AgwLastReconnectAt,
        long InboundConnects,
        long HashMismatches,
        IReadOnlyList<NeighbourSnapshot> Neighbours,
        IReadOnlyList<OperationalEvent> RecentEvents);
}
