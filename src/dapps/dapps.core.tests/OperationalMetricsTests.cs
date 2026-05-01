using AwesomeAssertions;
using dapps.core.Services;

namespace dapps.core.tests;

public class OperationalMetricsTests
{
    [Fact]
    public void RecordForwardSuccess_BumpsCounters_AndPerNeighbour()
    {
        var m = new OperationalMetrics();
        m.RecordForwardSuccess("G7XYZ", 42);
        m.RecordForwardSuccess("G7XYZ", 100);
        m.RecordForwardSuccess("M0LTE-9", 5);

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
        m.RecordForwardSuccess("G7XYZ", 10);
        m.RecordForwardFailure("G7XYZ", 0, "AGW connect timeout");

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
        m.RecordForwardFailure("G7XYZ", 0, "timeout");
        m.RecordForwardSuccess("G7XYZ", 10);

        m.Take().Neighbours.Single().LastError.Should().BeNull();
    }

    [Fact]
    public void RecentEvents_RingBoundedAtMaxRecent()
    {
        var m = new OperationalMetrics();
        for (var i = 0; i < OperationalMetrics.MaxRecent + 50; i++)
        {
            m.RecordForwardSuccess("G7XYZ", i);
        }

        var s = m.Take();
        s.RecentEvents.Should().HaveCount(OperationalMetrics.MaxRecent);
    }

    [Fact]
    public void RecentEvents_NewestFirst()
    {
        var m = new OperationalMetrics();
        m.RecordForwardSuccess("first", 1);
        m.RecordForwardFailure("second", 0, "boom");

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
}
