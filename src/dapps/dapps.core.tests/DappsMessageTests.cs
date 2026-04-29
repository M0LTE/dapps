using System.Text;
using dapps.client;
using FluentAssertions;

namespace dapps.core.tests;

public class DappsMessageTests
{
    [Fact]
    public void ComputeHash_WithoutSalt_IsSha1OfPayload()
    {
        var hex = DappsMessage.ComputeHash(Encoding.UTF8.GetBytes("Hello world"), null);
        hex.Should().Be("7b502c3a1f48c8609ae212cdfb639dee39673f5e");
    }

    [Theory]
    [InlineData(1L, "b60ddb6c33151f18b7b302cab13750adc80609f4")]
    [InlineData(12345678L, "83843063e55d6d6d6bd22ddeac7483e87ae74d1c")]
    public void ComputeHash_WithSalt_PrefixesEightLittleEndianBytes(long salt, string expectedHex)
    {
        // Cross-check vector computed independently in Python:
        //   hashlib.sha1(salt.to_bytes(8, 'little') + b'Hello world').hexdigest()
        var hex = DappsMessage.ComputeHash(Encoding.UTF8.GetBytes("Hello world"), salt);
        hex.Should().Be(expectedHex);
    }

    [Fact]
    public void Id_TakesFirst7HexCharsOfHash()
    {
        var msg = new DappsMessage { Payload = Encoding.UTF8.GetBytes("Hello world") };
        msg.Id.Should().Be("7b502c3");
    }
}
