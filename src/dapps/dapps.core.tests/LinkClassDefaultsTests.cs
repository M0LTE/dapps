using AwesomeAssertions;
using dapps.client.Discovery;

namespace dapps.core.tests;

/// <summary>
/// Pinning the <see cref="LinkClass"/> cost ordering. DAPPS is an
/// amateur-radio-first project: when a peer is reachable over RF,
/// the resolver picks RF even if a faster IP path exists. These tests
/// guardrail that policy so a casually-tweaked default doesn't quietly
/// invert the project's identity.
/// </summary>
public class LinkClassDefaultsTests
{
    [Fact]
    public void CostHint_RfClassesAreCheaperThanIpClasses()
    {
        var rfClasses = new[] { LinkClass.VhfUhfFm, LinkClass.MeshCore, LinkClass.Hf };
        var ipClasses = new[] { LinkClass.LanMulticast, LinkClass.InternetIp };

        var maxRf = rfClasses.Max(LinkClassDefaults.CostHint);
        var minIp = ipClasses.Min(LinkClassDefaults.CostHint);

        maxRf.Should().BeLessThan(minIp,
            "every RF class must be cheaper than every IP class — DAPPS routes RF-first; IP is a fallback to bridge between RF islands");
    }

    [Fact]
    public void CostHint_VhfUhfFmCheapestOfAll()
    {
        // Line-of-sight VHF/UHF FM is the most "in spirit" channel
        // (full-duplex, ~always-on, point-to-point) and should win when
        // a peer is reachable on it.
        var vhf = LinkClassDefaults.CostHint(LinkClass.VhfUhfFm);
        foreach (var other in new[] { LinkClass.MeshCore, LinkClass.Hf, LinkClass.LanMulticast, LinkClass.InternetIp })
        {
            LinkClassDefaults.CostHint(other).Should().BeGreaterThan(vhf,
                $"{other} must not be cheaper than VhfUhfFm");
        }
    }

    [Fact]
    public void CostHint_InternetIpMostExpensive()
    {
        // Generic Internet IP is the last-resort bridge.
        var ip = LinkClassDefaults.CostHint(LinkClass.InternetIp);
        foreach (var other in new[] { LinkClass.VhfUhfFm, LinkClass.MeshCore, LinkClass.Hf, LinkClass.LanMulticast })
        {
            LinkClassDefaults.CostHint(other).Should().BeLessThan(ip,
                $"{other} must be cheaper than InternetIp");
        }
    }

    [Fact]
    public void AdvertisedTtl_HfIsLongest()
    {
        // HF propagation closes overnight — a peer that went silent at
        // sundown is still very much "there" at sunup. Long TTL keeps
        // the peer in the discovered table across the prop cycle.
        var hf = LinkClassDefaults.AdvertisedTtlSeconds(LinkClass.Hf);
        foreach (var other in new[] { LinkClass.VhfUhfFm, LinkClass.MeshCore, LinkClass.LanMulticast, LinkClass.InternetIp })
        {
            LinkClassDefaults.AdvertisedTtlSeconds(other).Should().BeLessThan(hf,
                $"{other} ttl must not exceed Hf's propagation-aware default");
        }
    }

    [Fact]
    public void BeaconInterval_RfChannelsBeaconLessOftenThanIp()
    {
        // Channel-sharing politeness — a 1200 baud VHF doesn't want a
        // beacon every minute regardless of which we'd rather route on.
        LinkClassDefaults.BeaconIntervalSeconds(LinkClass.LanMulticast)
            .Should().BeLessThan(LinkClassDefaults.BeaconIntervalSeconds(LinkClass.VhfUhfFm));
        LinkClassDefaults.BeaconIntervalSeconds(LinkClass.InternetIp)
            .Should().BeLessThan(LinkClassDefaults.BeaconIntervalSeconds(LinkClass.VhfUhfFm));
    }
}
