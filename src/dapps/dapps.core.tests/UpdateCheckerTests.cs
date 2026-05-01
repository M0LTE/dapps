using AwesomeAssertions;
using dapps.core.Services;

namespace dapps.core.tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("0.8.0", "0.8.0", 0)]
    [InlineData("0.9.0", "0.8.0", +1)]
    [InlineData("0.8.0", "0.9.0", -1)]
    [InlineData("1.0.0", "0.99.99", +1)]
    [InlineData("0.8.1", "0.8.0", +1)]
    [InlineData("0.8", "0.8.0", 0)]
    [InlineData("0.8.0", "0.8", 0)]
    [InlineData("0.10.0", "0.9.0", +1)]
    public void CompareSemver_RanksDottedDecimals(string a, string b, int expectedSign)
    {
        var actual = UpdateChecker.CompareSemver(a, b);
        Math.Sign(actual).Should().Be(expectedSign);
    }

    [Fact]
    public void CompareSemver_StripsPreReleaseSuffix()
    {
        // We don't try to honour pre-release ordering; "0.9.0-rc1" and
        // "0.9.0" compare as equal. The point of the fallback is to
        // tolerate weird tags without crashing.
        UpdateChecker.CompareSemver("0.9.0-rc1", "0.9.0").Should().Be(0);
        UpdateChecker.CompareSemver("0.9.0", "0.9.0-rc1").Should().Be(0);
    }

    [Fact]
    public void CompareSemver_NonNumericComponentsAreTreatedAsZero()
    {
        // "0.x.0" is nonsense; the parser should not throw and should
        // treat the unparseable component as 0.
        UpdateChecker.CompareSemver("0.x.0", "0.0.0").Should().Be(0);
        UpdateChecker.CompareSemver("0.1.0", "0.x.0").Should().BeGreaterThan(0);
    }
}
