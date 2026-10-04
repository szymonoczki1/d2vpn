using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Versioning;

using D2Vpn.Core.Hosting;
using D2Vpn.Core.Input;
using D2Vpn.Core.Network;
using D2Vpn.Core.Processes;

namespace D2Vpn.Ui.ViewModels;

/// <summary>
/// The window's state: the hosted modules, the keys bound to them, and the control pipe.
/// </summary>
/// <remarks>
/// <para>
/// This window hosts the modules itself rather than attaching to a running <c>d2vpn host</c>. One
/// process, one thing to launch, one elevation prompt - and it still serves the same control pipe,
/// so a macro and the window drive the same modules instead of competing for them.
/// </para>
/// <para>
/// Everything it drives lives in <c>D2Vpn.Core</c> and was built before any of this existed:
/// <see cref="ModuleHost"/> for the modules, <see cref="IControllable"/> for one toggle,
/// <see cref="IModuleOptions"/> for a switch, <see cref="HostConfig"/> for the file. The window adds
/// no behaviour of its own, which is why there is no logic here worth testing separately.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _configPath;

    private ModuleHost? _host;
    private NamedPipeControlServer? _server;
    private HotkeyWatcher? _hotkeys;
    private List<IModuleRunner> _runners = [];
    private HostConfig _config;
    private TimerSettingsViewModel? _timerPage;
    private SidebarItem? _selected;
    private CategoryViewModel? _category;
    private string _status = string.Empty;
    private bool _failed;

    /// <summary>Loads a config and prepares to host it.</summary>
    public MainViewModel(string configPath, HostConfig config)
    {
        _configPath = configPath;
        _config = config;

        Instances = new InstanceWatcher(
            silence: TimeSpan.FromSeconds(config.Timer.EndAfterS),
            dormantAfter: TimeSpan.FromSeconds(config.Timer.HideAfterS));
    }

    /// <summary>
    /// How long the current activity has lasted, as the 30k traffic shows it.
    /// </summary>
    /// <remarks>
    /// Always here, even when nothing feeds it, so turning the 30k module on is the only thing
    /// needed rather than a restart. <see cref="TimerIsFed"/> is how a front end knows whether what
    /// it is showing means anything.
    /// </remarks>
    public InstanceWatcher Instances { get; }

    /// <summary>
    /// True when a hosted handle is reporting 30k traffic to <see cref="Instances"/>.
    /// </summary>
    /// <remarks>
    /// The timer has no handle of its own and cannot have one: a second handle on 30k would sit
    /// behind the first by priority and go blind for the length of every hold. So it rides the
    /// handle the 30k modules already opened, and without those modules there is nothing to ride.
    /// </remarks>
    public bool TimerIsFed { get; private set; }

    /// <summary>Every hosted module, across all categories.</summary>
    public ObservableCollection<ModuleViewModel> Modules { get; } = [];

    /// <summary>The rail: the sections a module can belong to.</summary>
    public ObservableCollection<CategoryViewModel> Categories { get; } = [];

    /// <summary>The section the sidebar is listing.</summary>
    /// <remarks>
    /// Switching remembers where you were in each one, so moving to game pause and back does not
    /// drop you at the top of a list of eleven.
    /// </remarks>
    public CategoryViewModel? Category
    {
        get => _category;
        set
        {
            if (_category is { } previous)
            {
                previous.Selected = Selected;
            }

            if (!Set(ref _category, value) || value is null)
            {
                return;
            }

            Selected = value.Selected is { } remembered && value.Items.Contains(remembered)
                ? remembered
                : value.Items.FirstOrDefault();
        }
    }

    /// <summary>
    /// What the detail panel is showing - a module, or a settings page.
    /// </summary>
    /// <remarks>
    /// Typed as the common base so the panel can pick its layout from the runtime type through a
    /// DataTemplate, rather than the window growing a switch on what it is displaying.
    /// </remarks>
    public SidebarItem? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    /// <summary>The selected item when it is a module, for the handlers that need one.</summary>
    public ModuleViewModel? SelectedModule => _selected as ModuleViewModel;

    /// <summary>A one-line description of what the session is doing.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>True when startup failed, so the window can say so instead of looking idle.</summary>
    public bool Failed
    {
        get => _failed;
        private set => Set(ref _failed, value);
    }

    /// <summary>How many modules are holding.</summary>
    public int ActiveCount => _host?.ActiveCount ?? 0;

    /// <summary>The control pipe path, for display.</summary>
    public string PipePath => _server?.Path ?? $@"\\.\pipe\{_config.Pipe}";

    /// <summary>Where the config was loaded from.</summary>
    public string ConfigPath => _configPath;

    /// <summary>The overlay's settings, as loaded.</summary>
    public OverlayConfig Overlay => _config.Overlay;

    /// <summary>
    /// Opens the handles, binds the keys and starts serving. Returns false if it could not.
    /// </summary>
    public bool Start()
    {
        try
        {
            _runners = [.. ModuleRunnerFactory.CreateAll(_config.Resolve())];

            if (_config.Pause.Enabled)
            {
                var process = NtProcessControl.Open(_config.Pause.Process);
                var timeout = _config.Pause.SafetyTimeoutS > 0
                    ? TimeSpan.FromSeconds(_config.Pause.SafetyTimeoutS)
                    : (TimeSpan?)null;

                _runners.Add(new GamePauseRunner(new GamePauseSession(process, timeout), "pause")
                {
                    IsEnabled = _config.Pause.ModuleEnabled
                });
            }

            _host = new ModuleHost(_runners);

            // From inside the handle the 30k modules already own, which is the only place a
            // withheld packet is visible. See IPacketFeed.
            foreach (var feed in _runners.OfType<IPacketFeed>()
                .Where(f => f.Group == DestinyPorts.Instance))
            {
                feed.PacketObserved += Instances.Observe;
                TimerIsFed = true;
            }
        }
        catch (Exception ex)
        {
            // A handle that will not open is the common failure - another limiter still running, or
            // the driver refusing. Saying so beats a window that looks fine and does nothing.
            Status = ex.Message;
            Failed = true;
            Dispose();

            // The rail is still built. The overlay and the timer are configurable whether or not
            // anything is hosted, and this is the one moment someone is most likely to go looking
            // - a window with no sections at all reads as broken rather than as nothing running.
            BuildCategories();
            Category = Categories.FirstOrDefault();

            return false;
        }

        var byName = _config.Modules.ToDictionary(m => ModuleSpec.Parse(m.Name).Name);

        foreach (var controllable in _runners.SelectMany(r => r.Controls))
        {
            var module = new ModuleViewModel(
                controllable, byName.GetValueOrDefault(controllable.Name));

            // Every switch the operator flips is written straight back, so enabled, autoresync and
            // buffering are all where they were left next launch - the same as a keybind.
            module.SettingChanged += Save;

            // Two controls have no entry in Modules, so without this they would show as unbound
            // however the config was written: pause keeps its key in the Pause section, and a
            // reconnect keeps its in resetBinds.
            if (module.Config is null)
            {
                module.BindKey = controllable.Name == PauseName
                    ? ParseOrNull(_config.Pause.Bind)
                    : ParseOrNull(ResetBindFor(controllable.Name));
            }

            Modules.Add(module);
        }

        BuildCategories();

        Category = Categories.FirstOrDefault(c => !c.IsEmpty) ?? Categories.FirstOrDefault();

        _host.StateChanged += (_, _) => Refresh();

        _server = new NamedPipeControlServer(_host, _config.Pipe);
        _ = _server.RunAsync(_shutdown.Token);
        _ = _host.RunAsync(_shutdown.Token);

        BindKeys();

        Status = $"hosting {Modules.Count} module{(Modules.Count == 1 ? "" : "s")} "
            + $"across {_runners.Count} handle{(_runners.Count == 1 ? "" : "s")}";
        return true;
    }

    /// <summary>
    /// Sorts the hosted modules into the rail's sections.
    /// </summary>
    /// <remarks>
    /// The extension point. A macro manager becomes another category here and another geometry in
    /// <c>Theme/Icons.xaml</c>; nothing else in the window has to know about it. Empty categories
    /// are left out rather than shown as a section with nothing in it, which reads as broken
    /// rather than as planned.
    /// </remarks>
    private void BuildCategories()
    {
        var traffic = new CategoryViewModel(
            "Traffic", "IconTraffic",
            "No traffic module could be started.");

        var system = new CategoryViewModel(
            "System", "IconSystem",
            "Game pause is not enabled in the config.");

        foreach (var module in Modules)
        {
            (module.Kind == ModuleKind.Process ? system : traffic).Items.Add(module);
        }

        // Settings is always listed, even when no module could start: the overlay is configurable
        // whether or not anything is hosted, and a rail that vanished would be a dead end.
        var settings = new CategoryViewModel(
            "Settings", "IconSettings", "Nothing to configure here yet.");

        settings.Items.Add(new OverlaySettingsViewModel(_config.Overlay, ApplyOverlay));

        // Kept, because it shows a running figure and so needs the fast tick. Everything else on
        // these pages only changes when something is clicked.
        _timerPage = new TimerSettingsViewModel(
            _config.Timer, Instances, TimerIsFed, ApplyTimer);

        settings.Items.Add(_timerPage);

        foreach (var category in new[] { traffic, system, settings })
        {
            if (!category.IsEmpty)
            {
                Categories.Add(category);
            }
        }
    }

    /// <summary>Flips one module.</summary>
    public void Toggle(ModuleViewModel module)
    {
        module.Toggle();
        Refresh();
    }

    /// <summary>Drops every hold at once.</summary>
    public void ReleaseAll()
    {
        _host?.ReleaseAll();
        Refresh();
    }

    /// <summary>Re-reads everything the host owns. The slower of the window's two ticks.</summary>
    public void Refresh()
    {
        foreach (var module in Modules)
        {
            module.Refresh();
        }

        RaiseShared();
    }

    /// <summary>
    /// Re-reads only the on states and the running timers. The fast tick.
    /// </summary>
    public void TickLive()
    {
        foreach (var module in Modules)
        {
            module.RaiseLive();
        }

        _timerPage?.Tick();

        // Silence cannot be noticed from a packet, there being none, so something has to ask.
        // Leaving this out of the measurement command once made the backstop dead code: an error
        // code followed by seventy seconds of nothing ended no instance at all.
        Instances.Tick(DateTimeOffset.UtcNow);

        RaiseShared();
        OverlayTick?.Invoke();
    }

    /// <summary>
    /// Raised on the fast tick, for anything else that shows a running timer.
    /// </summary>
    /// <remarks>
    /// The overlay rides this rather than keeping a timer of its own, so the two cannot drift apart
    /// and say different things about the same hold.
    /// </remarks>
    public event Action? OverlayTick;

    private void RaiseShared()
    {
        // The rail dots and the footer count follow the on states, so they belong on whichever
        // tick noticed one change - which is the fast one.
        foreach (var category in Categories)
        {
            category.Refresh();
        }

        Raise(nameof(ActiveCount));
    }

    /// <summary>
    /// Shows a message in the footer for a few seconds, then clears it.
    /// </summary>
    /// <remarks>
    /// Transient, because these are confirmations rather than state - "saved" is worth seeing once
    /// and is clutter from then on. A failure stays, since it is not something to glance past.
    /// </remarks>
    public async void Flash(string message)
    {
        Status = message;

        var shown = message;
        await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(true);

        if (!Failed && Status == shown)
        {
            Status = string.Empty;
        }
    }

    /// <summary>
    /// Rebinds a key and writes the config back, or reports why it could not.
    /// </summary>
    /// <param name="module">The module to rebind.</param>
    /// <param name="which">Which of its three keys.</param>
    /// <param name="key">The virtual-key code, or null to unbind.</param>
    /// <returns>Null on success, or a message to show.</returns>
    public string? Rebind(ModuleViewModel module, BindSlot which, int? key)
    {
        // A key doing two things is always a mistake and a silent one, since whichever binding is
        // registered second simply replaces the first. Checked here rather than left to the config
        // writer so the window can refuse before changing anything.
        if (key is { } code)
        {
            foreach (var other in Modules)
            {
                foreach (var slot in Enum.GetValues<BindSlot>())
                {
                    if (ReferenceEquals(other, module) && slot == which)
                    {
                        continue;
                    }

                    if (KeyOf(other, slot) == code)
                    {
                        return $"{KeyName.Describe(code)} is already bound to "
                            + $"{other.Name} {Label(slot)}";
                    }
                }
            }

            if (_releaseKey == code)
            {
                return $"{KeyName.Describe(code)} is the release key";
            }
        }

        switch (which)
        {
            case BindSlot.Toggle: module.BindKey = key; break;
            case BindSlot.Resync: module.ResyncKey = key; break;
            case BindSlot.Buffer: module.BufferKey = key; break;
        }

        BindKeys();
        Save();
        return null;
    }

    /// <summary>The control-surface name game pause is hosted under.</summary>
    private const string PauseName = "pause";

    /// <summary>
    /// Takes a change from the overlay settings page: saves it, and applies it live.
    /// </summary>
    /// <remarks>
    /// Applied before saving, so a setting that cannot be written to disk still takes effect for
    /// this session rather than appearing to do nothing.
    /// </remarks>
    private void ApplyOverlay(OverlayConfig overlay)
    {
        _config = _config with { Overlay = overlay };
        OverlayChanged?.Invoke(overlay);
        Save();
    }

    /// <summary>Raised when the overlay's settings change, so the window can apply them.</summary>
    public event Action<OverlayConfig>? OverlayChanged;

    /// <summary>Takes a change from the timer settings page: saves it, and applies it live.</summary>
    private void ApplyTimer(TimerConfig timer)
    {
        _config = _config with { Timer = timer };

        // Retuned rather than rebuilt. A running activity is exactly when someone would go and
        // adjust these, and rebuilding would reset the timer the moment the number changed.
        Instances.Retune(
            TimeSpan.FromSeconds(timer.EndAfterS),
            TimeSpan.FromSeconds(timer.HideAfterS));

        TimerChanged?.Invoke(timer);
        Save();
    }

    /// <summary>Raised when the timer's settings change.</summary>
    public event Action<TimerConfig>? TimerChanged;

    /// <summary>The timer's settings, as loaded.</summary>
    public TimerConfig Timer => _config.Timer;

    /// <summary>Writes the current binds and switch states back to the config file.</summary>
    /// <remarks>
    /// <para>
    /// Rebuilt from the view models rather than patched, so what is written is what the window
    /// shows. Pause is written back to its own section, since that is where it came from.
    /// </para>
    /// <para>
    /// Only the parts this session actually hosts. The settings pages are reachable when nothing
    /// started - that is deliberate, it is when you most want them - and rebuilding the module list
    /// from an empty sidebar would write that emptiness over a working file. One click on a
    /// settings page after a failed start was enough to erase every module from the config.
    /// </para>
    /// </remarks>
    public void Save()
    {
        try
        {
            if (!Failed)
            {
                _config = WithHostedState(_config);
            }

            File.WriteAllText(_configPath, _config.ToJson());
            Flash($"saved {Path.GetFileName(_configPath)}");
        }
        catch (Exception ex)
        {
            Status = $"could not save {Path.GetFileName(_configPath)}: {ex.Message}";
        }
    }

    /// <summary>
    /// Puts the binds and switch states the window is showing back into a config.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Save"/> so there is one obvious place that reads the view models,
    /// and one obvious condition under which it is allowed to run.
    /// </remarks>
    private HostConfig WithHostedState(HostConfig config)
    {
        var pause = Modules.FirstOrDefault(m => m.Name == PauseName);

        return config with
        {
            Modules = [.. Modules
                .Where(m => m.Kind == ModuleKind.Hold)
                .Select(m => m.ToConfig())],
            ResetBinds = [.. Modules
                .Where(m => m.Kind == ModuleKind.Trigger && m.BindKey is not null)
                .Select(m => new ResetBind
                {
                    Module = GroupOf(m.Name),
                    Bind = KeyName.Describe(m.BindKey!.Value)
                })],
            Pause = pause is null
                ? config.Pause
                : config.Pause with
                {
                    Bind = pause.BindKey is { } key ? KeyName.Describe(key) : null,

                    // Not Enabled, which is whether pause is hosted at all. A switch that
                    // unhosted its own control could not be switched back.
                    ModuleEnabled = pause.IsEnabled
                }
        };
    }

    /// <summary>The key a config bound to a reconnect, found by its group.</summary>
    /// <remarks>
    /// A reconnect is addressed as <c>&lt;group&gt;-rc</c> but stored under the group name, since
    /// that is the shape resetBinds already had before it became its own entry.
    /// </remarks>
    private string? ResetBindFor(string controlName) =>
        _config.ResetBinds
            .FirstOrDefault(r => string.Equals(
                r.Module, GroupOf(controlName), StringComparison.OrdinalIgnoreCase))
            ?.Bind;

    private static string GroupOf(string controlName) =>
        controlName.EndsWith("-rc", StringComparison.Ordinal)
            ? controlName[..^3]
            : controlName;

    private static int? ParseOrNull(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            return KeyName.Parse(name);
        }
        catch (Exception)
        {
            // An unrecognised key name shows as unbound rather than stopping the window opening.
            return null;
        }
    }

    private int? _releaseKey;

    private void BindKeys()
    {
        // Rebuilt wholesale rather than unbound individually: HotkeyWatcher has no unbind, and a
        // fresh watcher is cheaper to reason about than tracking which codes are stale.
        _hotkeys?.Dispose();
        _hotkeys = new HotkeyWatcher(TimeSpan.FromMilliseconds(_config.PollMs));

        foreach (var module in Modules)
        {
            var captured = module;

            if (captured.BindKey is { } toggle)
            {
                _hotkeys.Bind(toggle, () => Toggle(captured));
            }

            if (captured.ResyncKey is { } resync)
            {
                _hotkeys.Bind(resync, () =>
                {
                    captured.ResyncEnabled = !captured.ResyncEnabled;
                    Refresh();
                });
            }

            if (captured.BufferKey is { } buffer)
            {
                _hotkeys.Bind(buffer, () =>
                {
                    captured.BufferEnabled = !captured.BufferEnabled;
                    Refresh();
                });
            }
        }

        _releaseKey = null;

        if (!string.IsNullOrWhiteSpace(_config.ReleaseBind))
        {
            try
            {
                _releaseKey = KeyName.Parse(_config.ReleaseBind);
                _hotkeys.Bind(_releaseKey.Value, ReleaseAll);
            }
            catch (Exception)
            {
                // An unrecognised release key in the config is not worth refusing to start over.
                _releaseKey = null;
            }
        }

        _hotkeys.Start();
    }

    private static int? KeyOf(ModuleViewModel module, BindSlot slot) => slot switch
    {
        BindSlot.Toggle => module.BindKey,
        BindSlot.Resync => module.ResyncKey,
        _ => module.BufferKey
    };

    private static string Label(BindSlot slot) => slot switch
    {
        BindSlot.Toggle => "hold",
        BindSlot.Resync => "resync",
        _ => "buffer"
    };

    /// <inheritdoc/>
    public void Dispose()
    {
        // Order matters: stop the keys first so nothing can toggle a module that is being torn
        // down, then release every hold, then close the handles.
        _hotkeys?.Dispose();
        _hotkeys = null;

        if (!_shutdown.IsCancellationRequested)
        {
            _shutdown.Cancel();
        }

        _host?.Dispose();
        _host = null;

        _server?.Dispose();
        _server = null;

        _shutdown.Dispose();
    }
}

/// <summary>Which of a module's three keys is being rebound.</summary>
public enum BindSlot
{
    /// <summary>The key that holds and releases.</summary>
    Toggle,

    /// <summary>The key that flips autoresync.</summary>
    Resync,

    /// <summary>The key that flips buffering.</summary>
    Buffer
}
