using AwesomeAssertions;
using dapps.core.Services;

namespace dapps.core.tests;

public class TtlMathTests
{
    private static readonly DateTime BaseTime = new(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Residual_NullTtl_ReturnsNull()
    {
        TtlMath.Residual(ttl: null, createdAt: BaseTime, now: BaseTime.AddSeconds(30))
            .Should().BeNull();
    }

    [Fact]
    public void Residual_FreshlyCreated_ReturnsFullTtl()
    {
        TtlMath.Residual(ttl: 60, createdAt: BaseTime, now: BaseTime)
            .Should().Be(60);
    }

    [Fact]
    public void Residual_HalfElapsed_ReturnsHalf()
    {
        TtlMath.Residual(ttl: 60, createdAt: BaseTime, now: BaseTime.AddSeconds(30))
            .Should().Be(30);
    }

    [Fact]
    public void Residual_ExactlyExpired_ReturnsZero()
    {
        TtlMath.Residual(ttl: 60, createdAt: BaseTime, now: BaseTime.AddSeconds(60))
            .Should().Be(0);
    }

    [Fact]
    public void Residual_Overrun_ReturnsNegative()
    {
        TtlMath.Residual(ttl: 60, createdAt: BaseTime, now: BaseTime.AddSeconds(120))
            .Should().Be(-60);
    }

    [Fact]
    public void Residual_SubSecondOverrun_RoundsToOneSecondElapsed()
    {
        // 0.4s past creation should consume one second of headroom rather
        // than zero — preferring "expire on time" over "expire late".
        TtlMath.Residual(ttl: 60, createdAt: BaseTime, now: BaseTime.AddMilliseconds(400))
            .Should().Be(59);
    }

    [Fact]
    public void HasExpired_NullTtl_NeverExpires()
    {
        TtlMath.HasExpired(ttl: null, createdAt: BaseTime, now: BaseTime.AddDays(365))
            .Should().BeFalse();
    }

    [Fact]
    public void HasExpired_AtDeadline_IsTrue()
    {
        TtlMath.HasExpired(ttl: 60, createdAt: BaseTime, now: BaseTime.AddSeconds(60))
            .Should().BeTrue();
    }

    [Fact]
    public void HasExpired_BeforeDeadline_IsFalse()
    {
        TtlMath.HasExpired(ttl: 60, createdAt: BaseTime, now: BaseTime.AddSeconds(59))
            .Should().BeFalse();
    }

    [Fact]
    public void HasExpired_PastDeadline_IsTrue()
    {
        TtlMath.HasExpired(ttl: 60, createdAt: BaseTime, now: BaseTime.AddSeconds(61))
            .Should().BeTrue();
    }
}
