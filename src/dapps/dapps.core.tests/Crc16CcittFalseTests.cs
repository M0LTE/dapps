using System.Text;
using dapps.client;
using AwesomeAssertions;

namespace dapps.core.tests;

public class Crc16CcittFalseTests
{
    [Theory]
    [InlineData("", (ushort)0xFFFF)]
    [InlineData("123456789", (ushort)0x29B1)]
    [InlineData(
        "ihave abcdeff len=11 fmt=p s=12345678 dst=appname@gb7aaa-4 ttl=86400 key=value",
        (ushort)0x6907)]
    public void Compute_ProducesExpectedValue(string input, ushort expected)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        Crc16CcittFalse.Compute(bytes).Should().Be(expected);
    }

    [Fact]
    public void ComputeHex_ProducesLowercaseFourCharString()
    {
        var bytes = Encoding.UTF8.GetBytes(
            "ihave abcdeff len=11 fmt=p s=12345678 dst=appname@gb7aaa-4 ttl=86400 key=value");
        Crc16CcittFalse.ComputeHex(bytes).Should().Be("6907");
    }
}
