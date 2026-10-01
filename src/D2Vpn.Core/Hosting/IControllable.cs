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
