namespace D2Vpn.Core.Network;

/// <summary>
/// A contiguous range of remote ports on one protocol, treated as a single unit by a module.
/// </summary>
/// <remarks>
/// <para>
/// A group is defined purely by protocol and <em>remote</em> port. It deliberately carries no
/// local address, adapter, or interface: d2vpn must behave identically whether traffic leaves
/// through a VPN tunnel or a physical NIC, and binding a group to either would break that.
/// </para>
/// <para>
/// Single ports are expressed as a range whose ends are equal, so callers never need two shapes.
/// </para>
/// </remarks>
/// <param name="Name">Short label used in configuration and the UI, e.g. "3074" or "27k".</param>
/// <param name="Protocol">Transport protocol the group matches.</param>
/// <param name="FromPort">First remote port in the range, inclusive.</param>
/// <param name="ToPort">Last remote port in the range, inclusive.</param>
public sealed record PortGroup(string Name, PortProtocol Protocol, int FromPort, int ToPort)
{
    /// <summary>Lowest valid port number.</summary>
    public const int MinPort = 1;

    /// <summary>Highest valid port number.</summary>
    public const int MaxPort = 65535;

    /// <summary>Validates the range and throws if it is unusable.</summary>
    /// <exception cref="ArgumentException">The name is blank or the range is invalid.</exception>
    public PortGroup Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Port group name must not be blank.", nameof(Name));

        if (FromPort < MinPort || FromPort > MaxPort)
            throw new ArgumentException($"FromPort {FromPort} is outside {MinPort}-{MaxPort}.", nameof(FromPort));

        if (ToPort < MinPort || ToPort > MaxPort)
            throw new ArgumentException($"ToPort {ToPort} is outside {MinPort}-{MaxPort}.", nameof(ToPort));

        if (ToPort < FromPort)
            throw new ArgumentException($"ToPort {ToPort} is below FromPort {FromPort}.", nameof(ToPort));

        return this;
    }

    /// <summary>True when <paramref name="port"/> falls inside this group's range.</summary>
    public bool Contains(int port) => port >= FromPort && port <= ToPort;

    /// <summary>True when this group shares at least one port and the protocol with another.</summary>
    /// <remarks>
    /// Two WinDivert handles whose filters overlap both receive every matching packet and can each
    /// drop or reinject it independently. Callers use this to refuse an ambiguous configuration
    /// rather than debug the resulting chaos.
    /// </remarks>
    public bool Overlaps(PortGroup other) =>
        Protocol == other.Protocol && FromPort <= other.ToPort && ToPort >= other.FromPort;

    /// <summary>Number of ports covered.</summary>
    public int PortCount => ToPort - FromPort + 1;

    /// <inheritdoc/>
    public override string ToString() =>
        FromPort == ToPort
            ? $"{Name} ({Protocol.ToString().ToUpperInvariant()} {FromPort})"
            : $"{Name} ({Protocol.ToString().ToUpperInvariant()} {FromPort}-{ToPort})";
}
