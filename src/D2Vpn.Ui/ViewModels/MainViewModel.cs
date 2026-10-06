using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Versioning;

using D2Vpn.Core.Api;
using D2Vpn.Core.Hosting;
using D2Vpn.Core.Input;
using D2Vpn.Core.Macros;
using D2Vpn.Core.Interception;
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
    /// <summary>True when the traffic modules opened their handles.</summary>
    /// <remarks>
    /// What <see cref="Save"/> keys off. The sidebar is rebuilt from the view models, so writing
    /// the module list when none started would write that emptiness over a working config.
    /// </remarks>
    private bool _trafficHosted;

    private BungieActivityWatcher? _api;
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
        // One try each, because the three fail for unrelated reasons and none of them is a reason
        // to lose the others. A WinDivert handle needs elevation and no other limiter holding an
        // overlapping filter; game pause needs the game to be running; the macro needs neither.
        // Sharing a try meant the game being closed cost you the macro, which does not know the
        // game exists.
        List<string> refused = [];

        Host(refused, "traffic", () =>
        {
            _runners.AddRange(ModuleRunnerFactory.CreateAll(_config.Resolve()));
            _trafficHosted = true;
        });

        if (_config.Pause.Enabled)
        {
            Host(refused, "game pause", () =>
            {
                var process = NtProcessControl.Open(_config.Pause.Process);
                var timeout = _config.Pause.SafetyTimeoutS > 0
                    ? TimeSpan.FromSeconds(_config.Pause.SafetyTimeoutS)
                    : (TimeSpan?)null;

                _runners.Add(new GamePauseRunner(new GamePauseSession(process, timeout), "pause")
                {
                    IsEnabled = _config.Pause.ModuleEnabled
                });
            });
        }

        if (_config.Macro.Enabled)
        {
            Host(refused, "the macro", () =>
                // The uplink is resolved when the macro runs rather than now, because the traffic
                // runners are built in the same breath as this one and the lookup has to see them.
                _runners.Add(new MacroRunner(
                    MacroName,
                    new SyntheticInput(),
                    () => _config.Macro,
                    Find)));
        }

        try
        {
            _host = new ModuleHost(_runners);

            // From inside the handle the 30k modules already own, which is the only place a
            // withheld packet is visible. See IPacketFeed.
            foreach (var feed in _runners.OfType<IPacketFeed>()
                .Where(f => f.Group == DestinyPorts.Instance))
            {
                feed.PacketObserved += OnInstanceTraffic;
                TimerIsFed = true;
            }
        }
        catch (Exception ex)
        {
            // Game pause failing to find its process, or the macro refusing a plan. Rarer than a
            // handle, and there is nothing left to host afterwards, so this one does end startup.
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
            var preset = PresetIndexOf(controllable.Name);

            var module = new ModuleViewModel(
                controllable, byName.GetValueOrDefault(controllable.Name))
            {
                // Read and written through the index rather than captured, so the panel always
                // sees the preset as it stands now - including after a save from somewhere else.
                Macro = preset is { } at
                    ? new MacroSettingsViewModel(
                        () => PresetAt(at),
                        updated => ApplyPreset(at, updated),
                        _config.Macro.Grid,
                        Find)
                    : null
            };

            // Every switch the operator flips is written straight back, so enabled, autoresync and
            // buffering are all where they were left next launch - the same as a keybind.
            module.SettingChanged += Save;

            // Two controls have no entry in Modules, so without this they would show as unbound
            // however the config was written: pause keeps its key in the Pause section, and a
            // reconnect keeps its in resetBinds.
            if (module.Config is null)
            {
                module.BindKey = controllable.Name switch
                {
                    PauseName => ParseOrNull(_config.Pause.Bind),
                    _ when preset is { } bound => ParseOrNull(PresetAt(bound).Bind),
                    _ => ParseOrNull(ResetBindFor(controllable.Name))
                };
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
        StartApi();

        // What did not start matters more than what did, and it has nowhere else to appear.
        Status = refused.Count > 0
            ? string.Join("  ·  ", refused)
            : $"hosting {Modules.Count} module{(Modules.Count == 1 ? "" : "s")} "
                + $"across {_runners.Count} handle{(_runners.Count == 1 ? "" : "s")}";

        return !Failed;
    }

    /// <summary>
    /// Hosts one group of modules, noting rather than throwing if it will not start.
    /// </summary>
    /// <remarks>
    /// So a failure costs only the group it belongs to. The window is still worth having for
    /// whatever did start, and the thing that refused has to be said somewhere - the status line
    /// is the only place it can appear.
    /// </remarks>
    private void Host(List<string> refused, string what, Action build)
    {
        try
        {
            build();
        }
        catch (Exception ex)
        {
            refused.Add($"{what}: {ex.Message}");
            Failed = true;
        }
    }

    /// <summary>
    /// Starts following the API, if it is turned on and has what it needs.
    /// </summary>
    /// <remarks>
    /// Silently does nothing when it is not set up. Every reason it might not be - no key, no
    /// name, turned off - is already stated on the Timer page, and a session that hosts modules
    /// perfectly well should not open with a complaint about an optional extra.
    /// </remarks>
    private void StartApi()
    {
        StopApi();

        if (!_config.Timer.Enabled
            || !_config.Timer.UseApi
            || BungieResponses.ParseBungieName(_config.Timer.BungieName) is null
            || BungieKey.Read() is not { } key)
        {
            return;
        }

        _api = new BungieActivityWatcher(
            Instances, new BungieClient(key), _config.Timer.BungieName!);

        _api.Start();
    }

    /// <summary>Stops following, and lets go of the connection.</summary>
    private void StopApi()
    {
        _api?.Dispose();
        _api = null;
    }

    /// <summary>What the API is doing, for the Timer page.</summary>
    public BungieActivityWatcher? Api => _api;

    /// <summary>
    /// Hands a 30k packet to the instance watcher, unless the timer is turned off.
    /// </summary>
    /// <remarks>
    /// Checked here rather than by unsubscribing, so the setting takes effect at once and in both
    /// directions. Turning the timer off has to actually stop it: it has a figure of its own in
    /// the Timer tab, and one still counting under a label saying it was off would be the window
    /// lying about its own state.
    /// </remarks>
    private void OnInstanceTraffic(PacketInfo packet)
    {
        if (_config.Timer.Enabled)
        {
            Instances.Observe(packet);
        }
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

        // The category the rail was built for. A macro is not traffic and not the game process,
        // and filing it under either would make the rail a list of things that are nearly right.
        var macros = new CategoryViewModel(
            "Macros", "IconMacros",
            "The loadout macro is not enabled in the config.");

        foreach (var module in Modules)
        {
            var category = module.Kind switch
            {
                ModuleKind.Process => system,
                ModuleKind.Macro => macros,
                _ => traffic
            };

            category.Items.Add(module);
        }

        // Settings is always listed, even when no module could start: the overlay is configurable
        // whether or not anything is hosted, and a rail that vanished would be a dead end.
        var settings = new CategoryViewModel(
            "Settings", "IconSettings", "Nothing to configure here yet.");

        settings.Items.Add(new OverlaySettingsViewModel(_config.Overlay, ApplyOverlay));

        // Kept, because it shows a running figure and so needs the fast tick. Everything else on
        // these pages only changes when something is clicked.
        _timerPage = new TimerSettingsViewModel(
            _config.Timer, Instances, TimerIsFed, () => _api, ApplyTimer);

        settings.Items.Add(_timerPage);

        foreach (var category in new[] { traffic, system, macros, settings })
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
        if (_config.Timer.Enabled)
        {
            Instances.Tick(DateTimeOffset.UtcNow);
        }

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

    /// <summary>The stem of each macro control's address: macro-1, macro-2 and so on.</summary>
    private const string MacroName = "macro";

    /// <summary>Which preset a macro control stands for, or null if it is not one.</summary>
    /// <remarks>
    /// By position rather than by name, so renaming a preset in the window does not change its
    /// address and lose its key. A rename is not meant to be a re-bind.
    /// </remarks>
    private static int? PresetIndexOf(string controlName) =>
        controlName.StartsWith(MacroName + "-", StringComparison.Ordinal)
            && int.TryParse(
                controlName[(MacroName.Length + 1)..],
                System.Globalization.CultureInfo.InvariantCulture,
                out var number)
            && number >= 1
                ? number - 1
                : null;

    /// <summary>One hosted control by name, or null if it is not hosted.</summary>
    private IControllable? Find(string name) => _runners
        .SelectMany(r => r.Controls)
        .FirstOrDefault(c => c.Name == name);

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
        var wasEnabled = _config.Timer.Enabled;

        _config = _config with { Timer = timer };

        // Turned off, so there is nothing being timed any more. Left alone it would keep showing
        // the instance it was in when it stopped, and switching back on hours later would carry
        // that stale server set in - which reads as having never left.
        if (wasEnabled && !timer.Enabled)
        {
            Instances.Clear();
        }

        // Retuned rather than rebuilt. A running activity is exactly when someone would go and
        // adjust these, and rebuilding would reset the timer the moment the number changed.
        Instances.Retune(
            TimeSpan.FromSeconds(timer.EndAfterS),
            TimeSpan.FromSeconds(timer.HideAfterS));

        // Rebuilt rather than reconfigured. The name, the key and whether it runs at all are all
        // decided at construction, and this happens when somebody clicks a switch - not often
        // enough for tearing down one HTTP client to be worth avoiding.
        StartApi();

        TimerChanged?.Invoke(timer);
        Save();
    }

    /// <summary>One preset by position, or a default if the list is shorter than that.</summary>
    private MacroPreset PresetAt(int index)
    {
        var presets = _config.Macro.Presets;
        return index < presets.Count ? presets[index] : new MacroPreset();
    }

    /// <summary>
    /// Takes a change to one preset: saves it, and uses it on that preset's next run.
    /// </summary>
    /// <remarks>
    /// Nothing is pushed at a running macro. Its settings are read when the key is pressed, so a
    /// run in progress finishes the way it started rather than changing shape underneath itself.
    /// </remarks>
    private void ApplyPreset(int index, MacroPreset preset)
    {
        var presets = _config.Macro.Presets.ToList();

        // Grown rather than refused. A config hand-edited down to two presets still has five
        // controls hosted, and the fifth one's panel should write a fifth preset rather than
        // silently do nothing.
        while (presets.Count <= index)
        {
            presets.Add(new MacroPreset { Name = $"preset {presets.Count + 1}" });
        }

        presets[index] = preset;

        _config = _config with { Macro = _config.Macro with { Presets = presets } };
        Save();

        // The heading and the sidebar carry the preset's name, so a rename has to reach them.
        foreach (var module in Modules)
        {
            module.Refresh();
        }
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
    /// Section by section, and only the ones this session actually hosts. The settings pages are
    /// reachable when nothing started - that is deliberate, it is when you most want them - and
    /// rebuilding the module list from an empty sidebar would write that emptiness over a working
    /// file. One click on a settings page after a failed start was enough to erase every module
    /// from the config.
    /// </para>
    /// <para>
    /// Per section rather than all or nothing, because the failures are per section too. A refused
    /// WinDivert handle costs the traffic modules and nothing else, and a macro bind set in that
    /// session should still be there next time.
    /// </para>
    /// </remarks>
    public void Save()
    {
        try
        {
            _config = WithHostedState(_config);

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
            Modules = _trafficHosted
                ? [.. Modules
                    .Where(m => m.Kind == ModuleKind.Hold)
                    .Select(m => m.ToConfig())]
                : config.Modules,
            ResetBinds = _trafficHosted
                ? [.. Modules
                    .Where(m => m.Kind == ModuleKind.Trigger && m.BindKey is not null)
                    .Select(m => new ResetBind
                    {
                        Module = GroupOf(m.Name),
                        Bind = KeyName.Describe(m.BindKey!.Value)
                    })]
                : config.ResetBinds,
            Macro = WithMacroBinds(config.Macro),
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

    /// <summary>
    /// Puts each macro control's key and switch back into its own preset.
    /// </summary>
    /// <remarks>
    /// Per preset, since each is separately bindable and separately switchable. Presets with no
    /// control hosted - a config listing more than were built - are left exactly as they were
    /// rather than dropped.
    /// </remarks>
    private IReadOnlyList<MacroPreset> PresetsWithBinds(MacroConfig macro)
    {
        var presets = macro.Presets.ToList();

        foreach (var module in Modules)
        {
            if (PresetIndexOf(module.Name) is not { } at || at >= presets.Count)
            {
                continue;
            }

            presets[at] = presets[at] with
            {
                Bind = module.BindKey is { } key ? KeyName.Describe(key) : null,
                Enabled = module.IsEnabled
            };
        }

        return presets;
    }

    /// <summary>The macro section with every hosted preset's key written back.</summary>
    private MacroConfig WithMacroBinds(MacroConfig macro) =>
        Modules.Any(m => PresetIndexOf(m.Name) is not null)
            ? macro with { Presets = PresetsWithBinds(macro) }
            : macro;

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

        // Before the handles, so an in-flight web request is cancelled rather than left holding a
        // connection open after the window has gone.
        StopApi();

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
