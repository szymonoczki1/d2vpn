using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Modules;

/// <summary>
/// Rules on nothing. Every packet passes, always.
/// </summary>
/// <remarks>
/// For a controller that is there to watch and to inject rather than to block - Reconnect is the
/// case it exists for. That module never changes a verdict: it keeps a sample of each live
/// connection and, when asked, sends a reset built from it. Holding traffic would be beside the
/// point and blocking any of it would be actively wrong, since the connection has to be healthy
/// right up to the moment it is killed.
/// </remarks>
public sealed class PassThroughModule(string name, PortGroup group) : ITrafficModule
{
    /// <inheritdoc/>
    public PortGroup Group { get; } = group;

    /// <inheritdoc/>
    public string Name { get; } = name;

    /// <summary>
    /// Always false. Nothing here is ever held, so there is no state for this to describe.
    /// </summary>
    public bool IsActive
    {
        get => false;
        set { }
    }

    /// <inheritdoc/>
    public PacketVerdict Decide(PacketInfo packet) => PacketVerdict.Pass;

    /// <inheritdoc/>
    public void Reset()
    {
    }
}
