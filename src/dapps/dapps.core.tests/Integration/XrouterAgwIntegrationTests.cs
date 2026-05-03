using System.Net.Sockets;
using AwesomeAssertions;
using dapps.client.Transport.Agw;

namespace dapps.core.tests.Integration;

/// <summary>
/// AGW frame-format smoke against XRouter (G8PZT's XrLin) instead of
/// BPQ. Confirms our AGW transport layer is genuinely host-agnostic:
/// the same R-frame handshake we use against BPQ in
/// <see cref="AgwIntegrationTests"/> works byte-for-byte against
/// XRouter's AGW emulator with no DAPPS-side changes.
///
/// The version stamp differs - BPQ reports (2003, 999), XRouter
/// reports (2000, 20) - so this test asserts only that the kind byte
/// echoes back as 'R' and the payload is the documented 8-byte
/// little-endian uint pair, not a specific version.
/// </summary>
[Collection("XRouter integration")]
[Trait("Category", "Integration")]
public sealed class XrouterAgwIntegrationTests(XrouterIntegrationFixture fixture)
{
    [Fact]
    public async Task AgwVersionHandshake_AgainstRealXrouter_Succeeds()
    {
        using var ctSource = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(fixture.Host, fixture.AgwPort, ctSource.Token);
        var framing = new AgwFrameTransport(tcp.GetStream());

        await framing.WriteFrameAsync(
            new AgwFrame(0, 'R', 0, "", "", []),
            ctSource.Token);

        var reply = await framing.ReadFrameAsync(ctSource.Token);

        reply.Kind.Should().Be('R',
            "AGW protocol echoes the kind byte on the version handshake regardless of host");
        reply.Payload.Length.Should().Be(8,
            "version reply is two LE uint32s = 8 bytes");
    }
}
