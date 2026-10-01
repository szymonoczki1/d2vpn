namespace D2Vpn.Core.Hosting;

/// <summary>
/// One thing the control surface can address by name.
/// </summary>
/// <remarks>
/// <para>
/// Split from <see cref="IModuleRunner"/> because the two are no longer one-to-one. A runner is
/// something that <b>runs</b> - it owns a packet handle, or a suspended process - while a
/// controllable is something a macro can <b>toggle</b>, and one runner can expose several.
/// </para>
/// <para>
/// Four toggles on 3074 - uplink, downlink and a slow variant of each - share a single handle
/// because a WinDivert filter is keyed on the port group alone. So one runner, four controllables.
/// Game pause is the opposite shape: a controllable that touches no packets at all.
/// </para>
/// </remarks>
public interface IControllable
{
    /// <summary>The name a macro or a UI binds to, such as <c>3074-ul-slow</c>.</summary>
    string Name { get; }

    /// <summary>What shape of thing this is, which decides how a front end presents it.</summary>
    ModuleKind Kind { get; }

    /// <summary>
    /// Whether this module participates at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="IsActive"/>, and a layer above it: active asks whether the module is
    /// holding right now, enabled asks whether it may. A disabled module releases, refuses to be
    /// activated by a key or by the control pipe, and is skipped by a release-everything.
    /// </para>
    /// <para>
    /// Its handle stays open, because closing and reopening one mid-session is how a port ends up
    /// briefly unfiltered while another limiter is running. A disabled module is inert, not absent.
    /// </para>
    /// </remarks>
    bool IsEnabled { get; set; }

    /// <summary>Whether this is currently on.</summary>
    bool IsActive { get; set; }

    /// <summary>True if this can inject resets.</summary>
    bool SupportsReset { get; }

    /// <summary>The options that can be changed while running, or null if it has none.</summary>
    IModuleOptions? Options { get; }

    /// <summary>A point-in-time reading.</summary>
    ModuleStatus Status { get; }

    /// <summary>Asks for a reset, if this supports one.</summary>
    void RequestReset();
}

/// <summary>What shape of thing a controllable is.</summary>
/// <remarks>
/// A front end cannot present these the same way. A hold is a switch you leave on; a reconnect is a
/// button you press and which has no state at all. Showing a reconnect as a switch that silently
/// returns to off is the kind of control nobody trusts.
/// </remarks>
public enum ModuleKind
{
    /// <summary>A traffic hold or delay line: on until turned off.</summary>
    Hold,

    /// <summary>A one-shot action with no state, such as reconnect.</summary>
    Trigger,

    /// <summary>The game process being suspended.</summary>
    Process
}
