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
    /// A label for this runner, used in errors and on the status line.
    /// </summary>
    /// <remarks>
    /// Not the control-surface address. A runner sharing one handle between four modules has four
    /// addresses, which are the names on its <see cref="Controls"/>.
    /// </remarks>
    string Name { get; }

    /// <summary>
    /// What this runner exposes to the control surface, in declared order.
    /// </summary>
    /// <remarks>
    /// One entry for a runner holding a single module, several for one sharing a handle between
    /// modules on the same port group. Names must be unique across the whole host.
    /// </remarks>
    IReadOnlyList<IControllable> Controls { get; }

    /// <summary>
    /// What this runner's packet handle claims, or <see langword="null"/> if it opens none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two runners in one host may not claim the same thing, and <see cref="ModuleHost"/> refuses
    /// a set that does. The reason is specific to WinDivert: a filter is built from a port group
    /// alone - protocol and port range, never direction - so <c>3074-ul</c> and <c>3074-dl</c>
    /// produce byte-identical filters, as do <c>30k</c> and <c>30k-slow</c>. Two handles matching
    /// the same packets are ordered by priority, and the second sees only what the first chose to
    /// reinject. A module behind another one that is withholding would appear to do nothing, with
    /// no error to explain it.
    /// </para>
    /// <para>
    /// The filter expression is the natural key: identical filters are exactly the collision.
    /// </para>
    /// </remarks>
    string? HandleKey { get; }

    /// <summary>Runs until cancelled. Normal traffic is restored on the way out.</summary>
    Task RunAsync(CancellationToken cancellationToken);
}
