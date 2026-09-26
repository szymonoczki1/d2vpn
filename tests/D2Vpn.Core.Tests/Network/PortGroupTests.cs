using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Network;

public class PortGroupTests
{
    [Fact]
    public void Contains_includes_both_ends_of_the_range()
    {
        var group = new PortGroup("27k", PortProtocol.Udp, 27015, 27200);

        Assert.True(group.Contains(27015));
        Assert.True(group.Contains(27200));
        Assert.True(group.Contains(27056));
        Assert.False(group.Contains(27014));
        Assert.False(group.Contains(27201));
    }

    [Fact]
    public void A_single_port_group_contains_only_that_port()
    {
        var group = DestinyPorts.Peer;

        Assert.Equal(1, group.PortCount);
        Assert.True(group.Contains(3074));
        Assert.False(group.Contains(3075));
    }

    [Fact]
    public void Overlaps_ignores_groups_on_a_different_protocol()
    {
        var udp = new PortGroup("a", PortProtocol.Udp, 7500, 7509);
        var tcp = new PortGroup("b", PortProtocol.Tcp, 7500, 7509);

        Assert.False(udp.Overlaps(tcp));
    }

    [Fact]
    public void Overlaps_detects_a_partial_range_collision()
    {
        var left = new PortGroup("a", PortProtocol.Udp, 27015, 27100);
        var right = new PortGroup("b", PortProtocol.Udp, 27100, 27200);

        Assert.True(left.Overlaps(right));
        Assert.True(right.Overlaps(left));
    }

    [Fact]
    public void Validate_rejects_an_inverted_range()
    {
        var group = new PortGroup("bad", PortProtocol.Tcp, 30009, 30000);

        Assert.Throws<ArgumentException>(() => group.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Validate_rejects_ports_outside_the_legal_range(int port)
    {
        var group = new PortGroup("bad", PortProtocol.Udp, port, port);

        Assert.Throws<ArgumentException>(() => group.Validate());
    }

    [Fact]
    public void The_known_Destiny_groups_do_not_overlap_each_other()
    {
        // Overlapping groups would mean two WinDivert handles receiving the same packet, each able
        // to drop or reinject it independently.
        foreach (var a in DestinyPorts.All)
        {
            foreach (var b in DestinyPorts.All.Where(x => !ReferenceEquals(x, a)))
            {
                Assert.False(a.Overlaps(b), $"{a} overlaps {b}");
            }
        }
    }

    [Fact]
    public void ForPort_maps_measured_ports_to_the_right_group()
    {
        // Ports actually observed on this machine; see docs/protocol.md.
        Assert.Equal(DestinyPorts.Peer, DestinyPorts.ForPort(PortProtocol.Udp, 3074));
        Assert.Equal(DestinyPorts.Players, DestinyPorts.ForPort(PortProtocol.Udp, 27056));
        Assert.Equal(DestinyPorts.Api, DestinyPorts.ForPort(PortProtocol.Tcp, 7500));
        Assert.Equal(DestinyPorts.Instance, DestinyPorts.ForPort(PortProtocol.Tcp, 30000));
        Assert.Null(DestinyPorts.ForPort(PortProtocol.Udp, 443));
    }
}
