using System.Text;
using AwesomeAssertions;
using dapps.core.Services;

namespace dapps.core.tests;

/// <summary>
/// AGW <c>'G'</c> reply parser. Wire format is BPQ-emitted ASCII
/// "COUNT;DESC0;DESC1;...;" with a trailing NUL. Some implementations
/// drop the leading count or the trailing semicolon; we tolerate both.
/// </summary>
public sealed class AgwPortQueryTests
{
    [Fact]
    public void Parse_BpqStandardReply_ReturnsAllPorts()
    {
        var payload = Encoding.ASCII.GetBytes("3;Port1 - VHF FM 1200 baud;Port2 - HF 30m;Port3 - 9600 baud;\0");
        var ports = AgwPortQuery.Parse(payload);
        ports.Should().HaveCount(3);
        ports[0].Index.Should().Be(0);
        ports[0].Description.Should().Be("Port1 - VHF FM 1200 baud");
        ports[2].Index.Should().Be(2);
        ports[2].Description.Should().Be("Port3 - 9600 baud");
    }

    [Fact]
    public void Parse_NoLeadingCount_StillExtractsPorts()
    {
        // Some AGW implementations skip the count and just emit ;-list.
        var payload = Encoding.ASCII.GetBytes("VHF;UHF;HF\0");
        var ports = AgwPortQuery.Parse(payload);
        ports.Should().HaveCount(3);
        ports[0].Description.Should().Be("VHF");
        ports[1].Description.Should().Be("UHF");
        ports[2].Description.Should().Be("HF");
    }

    [Fact]
    public void Parse_NoTrailingNull_OK()
    {
        var payload = Encoding.ASCII.GetBytes("2;A;B");
        var ports = AgwPortQuery.Parse(payload);
        ports.Should().HaveCount(2);
        ports[0].Description.Should().Be("A");
        ports[1].Description.Should().Be("B");
    }

    [Fact]
    public void Parse_TrailingSemicolon_DoesNotEmitEmptyEntry()
    {
        var payload = Encoding.ASCII.GetBytes("1;Only;\0");
        var ports = AgwPortQuery.Parse(payload);
        ports.Should().HaveCount(1);
        ports[0].Description.Should().Be("Only");
    }

    [Fact]
    public void Parse_EmptyPayload_ReturnsEmpty()
    {
        AgwPortQuery.Parse(Array.Empty<byte>()).Should().BeEmpty();
        AgwPortQuery.Parse(Encoding.ASCII.GetBytes("\0")).Should().BeEmpty();
        AgwPortQuery.Parse(Encoding.ASCII.GetBytes("")).Should().BeEmpty();
    }

    [Fact]
    public void Parse_CountOnlyZeroPorts_ReturnsEmpty()
    {
        // BPQ with no ports configured: just "0;" or "0".
        AgwPortQuery.Parse(Encoding.ASCII.GetBytes("0;\0")).Should().BeEmpty();
        AgwPortQuery.Parse(Encoding.ASCII.GetBytes("0")).Should().BeEmpty();
    }

    [Fact]
    public void Parse_PreservesIndexOrdering()
    {
        // Index is 0-based regardless of count prefix.
        var payload = Encoding.ASCII.GetBytes("4;Alpha;Bravo;Charlie;Delta\0");
        var ports = AgwPortQuery.Parse(payload);
        ports.Select(p => p.Index).Should().Equal(0, 1, 2, 3);
    }
}
