using AwesomeAssertions;
using dapps.core.Services;

namespace dapps.core.tests;

public class OperationalMetricsTests
{
    [Fact]
    public void RecordForwardSuccess_BumpsCounters_AndPerNeighbour()
    {
        var m = new OperationalMetrics();
        m.RecordForwardSuccess("aaa1234", "G7XYZ", 42);
        m.RecordForwardSuccess("aaa1235", "G7XYZ", 100);
        m.RecordForwardSuccess("aaa1236", "M0LTE-9", 5);

        var s = m.Take();
        s.ForwardAttempts.Should().Be(3);
        s.ForwardSuccess.Should().Be(3);
        s.ForwardFailure.Should().Be(0);

        s.Neighbours.Should().HaveCount(2);
        var g7 = s.Neighbours.Single(n => n.Callsign == "G7XYZ");
        g7.SuccessCount.Should().Be(2);
        g7.FailureCount.Should().Be(0);
        g7.LastSuccessAt.Should().NotBeNull();
        g7.LastError.Should().BeNull();
    }

    [Fact]
    public void RecordForwardFailure_StashesLastError()
    {
        var m = new OperationalMetrics();
        m.RecordForwardSuccess("aaa1234", "G7XYZ", 10);
        m.RecordForwardFailure("aaa1234", "G7XYZ", 0, "AGW connect timeout");

        var s = m.Take();
        var g7 = s.Neighbours.Single();
        g7.SuccessCount.Should().Be(1);
        g7.FailureCount.Should().Be(1);
        g7.LastError.Should().Be("AGW connect timeout");
        g7.LastSuccessAt.Should().NotBeNull();
        g7.LastFailureAt.Should().NotBeNull();
        s.ForwardAttempts.Should().Be(2);
        s.ForwardSuccess.Should().Be(1);
        s.ForwardFailure.Should().Be(1);
    }

    [Fact]
    public void NextSuccess_ClearsLastError()
    {
        // Sticky-error semantics would be confusing on the dashboard:
        // "this neighbour just successfully forwarded — but it also
        // says 'last error: connection timeout'?" Last-error tracks
        // the most recent failure relative to the most recent success.
        var m = new OperationalMetrics();
        m.RecordForwardFailure("aaa1234", "G7XYZ", 0, "timeout");
        m.RecordForwardSuccess("aaa1234", "G7XYZ", 10);

        m.Take().Neighbours.Single().LastError.Should().BeNull();
    }

    [Fact]
    public void RecentEvents_RingBoundedAtMaxRecent()
    {
        var m = new OperationalMetrics();
        for (var i = 0; i < OperationalMetrics.MaxRecent + 50; i++)
        {
            m.RecordForwardSuccess($"a{i:x6}", "G7XYZ", i);
        }

        var s = m.Take();
        s.RecentEvents.Should().HaveCount(OperationalMetrics.MaxRecent);
    }

    [Fact]
    public void RecentEvents_NewestFirst()
    {
        var m = new OperationalMetrics();
        m.RecordForwardSuccess("aaa1111", "first", 1);
        m.RecordForwardFailure("aaa2222", "second", 0, "boom");

        var ev = m.Take().RecentEvents;
        ev[0].Kind.Should().Be("forward.fail");
        ev[1].Kind.Should().Be("forward.ok");
    }

    [Fact]
    public void AgwReconnect_StampsLastReconnectAt()
    {
        var m = new OperationalMetrics();
        m.Take().AgwLastReconnectAt.Should().BeNull();
        m.RecordAgwReconnect();
        var s = m.Take();
        s.AgwReconnects.Should().Be(1);
        s.AgwLastReconnectAt.Should().NotBeNull();
    }

    [Fact]
    public void TtlAndNoRoute_AreSeparateCounters()
    {
        var m = new OperationalMetrics();
        m.RecordTtlExpired("aaa1234", "app@N0DEST");
        m.RecordTtlExpired("bbb5678", "app@N0DEST");
        m.RecordNoRoute("ccc9abc", "app@N0LOST");

        var s = m.Take();
        s.TtlExpiredDrops.Should().Be(2);
        s.NoRouteSkips.Should().Be(1);
    }

    [Fact]
    public void Probe_SuccessAndFailure_BumpDistinctCounters()
    {
        var m = new OperationalMetrics();
        m.RecordProbeOutcome("G7XYZ", success: true, error: null);
        m.RecordProbeOutcome("G7XYZ", success: false, error: "timeout");

        var s = m.Take();
        s.ProbeAttempts.Should().Be(2);
        s.ProbeSuccess.Should().Be(1);
        s.ProbeFailure.Should().Be(1);
        s.RecentEvents.Select(e => e.Kind).Should().Contain(new[] { "probe.ok", "probe.fail" });
    }

    [Fact]
    public void Poll_SuccessRecordsDrainedCount_Failure_RecordsError()
    {
        var m = new OperationalMetrics();
        m.RecordPollOutcome("G7XYZ", success: true, messagesDrained: 3, error: null);
        m.RecordPollOutcome("G7XYZ", success: true, messagesDrained: 0, error: null);
        m.RecordPollOutcome("G7XYZ", success: false, messagesDrained: 0, error: "no banner");

        var s = m.Take();
        s.PollAttempts.Should().Be(3);
        s.PollSuccess.Should().Be(2);
        s.PollFailure.Should().Be(1);

        var summaries = s.RecentEvents.Select(e => e.Summary).ToList();
        summaries.Should().Contain(s => s.Contains("drained 3"));
        summaries.Should().Contain(s => s.Contains("(empty)"));
        summaries.Should().Contain(s => s.Contains("no banner"));
    }

    [Fact]
    public void RouteLearned_PeerAged_BudgetRefused_BumpCountersAndPushEvents()
    {
        var m = new OperationalMetrics();
        m.RecordRouteLearned("G7DEST", "G7HOP-9");
        m.RecordPeerAgedOut("G7CALL", "udp", "239.0.0.1:54321");
        m.RecordBudgetRefused("global cap: beacon udp/239.x");

        var s = m.Take();
        s.RoutesLearned.Should().Be(1);
        s.PeersAgedOut.Should().Be(1);
        s.BudgetRefusals.Should().Be(1);
        s.RecentEvents.Select(e => e.Kind).Should()
            .Contain(new[] { "route.learned", "peer.aged", "budget.refused" });
    }

    [Fact]
    public void LastForwardSuccessAt_TracksMostRecentSuccess()
    {
        var m = new OperationalMetrics();
        m.LastForwardSuccessAt.Should().BeNull();

        m.RecordForwardSuccess("aaa1111", "G7XYZ", 10);
        m.LastForwardSuccessAt.Should().NotBeNull();
        var firstStamp = m.LastForwardSuccessAt!.Value;

        // A failure between successes must not bump the timestamp —
        // /Health surfaces this as "node was actually doing real work
        // recently" and shouldn't be reset by a transient flap.
        m.RecordForwardFailure("aaa2222", "G7XYZ", 0, "timeout");
        m.LastForwardSuccessAt.Should().Be(firstStamp);

        m.RecordForwardSuccess("aaa3333", "G7XYZ", 10);
        m.LastForwardSuccessAt.Should().BeOnOrAfter(firstStamp);
    }
}
