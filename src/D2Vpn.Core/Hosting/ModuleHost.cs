namespace D2Vpn.Core.Hosting;

/// <summary>
/// Runs several modules at once and lets them be addressed by name.
/// </summary>
/// <remarks>
/// <para>
/// Every command before this one owned a single module, its own WinDivert handle and its own
/// hotkey loop, which is why running two modules together meant writing a third command. The host
/// is the one place that holds a set of modules, so the AutoHotkey manager and the WPF UI are
/// front-ends over it rather than reimplementations of it.
/// </para>
/// <para>
/// <b>It binds no hotkeys.</b> The design decision behind this project is that a macro triggers a
/// module directly rather than synthesising a keypress for a global listener to notice, so the host
/// exposes <see cref="Toggle"/> and leaves input to whatever is driving it. The single-module
/// <c>run</c> command keeps its own hotkeys for use without a macro.
/// </para>
/// <para>
/// <b>One handle per module, not one shared handle.</b> A combined filter would be fewer handles,
/// but it would also mean rewriting the receive loop and re-establishing drop semantics that are
/// already measured. Separate handles reuse the controller unchanged.
/// </para>
/// <para>
/// The cost is real and worth stating plainly: a filter is built from a port group alone, so every
/// module on one group wants the same handle. <c>3074-ul</c> and <c>3074-dl</c> cannot both be
/// hosted, nor <c>30k</c> and <c>30k-slow</c>. The constructor refuses such a set rather than
/// letting one module silently sit behind another - see <see cref="IModuleRunner.HandleKey"/>.
/// Independent per-direction toggles on one group would need a runner that owns several modules
/// behind a single handle, which does not exist yet.
/// </para>
/// <para>
/// Toggling is safe from any thread: a control connection, a UI thread and the shutdown path all
/// reach the same runners.
/// </para>
/// </remarks>
public sealed class ModuleHost : IDisposable
{
    private readonly Dictionary<string, IModuleRunner> _runners;
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Creates a host over a set of runners.</summary>
    /// <exception cref="ArgumentException">
    /// No runners were given, or two share a name.
    /// </exception>
    public ModuleHost(IEnumerable<IModuleRunner> runners)
    {
        _runners = [];

        foreach (var runner in runners)
        {
            if (!_runners.TryAdd(runner.Name, runner))
            {
                throw new ArgumentException(
                    $"two modules are both called '{runner.Name}' - names are how the control "
                    + "surface addresses them, so they have to be unique");
            }
        }

        if (_runners.Count == 0)
        {
            throw new ArgumentException("a host needs at least one module");
        }

        // Overlapping handles are the failure that has no symptom. See IModuleRunner.HandleKey:
        // a WinDivert filter is keyed on the port group alone, so two modules on one group match
        // the same packets and the lower-priority one only ever sees what the other reinjected.
        var claims = _runners.Values
            .Where(r => r.HandleKey is not null)
            .GroupBy(r => r.HandleKey!)
            .FirstOrDefault(g => g.Count() > 1);

        if (claims is not null)
        {
            var names = claims.Select(r => r.Name).ToArray();

            throw new ArgumentException(
                $"{string.Join(" and ", names)} act on the same ports, so they would need the "
                + "same packet handle and the second would only see what the first let through. "
                + "Host one of them, or use the combined module if you want both directions at "
                + "once.");
        }
    }

    /// <summary>The module names, in the order they were given.</summary>
    public IReadOnlyList<string> Names => [.. _runners.Keys];

    /// <summary>How many modules are holding.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _runners.Values.Count(r => r.IsActive);
            }
        }
    }

    /// <summary>Raised when a module's active state changes, with the new state.</summary>
    /// <remarks>
    /// So the console can print a line and a log can record one without either polling. Raised
    /// outside the lock, so a slow handler cannot block another toggle.
    /// </remarks>
    public event Action<string, bool>? StateChanged;

    /// <summary>True if a module of this name is hosted.</summary>
    public bool Has(string name) => _runners.ContainsKey(name);

    /// <summary>Flips one module, returning its new state.</summary>
    /// <exception cref="KeyNotFoundException">No such module.</exception>
    public bool Toggle(string name)
    {
        var runner = Find(name);
        bool now;

        lock (_gate)
        {
            now = !runner.IsActive;
            runner.IsActive = now;
        }

        StateChanged?.Invoke(name, now);
        return now;
    }

    /// <summary>Sets one module's state, returning true if it changed.</summary>
    /// <exception cref="KeyNotFoundException">No such module.</exception>
    public bool Set(string name, bool active)
    {
        var runner = Find(name);

        lock (_gate)
        {
            if (runner.IsActive == active)
            {
                return false;
            }

            runner.IsActive = active;
        }

        StateChanged?.Invoke(name, active);
        return true;
    }

    /// <summary>
    /// Releases every module, returning how many were holding.
    /// </summary>
    /// <remarks>
    /// The panic button, and the shutdown path. Releasing a module that is already released is a
    /// no-op, so this is safe to call at any time and more than once.
    /// </remarks>
    public int ReleaseAll()
    {
        List<string> released = [];

        lock (_gate)
        {
            foreach (var runner in _runners.Values.Where(r => r.IsActive))
            {
                runner.IsActive = false;
                released.Add(runner.Name);
            }
        }

        foreach (var name in released)
        {
            StateChanged?.Invoke(name, false);
        }

        return released.Count;
    }

    /// <summary>Asks one module to inject resets.</summary>
    /// <exception cref="KeyNotFoundException">No such module.</exception>
    /// <exception cref="InvalidOperationException">That module cannot reset.</exception>
    public void RequestReset(string name)
    {
        var runner = Find(name);

        if (!runner.SupportsReset)
        {
            throw new InvalidOperationException(
                $"'{name}' is a hold, not a reconnect - it has nothing to reset");
        }

        runner.RequestReset();
    }

    /// <summary>A reading of every module, in name order.</summary>
    public IReadOnlyList<ModuleStatus> Snapshot()
    {
        lock (_gate)
        {
            return [.. _runners.Values.Select(r => r.Status)];
        }
    }

    /// <summary>
    /// Runs every module until cancelled.
    /// </summary>
    /// <remarks>
    /// Returns when the token is signalled or when any module fails. A module that throws takes
    /// the host down rather than leaving a half-running session: a handle that has stopped
    /// diverting while the status line still claims the module is holding is worse than stopping.
    /// Every module is released on the way out, whichever way that happens.
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Linked, so one module failing can stop the others. Task.WhenAll alone would wait for
        // every module to finish, which means a dead handle leaves the host sitting on the healthy
        // ones while the status line still claims a working session.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = _runners.Values.Select(r => r.RunAsync(linked.Token)).ToArray();

        try
        {
            var first = await Task.WhenAny(tasks).ConfigureAwait(false);

            if (first.IsFaulted)
            {
                await linked.CancelAsync().ConfigureAwait(false);
            }

            List<Exception> faults = [];

            foreach (var task in tasks)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The normal way a session ends, and the way the others end once one fails.
                }
                catch (Exception ex)
                {
                    faults.Add(ex);
                }
            }

            // One failure keeps its own type, so a caller can still catch what WinDivert threw.
            if (faults.Count == 1)
            {
                throw faults[0];
            }

            if (faults.Count > 1)
            {
                throw new AggregateException(
                    "more than one module failed", faults);
            }
        }
        finally
        {
            ReleaseAll();
        }
    }

    private IModuleRunner Find(string name) =>
        _runners.TryGetValue(name, out var runner)
            ? runner
            : throw new KeyNotFoundException(
                $"no module called '{name}'. Hosted: {string.Join(", ", _runners.Keys)}");

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Release before disposing. A runner's handle closing restores traffic by itself, but a
        // module left marked active would be reported as holding by anything reading a last
        // snapshot on the way out.
        ReleaseAll();

        foreach (var runner in _runners.Values)
        {
            runner.Dispose();
        }
    }
}
