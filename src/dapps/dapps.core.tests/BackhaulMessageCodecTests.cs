using AwesomeAssertions;
using dapps.client.Backhaul;
using dapps.client.Backhaul.Datagram;

namespace dapps.core.tests;

/// <summary>
/// Round-trip tests for the binary <see cref="BackhaulMessageCodec"/>.
/// Datagram-shaped bearers (UDP today, MeshCore later) round-trip every
/// <see cref="BackhaulMessage"/> field through this codec; if encoding
/// drops a field or the decoder reads the wrong offset, the bearer
/// stops being binary-faithful at the seam. The test sweep covers each
/// optional field's presence/absence combinations.
/// </summary>
public class BackhaulMessageCodecTests
{
    [Fact]
    public void RoundTrip_MinimalMessage_PreservesAllFields()
    {
        var input = new BackhaulMessage(
            Id: "abcdeff",
            Destination: "app@N0CALL",
            Salt: null,
            Ttl: null,
            Payload: "hello"u8.ToArray());

        var encoded = BackhaulMessageCodec.Encode(input);
        var decoded = BackhaulMessageCodec.Decode(encoded);

        decoded.Should().Be(input with { Payload = decoded.Payload });
        decoded.Payload.Should().Equal(input.Payload);
        decoded.Headers.Should().BeNull();
    }

    [Fact]
    public void RoundTrip_AllFieldsSet_PreservesEverything()
    {
        var headers = new Dictionary<string, string>
        {
            ["priority"] = "high",
            ["src"] = "G0ORIG",
        };
        var input = new BackhaulMessage(
            Id: "1234567",
            Destination: "myapp@N0DEST-7",
            Salt: 12345678901234L,
            Ttl: 600,
            Payload: Enumerable.Range(0, 200).Select(i => (byte)i).ToArray(),
            Headers: headers);

        var encoded = BackhaulMessageCodec.Encode(input);
        var decoded = BackhaulMessageCodec.Decode(encoded);

        decoded.Id.Should().Be(input.Id);
        decoded.Destination.Should().Be(input.Destination);
        decoded.Salt.Should().Be(input.Salt);
        decoded.Ttl.Should().Be(input.Ttl);
        decoded.Payload.Should().Equal(input.Payload);
        decoded.Headers.Should().NotBeNull();
        decoded.Headers!["priority"].Should().Be("high");
        decoded.Headers["src"].Should().Be("G0ORIG");
    }

    [Fact]
    public void RoundTrip_BinaryPayload_AllByteValues()
    {
        var payload = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var input = new BackhaulMessage("0000001", "x@y", null, null, payload);

        var decoded = BackhaulMessageCodec.Decode(BackhaulMessageCodec.Encode(input));

        decoded.Payload.Should().Equal(payload);
    }

    [Fact]
    public void Encode_WrongIdLength_Throws()
    {
        var bm = new BackhaulMessage("toolong", "x@y", null, null, []);
        var act = () => BackhaulMessageCodec.Encode(bm with { Id = "short" });
        act.Should().Throw<ArgumentException>().WithMessage("*7 characters*");
    }

    [Fact]
    public void Decode_WrongVersion_Throws()
    {
        var bytes = BackhaulMessageCodec.Encode(
            new BackhaulMessage("0000001", "x@y", null, null, []));
        bytes[0] = 0xFF;

        var act = () => BackhaulMessageCodec.Decode(bytes);
        act.Should().Throw<InvalidDataException>().WithMessage("*version*");
    }

    [Fact]
    public void RoundTrip_LargePayload_StaysBinaryFaithful()
    {
        // 5 KB exercises the 4-byte payload-length field's range and gives
        // the packetiser something to chew on at the wire layer.
        var payload = new byte[5000];
        Random.Shared.NextBytes(payload);
        var input = new BackhaulMessage("ffffff0", "big@N0DEST", 999L, 60, payload);

        var decoded = BackhaulMessageCodec.Decode(BackhaulMessageCodec.Encode(input));

        decoded.Payload.Should().Equal(payload);
    }
}
