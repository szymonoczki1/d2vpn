using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Interception;

public class WinDivertFilterBuilderTests
{
    [Fact]
    public void A_single_port_group_uses_an_equality_filter()
    {
        var filter = WinDivertFilterBuilder.Build(DestinyPorts.Peer).ToString();

        Assert.Contains("udp", filter);
        Assert.Contains("remotePort == 3074", filter);
    }

    [Fact]
    public void A_range_group_uses_bounded_comparisons()
    {
        var filter = WinDivertFilterBuilder.Build(DestinyPorts.Players).ToString();

        Assert.Contains("udp", filter);
        Assert.Contains("remotePort >= 27015", filter);
        Assert.Contains("remotePort <= 27200", filter);
    }

    [Fact]
    public void Tcp_groups_filter_on_tcp()
    {
        var api = WinDivertFilterBuilder.Build(DestinyPorts.Api).ToString();
        var instance = WinDivertFilterBuilder.Build(DestinyPorts.Instance).ToString();

        Assert.Contains("tcp", api);
        Assert.Contains("tcp", instance);
        Assert.DoesNotContain("udp", api);
        Assert.DoesNotContain("udp", instance);
    }

    [Fact]
    public void No_filter_mentions_a_local_address_or_an_interface()
    {
        // This is the VPN-agnostic guarantee, enforced. Binding a filter to an address or adapter
        // would make every module stop matching the moment the VPN is toggled, and a tunnel goes up
        // and down as a matter of course.
        string[] forbidden = ["localAddr", "localPort", "ifIdx", "subIfIdx", "outbound", "inbound"];

        foreach (var group in DestinyPorts.All)
        {
            var filter = WinDivertFilterBuilder.Build(group).ToString() ?? string.Empty;

            foreach (var token in forbidden)
            {
                Assert.False(
                    filter.Contains(token, StringComparison.OrdinalIgnoreCase),
                    $"Filter for {group} must not reference '{token}', but was: {filter}");
            }
        }
    }

    [Fact]
    public void Build_rejects_an_invalid_group()
    {
        var group = new PortGroup("bad", PortProtocol.Udp, 500, 400);

        Assert.Throws<ArgumentException>(() => WinDivertFilterBuilder.Build(group));
    }
}
