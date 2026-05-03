namespace dapps.core.Services;

/// <summary>
/// Plan B7 - a small "is the forwarder busy right now?" oracle, used
/// by <see cref="ProbeSchedulerService"/> in WhenQuiet mode to defer
/// probes while real message traffic is going out. Singleton; the
/// outbound forwarder writes after every successful send, the probe
/// scheduler reads.
///
/// "Activity" here is link-source activity, not airtime: a write is
/// recorded regardless of bearer (UDP loopback in tests counts the
/// same as VHF), because the WhenQuiet semantics are about
/// scheduling courtesy ("don't elbow into a forwarder run"), not
/// channel-share politeness - that's the airtime accountant's job.
/// </summary>
public sealed class OutboundActivityTracker(TimeProvider timeProvider)
{
    private DateTime? _lastTransmissionAt;

    public DateTime? LastTransmissionAt => _lastTransmissionAt;

    public void RecordTransmission()
    {
        _lastTransmissionAt = timeProvider.GetUtcNow().UtcDateTime;
    }

    /// <summary>Time since the most recent transmission. Null if we've
    /// never recorded one - interpret as "no recent activity, free to
    /// proceed".</summary>
    public TimeSpan? IdleFor()
    {
        if (_lastTransmissionAt is not { } at) return null;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var delta = now - at;
        return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
    }
}
