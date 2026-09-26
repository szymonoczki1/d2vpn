using D2Vpn.Core.Network;
using WindivertDotnet;

namespace D2Vpn.Core.Interception;

/// <summary>
/// Turns a <see cref="PortGroup"/> into a WinDivert filter.
/// </summary>
/// <remarks>
/// Separate from <see cref="WinDivertPacketSource"/> on purpose: building a filter needs no driver
/// and no elevation, so the rule that filters stay address-free can be enforced by a test that
/// runs anywhere.
/// </remarks>
public static class WinDivertFilterBuilder
{
    /// <summary>
    /// Builds a filter matching <paramref name="group"/> by protocol and remote port only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No local address, adapter or interface may ever appear in a filter.</b> WinDivert hooks
    /// the network stack rather than an adapter, so a port-keyed filter matches whether the packet
    /// is bound for a VPN tunnel or a physical NIC. That property is the whole reason d2vpn keeps
    /// working when the VPN is toggled, and it is load-bearing: a connection behind CGNAT needs a
    /// tunnel for a workable NAT type, but the app must not assume one is there.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The group's range is invalid.</exception>
    public static Filter Build(PortGroup group)
    {
        group.Validate();

        // Captured locals are evaluated by the expression translator, so a range needs no special
        // handling beyond preferring the single-port form when both ends match.
        int from = group.FromPort;
        int to = group.ToPort;
        bool single = from == to;

        return group.Protocol switch
        {
            PortProtocol.Udp when single =>
                Filter.True.And(x => x.IsUdp && x.Network.RemotePort == from),
            PortProtocol.Udp =>
                Filter.True.And(x => x.IsUdp && x.Network.RemotePort >= from && x.Network.RemotePort <= to),
            PortProtocol.Tcp when single =>
                Filter.True.And(x => x.IsTcp && x.Network.RemotePort == from),
            PortProtocol.Tcp =>
                Filter.True.And(x => x.IsTcp && x.Network.RemotePort >= from && x.Network.RemotePort <= to),
            _ => throw new ArgumentOutOfRangeException(nameof(group), group.Protocol, "Unknown protocol.")
        };
    }
}
