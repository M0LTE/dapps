using System.Text;
using AwesomeAssertions;
using dapps.client;
using dapps.client.Backhaul;
using dapps.client.Backhaul.Datagram;
using dapps.core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace dapps.core.tests;

/// <summary>
/// Plan-item F1 — end-to-end source tracking. The <c>src=</c> field on
/// the <c>ihave</c> line carries the *originating* callsign (the node a
/// message was first submitted at, by a local app), distinct from the
/// link source (the callsign that handed *this* hop the message). The
/// receiver surfaces it as the <c>dapps-origin</c> MQTT user property
/// and the REST <c>OriginatorCallsign</c> field. These tests pin the
/// wire-format contract on both directions and at both seams (the
/// streamed <c>ihave</c> exchange and the binary datagram codec), and
/// guarantee that pre-F1 senders (no <c>src=</c>) still parse cleanly.
/// </summary>
public class F1SourceTrackingTests
{
    [Fact]
    public void Validator_ParsesSrc_IntoOriginator()
    {
        var line = "ihave abcdeff len=11 fmt=p s=12345678 src=G0ORIG-7 dst=appname@gb7aaa-4";
        var result = IHaveValidator.Validate(line);

        result.IsValid.Should().BeTrue("validator failed: {0}", result.Error);
        result.Offer!.Originator.Should().Be("G0ORIG-7");
        // src= is reserved — must not leak into AdditionalHeaders alongside.
        result.Offer.AdditionalHeaders.Should().NotContainKey("src");
    }

    [Fact]
    public void Validator_AbsentSrc_LeavesOriginatorNull()
    {
        var line = "ihave abcdeff len=11 dst=appname@gb7aaa-4";
        var result = IHaveValidator.Validate(line);

        result.IsValid.Should().BeTrue();
        result.Offer!.Originator.Should().BeNull();
    }

    [Fact]
    public void Validator_EmptySrcValue_RejectedAsMalformedKv()
    {
        // `src=` (no value) violates the general rule that values are
        // non-empty; the existing token validator catches this. Pin the
        // behaviour so a future relaxation doesn't silently treat
        // `src=` as "originator unknown".
        var line = "ihave abcdeff len=11 src= dst=appname@gb7aaa-4";
        var result = IHaveValidator.Validate(line);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Emitter_IncludesSrc_WhenOriginatorSet_AndRoundTrips()
    {
        var msg = new DappsMessage
        {
            Payload = Encoding.UTF8.GetBytes("hello"),
            Salt = 1L,
            Destination = "chat@gb7zzz-4",
        };
        var line = new IHaveCommand { Message = msg, Originator = "M0ORIG-9" }.ToString();

        line.Should().Contain("src=M0ORIG-9");

        var parsed = IHaveValidator.Validate(line);
        parsed.IsValid.Should().BeTrue("checksum / round-trip failed: {0}", parsed.Error);
        parsed.Offer!.Originator.Should().Be("M0ORIG-9");
    }

    [Fact]
    public void Emitter_OmitsSrc_WhenOriginatorNullOrEmpty()
    {
        var msg = new DappsMessage
        {
            Payload = Encoding.UTF8.GetBytes("x"),
            Salt = null,
            Destination = "x@y",
        };
        new IHaveCommand { Message = msg, Originator = null }.ToString().Should().NotContain("src=");
        new IHaveCommand { Message = msg, Originator = "" }.ToString().Should().NotContain("src=");
    }

    [Fact]
    public async Task OfferMessageAsync_EmitsSrc_WhenOriginatorProvided()
    {
        var canned = Encoding.UTF8.GetBytes("send abc1234\n");
        var stream = new FakeDuplexStream(canned);
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        await client.OfferMessageAsync(
            id: "abc1234",
            salt: 99L,
            format: DappsMessage.MessageFormat.Plain,
            destination: "chat@gb7zzz-4",
            length: 4,
            ct: CancellationToken.None,
            ttl: 60,
            originator: "G7ORIG-1");

        var written = Encoding.UTF8.GetString(stream.WriteCapture.ToArray());
        written.Should().Be(
            "ihave abc1234 len=4 fmt=p dst=chat@gb7zzz-4 s=99 ttl=60 src=G7ORIG-1\n");
    }

    [Fact]
    public async Task OfferMessageAsync_OmitsSrc_WhenOriginatorAbsent()
    {
        var canned = Encoding.UTF8.GetBytes("send abc\n");
        var stream = new FakeDuplexStream(canned);
        var client = new DappsProtocolClient(stream, NullLoggerFactory.Instance);

        await client.OfferMessageAsync(
            id: "abc",
            salt: null,
            format: DappsMessage.MessageFormat.Plain,
            destination: "x@y",
            length: 1,
            ct: CancellationToken.None);

        Encoding.UTF8.GetString(stream.WriteCapture.ToArray()).Should().NotContain("src=");
    }

    [Fact]
    public void Codec_RoundTripsOriginator()
    {
        var input = new BackhaulMessage(
            Id: "1234567",
            Destination: "app@N0DEST",
            Salt: 7L,
            Ttl: 90,
            Payload: "hi"u8.ToArray(),
            Originator: "G0ORIG-3");

        var decoded = BackhaulMessageCodec.Decode(BackhaulMessageCodec.Encode(input));
        decoded.Originator.Should().Be("G0ORIG-3");
    }

    [Fact]
    public void Codec_NullOriginator_DoesNotPopulateField()
    {
        var input = new BackhaulMessage(
            Id: "1234567",
            Destination: "app@N0DEST",
            Salt: null,
            Ttl: null,
            Payload: "x"u8.ToArray(),
            Originator: null);

        var decoded = BackhaulMessageCodec.Decode(BackhaulMessageCodec.Encode(input));
        decoded.Originator.Should().BeNull();
    }

    [Fact]
    public void Codec_DecodesV1MessagesAsOriginatorNull()
    {
        // Pre-F1 producers stamped version=1 and never set the originator
        // flag bit. New decoder must keep accepting those messages so
        // in-flight UDP datagrams from un-upgraded senders aren't dropped.
        var v1 = new BackhaulMessage(
            Id: "v1abcde",
            Destination: "app@N0DEST",
            Salt: 1L,
            Ttl: 60,
            Payload: "old"u8.ToArray());
        var bytes = BackhaulMessageCodec.Encode(v1);
        bytes[0] = 1;     // pretend the encoder still wrote v1

        var decoded = BackhaulMessageCodec.Decode(bytes);
        decoded.Id.Should().Be("v1abcde");
        decoded.Originator.Should().BeNull();
        decoded.Payload.Should().Equal(v1.Payload);
    }
}
