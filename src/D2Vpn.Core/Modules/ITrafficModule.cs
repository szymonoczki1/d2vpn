using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Modules;

/// <summary>
/// Decides the fate of each packet on one port group.
/// </summary>
/// <remarks>
/// <para>
/// A module is a pure decision function over a packet stream. It holds no driver handle, no UI and
/// no timers of its own - time comes from the packets, so a module can be driven through an entire
/// hold in a unit test with no elevation and no game running.
/// </para>
/// <para>
/// <see cref="Decide"/> sits in the packet path and is called for every packet on the group, so it
/// must not block, allocate heavily, or throw.
/// </para>
/// </remarks>
public interface ITrafficModule
{
    /// <summary>The ports this module acts on.</summary>
    PortGroup Group { get; }

    /// <summary>Name shown in the UI and in logs.</summary>
    string Name { get; }

    /// <summary>
    /// Whether the module is currently holding. Toggled by a hotkey or the control API.
    /// </summary>
    /// <remarks>
    /// Setting this to <see langword="false"/> must restore normal traffic immediately and
    /// discard any per-peer state, so that releasing is always safe.
    /// </remarks>
    bool IsActive { get; set; }

    /// <summary>Decides what to do with one packet.</summary>
    PacketVerdict Decide(PacketInfo packet);

    /// <summary>Forgets all per-peer state. Called when the module is disabled or torn down.</summary>
    void Reset();
}
