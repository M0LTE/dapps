using AwesomeAssertions;
using dapps.core.Models;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace dapps.core.tests;

/// <summary>
/// Plan B7 — single-counter discovery airtime budget. The accountant
/// is the shared resource three subsystems (beacons, solicit replies,
/// probes) consult before transmitting; if it gets the bookkeeping
/// wrong, the operator's budget knob silently doesn't work.
/// </summary>
public sealed class AirtimeAccountantTests
{
    [Fact]
    public void TryReserve_BudgetZero_AlwaysAllows()
    {
        var (acct, _) = NewAccountant(budget: 0);

        for (var i = 0; i < 100; i++)
        {
            acct.TryReserve(10, $"call-{i}").Should().BeTrue();
        }
    }

    [Fact]
    public void TryReserve_WithinBudget_Allows()
    {
        var (acct, _) = NewAccountant(budget: 60);

        acct.TryReserve(20, "a").Should().BeTrue();
        acct.TryReserve(20, "b").Should().BeTrue();
        acct.TryReserve(20, "c").Should().BeTrue();
        acct.ConsumedSecondsLastHour.Should().BeApproximately(60, 0.001);
    }

    [Fact]
    public void TryReserve_ExceedingBudget_Rejects()
    {
        var (acct, _) = NewAccountant(budget: 30);

        acct.TryReserve(20, "a").Should().BeTrue();
        acct.TryReserve(20, "b").Should().BeFalse("would push total to 40, over the 30s budget");
        acct.ConsumedSecondsLastHour.Should().BeApproximately(20, 0.001);
    }

    [Fact]
    public void TryReserve_EntriesAgeOut_AfterOneHour()
    {
        var (acct, clock) = NewAccountant(budget: 30);

        acct.TryReserve(25, "old").Should().BeTrue();
        acct.TryReserve(10, "blocked").Should().BeFalse();

        // Roll past the 60-min window — the old entry drops, freeing budget.
        clock.Advance(TimeSpan.FromMinutes(61));
        acct.TryReserve(10, "after-rollover").Should().BeTrue();
        acct.ConsumedSecondsLastHour.Should().BeApproximately(10, 0.001);
    }

    [Fact]
    public void TryReserve_BudgetReducedAtRuntime_NewLowerBudgetEnforced()
    {
        // Operator can flip /Config; the accountant rereads CurrentValue
        // each call. A reduction shouldn't retroactively reject already-
        // reserved airtime, but new reservations are checked against the
        // new ceiling.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var opts = new MutableOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0TST",
            DiscoveryAirtimeBudgetSecondsPerHour = 100,
        });
        var acct = new AirtimeAccountant(opts, clock, NullLogger<AirtimeAccountant>.Instance);

        acct.TryReserve(60, "a").Should().BeTrue();
        opts.CurrentValue.DiscoveryAirtimeBudgetSecondsPerHour = 40;

        acct.TryReserve(1, "b").Should().BeFalse("60 already consumed > new 40s budget");
    }

    [Fact]
    public void TryReserve_NegativeEstimate_ClampedToZero()
    {
        // Defensive: a buggy caller shouldn't be able to "credit" the
        // budget by reserving negative airtime.
        var (acct, _) = NewAccountant(budget: 10);

        acct.TryReserve(-100, "buggy").Should().BeTrue();
        acct.ConsumedSecondsLastHour.Should().BeApproximately(0, 0.001);
        acct.TryReserve(10, "real").Should().BeTrue();
    }

    [Fact]
    public void ConsumedSecondsLastHour_DropsAgedEntries()
    {
        var (acct, clock) = NewAccountant(budget: 0);

        acct.TryReserve(5, "a").Should().BeTrue();
        clock.Advance(TimeSpan.FromMinutes(30));
        acct.TryReserve(5, "b").Should().BeTrue();
        acct.ConsumedSecondsLastHour.Should().BeApproximately(10, 0.001);

        clock.Advance(TimeSpan.FromMinutes(31)); // first entry ages out, second still in
        acct.ConsumedSecondsLastHour.Should().BeApproximately(5, 0.001);

        clock.Advance(TimeSpan.FromMinutes(31)); // second ages out too
        acct.ConsumedSecondsLastHour.Should().BeApproximately(0, 0.001);
    }

    private static (AirtimeAccountant acct, FakeTimeProvider clock) NewAccountant(int budget)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var opts = new MutableOptionsMonitor<SystemOptions>(new SystemOptions
        {
            Callsign = "N0TST",
            DiscoveryAirtimeBudgetSecondsPerHour = budget,
        });
        var acct = new AirtimeAccountant(opts, clock, NullLogger<AirtimeAccountant>.Instance);
        return (acct, clock);
    }

    /// <summary>Mutable IOptionsMonitor for tests that need to flip
    /// CurrentValue mid-test (operator-runtime-flip simulation). The
    /// instance returned from CurrentValue is the same one each call,
    /// so test code can mutate its fields directly.</summary>
    private sealed class MutableOptionsMonitor<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
