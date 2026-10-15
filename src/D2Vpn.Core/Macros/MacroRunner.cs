using System.Runtime.Versioning;

using D2Vpn.Core.Hosting;
using D2Vpn.Core.Input;

namespace D2Vpn.Core.Macros;

/// <summary>
/// The loadout macros, hosted like any other module.
/// </summary>
/// <remarks>
/// <para>
/// One runner, one control per preset - the same shape as 3074's four toggles behind one handle.
/// Each preset carries its own key, its own timings and its own rotation, so they are really
/// several macros that happen to agree about where the loadouts are.
/// </para>
/// <para>
/// Only one may run at a time, and starting a second stops the first. Two sequences clicking the
/// same twenty squares at once is not two macros, it is neither.
/// </para>
/// <para>
/// It opens no handle, so <see cref="HandleKey"/> is null and it cannot collide with a traffic
/// module. The hold it drives is the reason this lives inside d2vpn rather than beside it: ticking
/// the chosen uplink for exactly the length of the swap is a method call here, where from a
/// separate
/// script it would be a round trip through a pipe an unelevated script cannot open.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MacroRunner : IModuleRunner
{
    private readonly IInputSink _input;
    private readonly Func<string, IControllable?> _uplink;
    private readonly Func<MacroConfig> _config;
    private readonly Func<IMacroClock> _clocks;
    private readonly List<PresetControl> _controls = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Cancelled only when the module is torn down, never by a run being stopped.
    /// </summary>
    /// <remarks>
    /// What the uplink linger waits on. Stopping a macro is a reason to stop clicking, not a
    /// reason to drop the hold early - that is the whole point of a setting for how long it stays
    /// held afterwards. Closing the window is a different matter: nothing should keep it open.
    /// </remarks>
    private readonly CancellationTokenSource _shutdown = new();

    private PresetControl? _running;

    /// <summary>Creates the macro module, with one control per preset.</summary>
    /// <param name="name">The stem of each control's address, such as <c>macro</c>.</param>
    /// <param name="input">Where the clicks go.</param>
    /// <param name="config">
    /// Read fresh at each run, so changing a delay or a rotation in the window takes effect on the
    /// next press rather than on the next launch.
    /// </param>
    /// <param name="uplink">
    /// How to find a hold by name, or null if it is not hosted. A function because the macro is
    /// built alongside the traffic modules and would otherwise have to be built after them, and it
    /// takes the name because which hold a preset drives is a per-preset setting.
    /// </param>
    /// <param name="clocks">Supplies a clock per run. Replaced in tests.</param>
    public MacroRunner(
        string name,
        IInputSink input,
        Func<MacroConfig> config,
        Func<string, IControllable?> uplink,
        Func<IMacroClock>? clocks = null)
    {
        Name = name;
        _input = input;
        _config = config;
        _uplink = uplink;
        _clocks = clocks ?? (() => new MacroClock());

        // Addressed by position, not by name. A preset renamed in the window would otherwise
        // change its control address, which would lose its key and break anything driving it over
        // the pipe - a rename is not meant to be a re-bind.
        var presets = config().Presets;

        for (var i = 0; i < presets.Count; i++)
        {
            _controls.Add(new PresetControl(this, i, $"{name}-{i + 1}")
            {
                IsEnabled = presets[i].Enabled
            });
        }
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public IReadOnlyList<IControllable> Controls => _controls;

    /// <inheritdoc/>
    /// <remarks>Null: this opens no packet handle, so it can never collide with one.</remarks>
    public string? HandleKey => null;

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing to run in the background: a macro runs when it is pressed. Waits for cancellation so
    /// the host's task set behaves the same for every runner.
    /// </remarks>
    public Task RunAsync(CancellationToken cancellationToken) =>
        Task.Delay(Timeout.Infinite, cancellationToken);

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var control in _controls)
        {
            control.Stop();
        }

        // After the stops, so a run that is lingering on its uplink gives it up now rather than
        // holding the window open for the rest of its wait.
        _shutdown.Cancel();

        foreach (var control in _controls)
        {
            control.AwaitEnd();
        }

        _shutdown.Dispose();
    }

    /// <summary>The preset a control stands for, or a default if it has been deleted.</summary>
    private MacroPreset PresetAt(int index)
    {
        var presets = _config().Presets;
        return index < presets.Count ? presets[index] : new MacroPreset();
    }

    /// <summary>
    /// Stops whatever is running and records the new one, if any.
    /// </summary>
    /// <remarks>
    /// Under one lock, so two keys pressed together cannot both believe they won.
    /// </remarks>
    private bool Claim(PresetControl control)
    {
        PresetControl? previous;

        lock (_gate)
        {
            if (ReferenceEquals(_running, control))
            {
                return false;
            }

            previous = _running;
            _running = control;
        }

        previous?.Stop();
        return true;
    }

    /// <summary>Lets go, if this control is still the one holding.</summary>
    private void Release(PresetControl control)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_running, control))
            {
                _running = null;
            }
        }
    }

    /// <summary>One preset, as the control surface sees it.</summary>
    private sealed class PresetControl(MacroRunner owner, int index, string name) : IControllable
    {
        private readonly Lock _gate = new();

        private CancellationTokenSource? _run;
        private Task? _task;
        private DateTimeOffset? _activeSince;
        private TimeSpan _totalActive;

        public string Name => name;

        public ModuleKind Kind => ModuleKind.Macro;

        public bool IsEnabled { get; set; } = true;

        public bool SupportsReset => false;

        public IModuleOptions? Options => null;

        /// <summary>The preset this runs, read fresh so a rename or a retune is picked up.</summary>
        public MacroPreset Preset => owner.PresetAt(index);

        /// <summary>How many times it has been run.</summary>
        public int Runs { get; private set; }

        /// <summary>How many loadout clicks the last run made.</summary>
        public int LastSwaps { get; private set; }

        /// <summary>Why the last run stopped early, or null if it finished.</summary>
        public string? LastError { get; private set; }

        /// <summary>
        /// Why the last run held nothing, or null if it held what it asked for.
        /// </summary>
        /// <remarks>
        /// A hold that is missing and a hold that is switched off are different things to do
        /// something about, so they are said differently.
        /// </remarks>
        public string? Unavailable { get; private set; }

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

        public bool IsActive
        {
            get => ActiveSince is not null;
            set
            {
                if (value)
                {
                    Start();
                }
                else
                {
                    Stop();
                }
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Every packet count is zero, and honestly so: this module touches none. They exist
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

        public IReadOnlyList<ModuleMetric> Metrics =>
        [
            new("runs", Runs.ToString()),
            new("swaps last run", LastSwaps.ToString()),
            new("total", ModuleMetrics.Seconds(TotalActive))
        ];

        public void RequestReset()
        {
            // Nothing to reset. A macro is a sequence, not a connection.
        }

        private void Start()
        {
            if (!IsEnabled || IsActive || !owner.Claim(this))
            {
                return;
            }

            CancellationToken token;

            lock (_gate)
            {
                _run = new CancellationTokenSource();
                token = _run.Token;
                _activeSince = DateTimeOffset.UtcNow;
                LastError = null;
                Runs++;
            }

            var config = owner._config();
            var settings = Preset.ToSettings(config.Grid);

            // Its own thread rather than the pool. The run spins out the last milliseconds of each
            // delay to keep the gap accurate, and a pool thread doing that is one fewer for
            // everything else in the process - including the packet pumps.
            _task = Task.Factory.StartNew(
                () => Execute(settings, token),
                token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public void Stop()
        {
            CancellationTokenSource? run;

            lock (_gate)
            {
                run = _run;
            }

            run?.Cancel();
        }

        /// <summary>Waits for a stopped run to finish putting things back.</summary>
        public void AwaitEnd()
        {
            try
            {
                _task?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Shutting down. The uplink is restored in the run's own finally.
            }
        }

        /// <summary>One run, start to finish, including putting the uplink back.</summary>
        private void Execute(MacroSettings settings, CancellationToken token)
        {
            var wanted = settings.TickUplink || settings.DisableBuffer;
            var found = wanted ? owner._uplink(settings.Uplink) : null;

            // A disabled hold is not a hold. IsEnabled is enforced by ModuleHost, which the macro
            // does not go through - it writes to the control directly, so without this the macro
            // was a third way in that nobody had closed, and it would hold a module the operator
            // had switched off.
            var uplink = found is { IsEnabled: true } ? found : null;

            // The clicks still happen either way - a macro that refused to run because a traffic
            // module was missing would be worse - but it must not look like the hold happened.
            Unavailable = !wanted || uplink is not null
                ? null
                : found is null
                    ? $"{settings.Uplink} is not hosted"
                    : $"{settings.Uplink} is disabled";

            // Remembered rather than assumed. Turning the hold off afterwards when it was already
            // on before would release a hold the operator had started by hand.
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

                    // After the hold, not before. Turning the buffer off is what makes withheld
                    // packets be destroyed rather than replayed, and doing it while nothing is
                    // held changes nothing to destroy.
                    if (settings.DisableBuffer
                        && uplink.Options is { SupportsBuffer: true } options)
                    {
                        options.BufferEnabled = false;
                    }
                }

                var macro = new LoadoutMacro(owner._input, owner._clocks());
                LastSwaps = macro.Run(settings.ToPlan(), token);
            }
            catch (Exception ex)
            {
                // A plan that will not run, or Windows refusing the input. Reported on the module
                // rather than thrown: this is on a thread of its own, and a macro that failed must
                // not take the session with it.
                LastError = ex.Message;
            }
            finally
            {
                // Held past the end of the clicking, if asked. The hold is what the swap is hidden
                // behind and the last click needs to be inside it as much as the first, so this is
                // the margin for the game still catching up once the macro has finished.
                //
                // On the shutdown token rather than the run's own: a stop ends the clicking, and
                // dropping the hold early would be the one thing this setting exists to prevent.
                if (settings.UplinkAfter > TimeSpan.Zero && wasActive is false)
                {
                    PreciseWait.Wait(settings.UplinkAfter, owner._shutdown.Token);
                }

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

                owner.Release(this);
            }
        }

        /// <summary>
        /// Puts the uplink back the way it was found.
        /// </summary>
        /// <remarks>
        /// In a finally, and swallowing its own failures, because this is the half that matters. A
        /// macro that threw halfway and left the uplink held is a connection that stays broken
        /// until somebody notices and releases it by hand.
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
    }
}
