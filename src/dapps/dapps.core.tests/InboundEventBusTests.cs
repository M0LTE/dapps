using System.Threading.Channels;
using AwesomeAssertions;
using dapps.core.Services;

namespace dapps.core.tests;

public class InboundEventBusTests
{
    [Fact]
    public async Task Subscribe_GetsAllPublishedEvents()
    {
        var bus = new InboundEventBus();
        using var sub = bus.Subscribe(out var reader);

        bus.Publish(Make("a"));
        bus.Publish(Make("b"));

        var first = await reader.ReadAsync(TestContext.Current.CancellationToken);
        var second = await reader.ReadAsync(TestContext.Current.CancellationToken);
        first.Id.Should().Be("a");
        second.Id.Should().Be("b");
    }

    [Fact]
    public async Task UnsubscribeOnDispose_StopsReceivingEvents()
    {
        var bus = new InboundEventBus();
        var sub = bus.Subscribe(out var reader);

        bus.Publish(Make("before"));
        sub.Dispose();
        bus.Publish(Make("after"));

        // The pre-dispose event is still in the buffer; drain it and EOF.
        var first = await reader.ReadAsync(TestContext.Current.CancellationToken);
        first.Id.Should().Be("before");

        // After dispose the channel is completed — next read throws.
        var act = async () => await reader.ReadAsync(TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<ChannelClosedException>();
    }

    [Fact]
    public void MultipleSubscribers_EachReceiveTheSameEvent()
    {
        var bus = new InboundEventBus();
        using var s1 = bus.Subscribe(out var r1);
        using var s2 = bus.Subscribe(out var r2);

        bus.Publish(Make("fanout"));

        r1.TryRead(out var e1).Should().BeTrue();
        r2.TryRead(out var e2).Should().BeTrue();
        e1!.Id.Should().Be("fanout");
        e2!.Id.Should().Be("fanout");
    }

    [Fact]
    public void SlowSubscriber_DropsOldestRatherThanBlockingPublishers()
    {
        var bus = new InboundEventBus();
        using var sub = bus.Subscribe(out var reader);

        // Bound is 64 — push 100 events without reading. Older ones get
        // dropped under DropOldest policy; publish itself never blocks.
        for (var i = 0; i < 100; i++) bus.Publish(Make($"m{i}"));

        var seen = 0;
        while (reader.TryRead(out _)) seen++;
        seen.Should().BeLessThanOrEqualTo(64,
            "bounded channel caps the backlog at the configured size");
    }

    private static InboundEvent Make(string id) =>
        new(DateTime.UtcNow, id, "G7XYZ", "app@N0CALL", 5, null);
}
