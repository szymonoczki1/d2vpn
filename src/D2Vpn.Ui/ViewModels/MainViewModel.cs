using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Versioning;

using D2Vpn.Core.Hosting;
using D2Vpn.Core.Input;
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
    private ModuleViewModel? _selected;
    private string _status = "starting";
    private bool _failed;

    /// <summary>Loads a config and prepares to host it.</summary>
    public MainViewModel(string configPath, HostConfig config)
    {
        _configPath = configPath;
        _config = config;
    }

    /// <summary>The modules, in config order, for the sidebar.</summary>
    public ObservableCollection<ModuleViewModel> Modules { get; } = [];

    /// <summary>The module the detail panel is showing.</summary>
    public ModuleViewModel? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

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

                _runners.Add(new GamePauseRunner(new GamePauseSession(process, timeout), "pause"));
            }

            _host = new ModuleHost(_runners);
        }
        catch (Exception ex)
        {
            // A handle that will not open is the common failure - another limiter still running, or
            // the driver refusing. Saying so beats a window that looks fine and does nothing.
            Status = ex.Message;
            Failed = true;
            Dispose();
            return false;
        }

        var byName = _config.Modules.ToDictionary(m => ModuleSpec.Parse(m.Name).Name);

        foreach (var controllable in _runners.SelectMany(r => r.Controls))
        {
            var module = new ModuleViewModel(
                controllable, byName.GetValueOrDefault(controllable.Name));

            // Pause has no entry in Modules - its bind lives in the Pause section - so it would
            // otherwise show as unbound however the config was written.
            if (module.Config is null && controllable.Name == PauseName)
            {
                module.BindKey = ParseOrNull(_config.Pause.Bind);
            }

            Modules.Add(module);
        }

        Selected = Modules.FirstOrDefault();

        _host.StateChanged += (_, _) => Refresh();

        _server = new NamedPipeControlServer(_host, _config.Pipe);
        _ = _server.RunAsync(_shutdown.Token);
        _ = _host.RunAsync(_shutdown.Token);

        BindKeys();

        Status = $"hosting {Modules.Count} modules across {_runners.Count} handles";
        return true;
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

    /// <summary>Re-reads what the host owns. Called by the window's refresh tick.</summary>
    public void Refresh()
    {
        foreach (var module in Modules)
        {
            module.Refresh();
        }

        Raise(nameof(ActiveCount));
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

    /// <summary>Writes the current binds and switch states back to the config file.</summary>
    /// <remarks>
    /// Rebuilt from the view models rather than patched, so what is written is what the window
    /// shows. Pause is written back to its own section, since that is where it came from.
    /// </remarks>
    public void Save()
    {
        try
        {
            var pause = Modules.FirstOrDefault(m => m.Name == PauseName);

            _config = _config with
            {
                Modules = [.. Modules
                    .Where(m => m.Name != PauseName)
                    .Select(m => m.ToConfig())],
                Pause = pause is null
                    ? _config.Pause
                    : _config.Pause with
                    {
                        Bind = pause.BindKey is { } key ? KeyName.Describe(key) : null
                    }
            };

            File.WriteAllText(_configPath, _config.ToJson());
            Status = $"saved {Path.GetFileName(_configPath)}";
        }
        catch (Exception ex)
        {
            Status = $"could not save {Path.GetFileName(_configPath)}: {ex.Message}";
        }
    }

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
