using dapps.core.Models;
using Microsoft.Extensions.Options;

namespace dapps.core.Services;

/// <summary>
/// Plan B7 — single-counter discovery airtime budget. Beacons, solicit
/// replies, and probes all share one pool of airtime per hour: the
/// scratchpad called for "this DAPPS node is using N% of channel
/// time" as one operator-tuneable knob rather than a per-subsystem
/// silo, and the simplest faithful shape is one rolling 60-min window
/// any discovery-class transmitter consults before sending.
///
/// Disabled by default (<see cref="SystemOptions.DiscoveryAirtimeBudgetSecondsPerHour"/>
/// = 0). When set to a positive value, callers <see cref="TryReserve"/>
/// their estimated airtime cost; the accountant says yes if the
/// trailing-hour total stays under budget, else no — and the caller
/// defers the transmission to a later tick. No carry-forward: a quiet
/// hour doesn't bank credit for a busy one.
///
/// Estimates rather than measurements: link rates aren't observable
/// from .NET, so each transmission type has a coarse per-LinkClass
/// constant (see <see cref="dapps.client.Discovery.LinkClassDefaults.AirtimeSecondsEstimate"/>).
/// Off by an order of magnitude in either direction is fine — the
/// budget is a cap on order-of-magnitude growth, not a precision
/// regulator.
/// </summary>
public sealed class AirtimeAccountant(
    IOptionsMonitor<SystemOptions> options,
    TimeProvider timeProvider,
    ILogger<AirtimeAccountant> logger)
{
    private static readonly TimeSpan WindowSize = TimeSpan.FromHours(1);
    private readonly object _lock = new();
    private readonly Queue<(DateTime At, double Seconds, string Reason)> _entries = new();

    /// <summary>
    /// Try to reserve <paramref name="estimatedSeconds"/> of airtime
    /// for an upcoming transmission. Returns true if the reservation
    /// fits inside the operator's budget; false if it would push the
    /// trailing-hour total past it. <paramref name="reason"/> is logged
    /// on a deferred call and surfaced via diagnostics — pass something
    /// recognisable (e.g. "beacon udp/239.x", "probe G0CALL", "solicit").
    /// </summary>
    public bool TryReserve(double estimatedSeconds, string reason)
    {
        if (estimatedSeconds < 0) estimatedSeconds = 0;
        var budget = options.CurrentValue.DiscoveryAirtimeBudgetSecondsPerHour;

        lock (_lock)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            DropExpiredLocked(now);

            // Budget <= 0 means unlimited — record the reservation for
            // diagnostics (so dashboards can surface "you're using N s/hr
            // even though the cap is off") but never refuse.
            if (budget <= 0)
            {
                _entries.Enqueue((now, estimatedSeconds, reason));
                return true;
            }

            var consumed = SumLocked();
            if (consumed + estimatedSeconds > budget)
            {
                logger.LogInformation(
                    "Airtime budget exhausted — refusing {Reason} ({Estimate:0.000}s); consumed {Consumed:0.000}s of {Budget}s/hour",
                    reason, estimatedSeconds, consumed, budget);
                return false;
            }

            _entries.Enqueue((now, estimatedSeconds, reason));
            return true;
        }
    }

    /// <summary>Trailing-hour airtime consumption in seconds. For
    /// dashboards / diagnostics; not part of the reservation flow.</summary>
    public double ConsumedSecondsLastHour
    {
        get
        {
            lock (_lock)
            {
                DropExpiredLocked(timeProvider.GetUtcNow().UtcDateTime);
                return SumLocked();
            }
        }
    }

    /// <summary>The currently-effective budget, for dashboards. 0 = unlimited.</summary>
    public int BudgetSecondsPerHour => options.CurrentValue.DiscoveryAirtimeBudgetSecondsPerHour;

    private void DropExpiredLocked(DateTime now)
    {
        var cutoff = now - WindowSize;
        while (_entries.Count > 0 && _entries.Peek().At < cutoff)
        {
            _entries.Dequeue();
        }
    }

    private double SumLocked()
    {
        var total = 0.0;
        foreach (var e in _entries) total += e.Seconds;
        return total;
    }
}
