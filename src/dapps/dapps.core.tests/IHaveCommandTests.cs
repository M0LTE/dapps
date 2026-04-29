using System.Text;
using dapps.client;
using dapps.core.Services;
using AwesomeAssertions;

namespace dapps.core.tests;

public class IHaveCommandTests
{
    [Fact]
    public void ToString_ProducesLineThatRoundTripsThroughIHaveValidator()
    {
        var msg = new DappsMessage
        {
            Payload = Encoding.UTF8.GetBytes("Hello world"),
            Salt = 12345678L,
            Destination = "appname@gb7aaa-4",
        };
        var line = new IHaveCommand { Message = msg }.ToString();

        var result = IHaveValidator.Validate(line);

        result.IsValid.Should().BeTrue("validate failed with: {0}; line was: {1}", result.Error, line);
        result.Offer!.Id.Should().Be(msg.Id);
        result.Offer.Length.Should().Be(11);
        result.Offer.Salt.Should().Be(12345678L);
        result.Offer.Destination.Should().Be("appname@gb7aaa-4");
        result.Offer.Format.Should().Be("p");
    }

    [Fact]
    public void Checksum_OfEmptyString_IsCrcInitValue()
    {
        IHaveCommand.Checksum("").Should().Be("ffff");
    }
}
