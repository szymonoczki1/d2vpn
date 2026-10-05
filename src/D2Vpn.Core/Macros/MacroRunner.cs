using System.Runtime.Versioning;

using D2Vpn.Core.Hosting;
using D2Vpn.Core.Input;

namespace D2Vpn.Core.Macros;

/// <summary>
/// The loadout macro, hosted like any other module.
/// </summary>
/// <remarks>
/// <para>
/// A switch you turn on, which turns itself off when the duration is up. Pressing the key again
/// stops it early - a macro that could only be started would be one you had to wait out.
/// </para>
/// <para>
/// It opens no handle, so <see cref="HandleKey"/> is null and it cannot collide with a traffic
/// module. Like game pause, it is a runner only because that is how the host addresses things.
/// </para>
/// <para>
/// The hold it drives is the reason this is inside d2vpn rather than beside it. Ticking 3074's
/// uplink for exactly the length of the swap is a method call here; from a separate script it would
/// be a round trip through the control pipe, which an unelevated script cannot even open.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MacroRunner : IModuleRunner, IControllable
{
    private readonly IInputSink _input;
    private readonly Func<IControllable?> _uplink;
    private readonly Func<MacroSettings> _settings;
    private readonly Func<IMacroClock> _clocks;

    private readonly Lock _gate = new();

    private CancellationTokenSource? _run;
    private Task? _task;
    private DateTimeOffset? _activeSince;
    private TimeSpan _totalActive;

    /// <summary>Creates the macro module.</summary>
    /// <param name="name">What the control surface calls it.</param>
    /// <param name="input">Where the clicks go.</param>
    /// <param name="settings">
    /// Read fresh at each run, so changing the delay or the order in the window takes effect on the
    /// next press rather than on the next launch.
    /// </param>
    /// <param name="uplink">
    /// How to find 3074's uplink hold, or null if it is not hosted. A function because the macro is
    /// built alongside the traffic modules and would otherwise have to be built after them.
    /// </param>
    /// <param name="clocks">Supplies a clock per run. Replaced in tests.</param>
    public MacroRunner(
        string name,
        IInputSink input,
        Func<MacroSettings> settings,
        Func<IControllable?> uplink,
        Func<IMacroClock>? clocks = null)
    {
        Name = name;
        _input = input;
        _settings = settings;
        _uplink = uplink;
        _clocks = clocks ?? (() => new MacroClock());
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public IReadOnlyList<IControllable> Controls => [this];

    /// <inheritdoc/>
    /// <remarks>Null: this opens no packet handle, so it can never collide with one.</remarks>
    public string? HandleKey => null;

    /// <inheritdoc/>
    public ModuleKind Kind => ModuleKind.Macro;

    /// <inheritdoc/>
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc/>
    public bool SupportsReset => false;

    /// <inheritdoc/>
    public IModuleOptions? Options => null;

    /// <inheritdoc/>
    public DateTimeOffset? ActiveSince
    {
        get
        {
            lock (_gate)
            {
                return _activeSince;
            }
        }
    }

    /// <inheritdoc/>
    public TimeSpan TotalActive
    {
        get
        {
            lock (_gate)
            {
                return _activeSince is { } since
                    ? _totalActive + (DateTimeOffset.UtcNow - since)
                    : _totalActive;
            }
        }
    }

    /// <summary>How many times it has been run.</summary>
    public int Runs { get; private set; }

    /// <summary>How many loadout clicks the last run made.</summary>
    public int LastSwaps { get; private set; }

    /// <summary>Why the last run stopped early, or null if it finished.</summary>
    public string? LastError { get; private set; }

    /// <inheritdoc/>
    public bool IsActive
    {
        get => ActiveSince is not null;
        set
        {
            if (value)
            {
                StartRun();
            }
            else
            {
                StopRun();
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Every packet count is zero, and honestly so: this module touches none. The counts exist
    /// because the control protocol is packet-shaped, which is also why <see cref="Metrics"/>
    /// exists for the numbers a macro actually has.
    /// </remarks>
    public ModuleStatus Status => new(
        Name,
        IsActive,
        IsEnabled,
        PacketsSeen: 0,
        PacketsDropped: 0,
        PacketsBuffered: 0,
        PacketsReplayed: 0,
        ResetsInjected: 0,
        HeldSeconds: ActiveSince is { } since
            ? (DateTimeOffset.UtcNow - since).TotalSeconds
            : 0,
        TotalHeldSeconds: TotalActive.TotalSeconds);

    /// <inheritdoc/>
    public IReadOnlyList<ModuleMetric> Metrics =>
    [
        new("runs", Runs.ToString()),
        new("swaps last run", LastSwaps.ToString()),
        new("total", ModuleMetrics.Seconds(TotalActive))
    ];

    /// <inheritdoc/>
    public void RequestReset()
    {
        // Nothing to reset. A macro is a sequence, not a connection.
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing to run in the background: a macro runs when it is pressed. Waits for cancellation so
    /// the host's task set behaves the same for every runner.
    /// </remarks>
    public Task RunAsync(CancellationToken cancellationToken) =>
        Task.Delay(Timeout.Infinite, cancellationToken);

    private void StartRun()
    {
        lock (_gate)
        {
            if (!IsEnabled || _activeSince is not null)
            {
                return;
            }

            _run = new CancellationTokenSource();
            _activeSince = DateTimeOffset.UtcNow;
            LastError = null;
            Runs++;
        }

        var token = _run!.Token;
        var settings = _settings();

        // Its own thread rather than the pool. The run spins out the last milliseconds of each
        // delay to keep the gap accurate, and a pool thread doing that is one fewer for everything
        // else in the process - including the packet pumps.
        _task = Task.Factory.StartNew(
            () => Execute(settings, token),
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void StopRun()
    {
        CancellationTokenSource? run;

        lock (_gate)
        {
            run = _run;
        }

        run?.Cancel();
    }

    /// <summary>One run, start to finish, including putting the uplink back.</summary>
    private void Execute(MacroSettings settings, CancellationToken token)
    {
        var uplink = settings.TickUplink || settings.DisableBuffer ? _uplink() : null;

        // Remembered rather than assumed. Turning the hold off afterwards when it was already on
        // before would release a hold the operator had started by hand.
        var wasActive = uplink?.IsActive;
        var hadBuffer = uplink?.Options is { SupportsBuffer: true } o
            ? o.BufferEnabled
            : (bool?)null;

        try
        {
            if (uplink is not null)
            {
                if (settings.TickUplink)
                {
                    uplink.IsActive = true;
                }

                // After the hold, not before. Turning the buffer off is what makes the withheld
                // packets be destroyed rather than replayed, and doing it while nothing is held
                // changes nothing to destroy.
                if (settings.DisableBuffer && uplink.Options is { SupportsBuffer: true } options)
                {
                    options.BufferEnabled = false;
                }
            }

            var macro = new LoadoutMacro(_input, _clocks());
            LastSwaps = macro.Run(settings.ToPlan(), token);
        }
        catch (Exception ex)
        {
            // A plan that will not run, or Windows refusing the input. Reported on the module
            // rather than thrown: this is on a thread of its own, and a macro that failed must not
            // take the session with it.
            LastError = ex.Message;
        }
        finally
        {
            Restore(uplink, wasActive, hadBuffer);

            lock (_gate)
            {
                if (_activeSince is { } since)
                {
                    _totalActive += DateTimeOffset.UtcNow - since;
                }

                _activeSince = null;
                _run?.Dispose();
                _run = null;
            }
        }
    }

    /// <summary>
    /// Puts the uplink back the way it was found.
    /// </summary>
    /// <remarks>
    /// In a finally, and swallowing its own failures, because this is the half that matters. A
    /// macro that threw halfway and left 3074's uplink held is a connection that stays broken until
    /// somebody notices and releases it by hand.
    /// </remarks>
    private static void Restore(IControllable? uplink, bool? wasActive, bool? hadBuffer)
    {
        if (uplink is null)
        {
            return;
        }

        try
        {
            if (hadBuffer is { } buffer && uplink.Options is { SupportsBuffer: true } options)
            {
                options.BufferEnabled = buffer;
            }

            if (wasActive is false)
            {
                uplink.IsActive = false;
            }
        }
        catch
        {
            // Nothing useful left to do. The release-everything key is the backstop, and it is
            // bound by default.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        StopRun();

        try
        {
            _task?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Shutting down. The uplink is restored in the run's own finally.
        }
    }
}
