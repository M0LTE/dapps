using AwesomeAssertions;
using dapps.client.Backhaul.Datagram;

namespace dapps.core.tests;

/// <summary>
/// Splitting + reassembly tests. The bearer-agnostic packetiser is
/// where DAPPS owns fragmentation policy (Plan A0.3); these tests pin
/// down the contract so a future MeshCore bearer can drop in without
/// re-deriving the rules.
/// </summary>
public class PacketiserTests
{
    [Fact]
    public void Split_FitsInOneFragment_ProducesSingleDatagram()
    {
        var fragments = Packetiser.Split("abcdefa", "hello"u8.ToArray(), mtu: 200);
        fragments.Should().ContainSingle();
        Packetiser.ParseHeader(fragments[0]).Count.Should().Be(1);
    }

    [Fact]
    public void Split_LargerThanMtu_FragmentsAndReassembles()
    {
        var data = new byte[500];
        Random.Shared.NextBytes(data);
        const int mtu = 80;

        var fragments = Packetiser.Split("12345ab", data, mtu);

        fragments.Count.Should().BeGreaterThan(1, "500 bytes / ~67 chunk bytes per fragment must split");
        fragments.Should().AllSatisfy(f => f.Length.Should().BeLessThanOrEqualTo(mtu));

        var reassembler = new Reassembler();
        byte[]? assembled = null;
        foreach (var f in fragments)
        {
            assembled = reassembler.Accept(f, DateTime.UtcNow);
        }
        assembled.Should().NotBeNull();
        assembled!.Should().Equal(data);
    }

    [Fact]
    public void Reassembler_OutOfOrderFragments_StillReassembles()
    {
        var data = new byte[300];
        Random.Shared.NextBytes(data);
        var fragments = Packetiser.Split("abcdef0", data, mtu: 64).ToList();
        // Shuffle.
        for (var i = fragments.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (fragments[i], fragments[j]) = (fragments[j], fragments[i]);
        }

        var reassembler = new Reassembler();
        byte[]? assembled = null;
        foreach (var f in fragments)
        {
            assembled = reassembler.Accept(f, DateTime.UtcNow);
        }

        assembled.Should().NotBeNull();
        assembled!.Should().Equal(data);
    }

    [Fact]
    public void Reassembler_DuplicateFragment_DoesNotDoubleCount()
    {
        var fragments = Packetiser.Split("dupdup0", new byte[100], mtu: 32).ToList();
        var reassembler = new Reassembler();
        // First copy of the first fragment - should not complete yet.
        reassembler.Accept(fragments[0], DateTime.UtcNow).Should().BeNull();
        // Same fragment again - also should not complete.
        reassembler.Accept(fragments[0], DateTime.UtcNow).Should().BeNull();
        // Now feed the rest in order.
        byte[]? assembled = null;
        for (var i = 1; i < fragments.Count; i++)
        {
            assembled = reassembler.Accept(fragments[i], DateTime.UtcNow);
        }
        assembled.Should().NotBeNull("duplicate fragments should not derail completion");
    }

    [Fact]
    public void Reassembler_DropOlderThan_RemovesIncompletePartials()
    {
        var fragments = Packetiser.Split("staling", new byte[200], mtu: 64);
        var reassembler = new Reassembler();
        var t0 = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc);

        reassembler.Accept(fragments[0], t0); // start a partial at t0

        var dropped = reassembler.DropOlderThan(t0.AddMinutes(5));
        dropped.Should().Be(1);

        // After the drop the next fragment for the same id is treated as
        // a fresh first fragment; reassembly does not complete from the
        // remaining old fragments.
        reassembler.Accept(fragments[1], t0.AddMinutes(6)).Should().BeNull();
    }

    [Fact]
    public void Split_EmptyBuffer_ProducesOneEmptyFragment()
    {
        var fragments = Packetiser.Split("abcdeff", [], mtu: 100);
        fragments.Should().ContainSingle();
        var hdr = Packetiser.ParseHeader(fragments[0]);
        hdr.Count.Should().Be(1);
        hdr.ChunkLength.Should().Be(0);

        var reassembler = new Reassembler();
        var assembled = reassembler.Accept(fragments[0], DateTime.UtcNow);
        assembled.Should().NotBeNull();
        assembled!.Length.Should().Be(0);
    }

    [Fact]
    public void Split_MtuTooSmall_Throws()
    {
        var act = () => Packetiser.Split("abcdeff", new byte[10], mtu: Packetiser.MinMtu - 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
