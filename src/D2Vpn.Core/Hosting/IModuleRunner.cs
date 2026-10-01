namespace D2Vpn.Core.Hosting;

/// <summary>
/// One module running under a <see cref="ModuleHost"/>, addressable by name.
/// </summary>
/// <remarks>
/// <para>
/// The seam between the host's bookkeeping and anything that opens a real handle. A runner owns
/// whatever a single module needs to work - in production a WinDivert handle, a buffer and an ack
/// rewriter; in tests, nothing at all. That is what lets the host's state machine and the control
/// protocol be tested without administrator rights or a network.
/// </para>
/// <para>
/// A runner is not reusable. <see cref="RunAsync"/> is called once, and the runner is disposed
/// afterwards.
/// </para>
/// </remarks>
public interface IModuleRunner : IDisposable
{
    /// <summary>
    /// The name the control surface addresses this by, such as <c>27k-ul</c>.
    /// </summary>
    /// <remarks>
    /// Unique within a host, and stable: an AHK script or a UI binds to this string, so it is part
    /// of the contract rather than a label.
    /// </remarks>
    string Name { get; }

    /// <summary>Whether the module is holding.</summary>
    bool IsActive { get; set; }

    /// <summary>True if this runner can inject resets, as the reconnect module does.</summary>
    bool SupportsReset { get; }

    /// <summary>Runs until cancelled. Normal traffic is restored on the way out.</summary>
    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>Asks for a reset on the next pass, if this runner supports one.</summary>
    void RequestReset();

    /// <summary>A point-in-time reading, for the control surface and the status line.</summary>
    ModuleStatus Status { get; }
}
