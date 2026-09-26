namespace D2Vpn.Core.Network;

/// <summary>
/// The port groups Destiny 2 uses, as observed on this machine and cross-checked against the
/// ranges long-running community limiters filter on.
/// </summary>
/// <remarks>
/// <para>
/// Ranges are deliberately kept wider than what a single capture shows. A 10-second sample saw
/// only ports 27017-27127 in <see cref="Players"/>, exactly 7500 in <see cref="Api"/>, and exactly
/// 30000 in <see cref="Instance"/> - but tools using these wider ranges have run reliably for
/// years, and a narrow filter fails silently by simply never matching. Over-matching costs a few
/// wasted comparisons; under-matching costs a module that quietly does nothing.
/// </para>
/// <para>See <c>docs/protocol.md</c> for the measurements behind each entry.</para>
/// </remarks>
public static class DestinyPorts
{
    /// <summary>
    /// UDP 3074. Primary peer traffic, roughly a 10 Hz tick in each direction.
    /// </summary>
    public static readonly PortGroup Peer = new("3074", PortProtocol.Udp, 3074, 3074);

    /// <summary>
    /// UDP 27015-27200. Additional peer traffic. Shares a range conventionally associated with
    /// Steam, but every port observed in it belonged to the game process, not to Steam.
    /// </summary>
    public static readonly PortGroup Players = new("27k", PortProtocol.Udp, 27015, 27200);

    /// <summary>
    /// TCP 7500-7509. Service channel. This is the group whose weapon-swap packet size users are
    /// asked to calibrate, so it matters most to the calibrator.
    /// </summary>
    public static readonly PortGroup Api = new("7500", PortProtocol.Tcp, 7500, 7509);

    /// <summary>
    /// TCP 30000-30009. Instance and session control. Long-lived connections; injecting a TCP RST
    /// here is what forces a reconnect.
    /// </summary>
    public static readonly PortGroup Instance = new("30k", PortProtocol.Tcp, 30000, 30009);

    /// <summary>All known groups.</summary>
    public static IReadOnlyList<PortGroup> All { get; } = [Peer, Players, Api, Instance];

    /// <summary>Finds a group by its <see cref="PortGroup.Name"/>, case-insensitively.</summary>
    /// <returns>The group, or <see langword="null"/> when no group has that name.</returns>
    public static PortGroup? ByName(string name) =>
        All.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Finds the group that would match a given protocol and remote port.
    /// </summary>
    /// <returns>The group, or <see langword="null"/> when the port belongs to no known group.</returns>
    public static PortGroup? ForPort(PortProtocol protocol, int port) =>
        All.FirstOrDefault(g => g.Protocol == protocol && g.Contains(port));
}
