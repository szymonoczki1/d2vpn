using System.Text.Json;
using System.Text.Json.Serialization;

namespace D2Vpn.Core.Hosting;

/// <summary>One module in a config file: what to host, what key toggles it, and its numbers.</summary>
public sealed record ModuleConfig
{
    /// <summary>The module name, such as <c>3074-ul</c>. Parsed by <see cref="ModuleSpec"/>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// The key that toggles it, or <see langword="null"/> for a module driven only over the pipe.
    /// </summary>
    public string? Bind { get; init; }

    /// <summary>
    /// Whether the module participates at all.
    /// </summary>
    /// <remarks>
    /// A layer above holding: a disabled module keeps its handle but refuses to be activated, by a
    /// key or by the control pipe. Useful for keeping a module configured without it being
    /// reachable by a mistimed keypress.
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether the resync window, or the TCP keepalive, starts on.</summary>
    public bool Resync { get; init; } = true;

    /// <summary>Whether withheld packets start being kept.</summary>
    public bool Buffer { get; init; } = true;

    /// <summary>
    /// A key that flips <see cref="Resync"/> while running, or null for none.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Bind"/> because these are different questions: one asks whether the
    /// module is holding, the other how it holds. Most setups bind the hold and leave the switches
    /// to the pipe, which is why both are optional.
    /// </remarks>
    public string? ResyncBind { get; init; }

    /// <summary>A key that flips <see cref="Buffer"/> while running, or null for none.</summary>
    public string? BufferBind { get; init; }

    /// <inheritdoc cref="ModuleTuning.SlowMs"/>
    public int? SlowMs { get; init; }

    /// <inheritdoc cref="ModuleTuning.ResyncCooldownS"/>
    public int? ResyncCooldownS { get; init; }

    /// <inheritdoc cref="ModuleTuning.BufferPackets"/>
    public int? BufferPackets { get; init; }

    /// <inheritdoc cref="ModuleTuning.BufferAgeS"/>
    public int? BufferAgeS { get; init; }

    /// <summary>The numbers, as the factory wants them.</summary>
    public ModuleTuning ToTuning() => new()
    {
        Enabled = Enabled,
        Resync = Resync,
        Buffer = Buffer,
        SlowMs = SlowMs,
        ResyncCooldownS = ResyncCooldownS,
        BufferPackets = BufferPackets,
        BufferAgeS = BufferAgeS
    };
}

/// <summary>Game pause, as a config file describes it.</summary>
public sealed record PauseConfig
{
    /// <summary>
    /// Whether to host it at all.
    /// </summary>
    /// <remarks>
    /// The equivalent of a module being present in <see cref="HostConfig.Modules"/> rather than of
    /// <see cref="ModuleConfig.Enabled"/>. False means it does not appear in the window at all,
    /// which is why the window's own switch writes to <see cref="ModuleEnabled"/> instead - a
    /// switch that removed its own control from the window could not be switched back.
    /// </remarks>
    public bool Enabled { get; init; }

    /// <summary>
    /// Whether it participates, once hosted. The window's "Module enabled" switch.
    /// </summary>
    public bool ModuleEnabled { get; init; } = true;

    /// <summary>The key that toggles it.</summary>
    public string? Bind { get; init; }

    /// <summary>Process name without the extension.</summary>
    public string Process { get; init; } = "destiny2";

    /// <summary>
    /// How long a pause may run before it is ended automatically, or 0 for no limit.
    /// </summary>
    /// <remarks>
    /// On by default, unlike every traffic hold. A hold that goes wrong ends in an error code and
    /// the game reconnects itself; a suspend that goes wrong ends in a process that will never run
    /// again.
    /// </remarks>
    public int SafetyTimeoutS { get; init; } = 30;
}

/// <summary>
/// The instance timer: how long the game has been in the current activity.
/// </summary>
/// <remarks>
/// Both numbers are waits on silence, and the shorter one only hides. A loading screen stops 30k
/// traffic without ending anything, so the timer keeps running while it is out of sight and picks
/// up where it left off if the same server comes back.
/// </remarks>
public sealed record TimerConfig
{
    /// <summary>Whether to track it at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Seconds of silence before the timer is hidden, while still running.
    /// </summary>
    /// <remarks>
    /// 30k carries a heartbeat every 3.02 s, so fifteen is five missed beats - long enough not to
    /// flicker on an ordinary gap, short enough to go away promptly on a load screen.
    /// </remarks>
    public int HideAfterS { get; init; } = 15;

    /// <summary>
    /// Seconds of silence before the instance is declared over.
    /// </summary>
    /// <remarks>
    /// The backstop for a departure that sends no FIN, which was measured happening. Returning to
    /// the same server before this elapses continues the timer rather than restarting it.
    /// </remarks>
    public int EndAfterS { get; init; } = 45;
}

/// <summary>
/// One panel of the overlay: whether it is drawn, and where.
/// </summary>
/// <remarks>
/// The modules and the instance timer are separate panels so they can sit in different corners -
/// or in the same one, by attaching the timer to the modules. Size and transparency are not here:
/// two panels at different scales on one screen read as a mistake rather than a choice.
/// </remarks>
public sealed record OverlayPanel
{
    /// <summary>Whether this panel is drawn at all.</summary>
    public bool Show { get; init; } = true;

    /// <summary>Which corner of the game window it sits in.</summary>
    public OverlayCorner Corner { get; init; } = OverlayCorner.TopRight;

    /// <summary>Gap from the window's vertical edge, in pixels.</summary>
    public int MarginX { get; init; } = 28;

    /// <summary>Gap from the window's horizontal edge, in pixels.</summary>
    public int MarginY { get; init; } = 28;
}

/// <summary>Which of the overlay's panels something refers to.</summary>
public enum OverlayPanelKind
{
    /// <summary>The panel listing held modules.</summary>
    Modules,

    /// <summary>The panel showing the instance timer.</summary>
    Timer
}

/// <summary>Which corner of the game window the overlay sits in.</summary>
public enum OverlayCorner
{
    /// <summary>Top left.</summary>
    TopLeft,

    /// <summary>Top right.</summary>
    TopRight,

    /// <summary>Bottom left.</summary>
    BottomLeft,

    /// <summary>Bottom right.</summary>
    BottomRight
}

/// <summary>
/// The in-game overlay: what it shows, where, and when.
/// </summary>
/// <remarks>
/// <para>
/// It appears only while the watched process owns the foreground window, so it is invisible on the
/// desktop and while anything else is in front. It never takes focus and never takes a click - it
/// is a layered, transparent, click-through window - so it cannot swallow an input meant for the
/// game.
/// </para>
/// <para>
/// <b>It cannot draw over exclusive fullscreen.</b> Nothing outside the game can: the display is
/// owned by that process and a composited window is not in the chain. Borderless windowed is the
/// supported arrangement, and is what most people run anyway for alt-tabbing.
/// </para>
/// </remarks>
public sealed record OverlayConfig
{
    /// <summary>Whether to show it at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The process it follows, without the extension.</summary>
    public string Process { get; init; } = "destiny2";

    /// <summary>The panel listing held modules.</summary>
    public OverlayPanel Modules { get; init; } = new() { Corner = OverlayCorner.TopRight };

    /// <summary>The panel showing how long the current instance has lasted.</summary>
    public OverlayPanel Timer { get; init; } = new() { Corner = OverlayCorner.TopLeft };

    /// <summary>
    /// Draw the timer as a row of the module panel rather than as a panel of its own.
    /// </summary>
    /// <remarks>
    /// One panel in one corner when set, two independently placed panels when not. Attached, the
    /// timer's own corner and margins stop meaning anything and it is not drawn separately.
    /// </remarks>
    public bool TimerAttached { get; init; }

    /// <summary>
    /// Where the module panel used to be placed, before there were two of them.
    /// </summary>
    /// <remarks>
    /// Kept only so an existing config is carried across rather than silently reset to the
    /// defaults. <see cref="HostConfig"/> moves these into <see cref="Modules"/> on load and leaves
    /// them null, so they disappear the next time the file is written.
    /// </remarks>
    public OverlayCorner? Corner { get; init; }

    /// <inheritdoc cref="Corner"/>
    public int? MarginX { get; init; }

    /// <inheritdoc cref="Corner"/>
    public int? MarginY { get; init; }

    /// <summary>Text size multiplier, for a 1440p screen or a small one.</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>How solid the panel behind the text is, from 0 to 1.</summary>
    public double Opacity { get; init; } = 0.72;

    /// <summary>Show how long each module has been held.</summary>
    public bool ShowTimers { get; init; } = true;

    /// <summary>
    /// Show modules that are hosted but not currently held.
    /// </summary>
    /// <remarks>
    /// Off by default. The overlay exists to answer "what is on right now", and a list of nine
    /// modules that are mostly off answers it worse than a list of the one that is.
    /// </remarks>
    public bool ShowIdle { get; init; }

    /// <summary>Disappear entirely when nothing is held, rather than showing an empty panel.</summary>
    public bool HideWhenIdle { get; init; } = true;
}

/// <summary>A key bound to <c>reset</c> on one module.</summary>
public sealed record ResetBind
{
    /// <summary>The module to reset. Must be a TCP one.</summary>
    public string Module { get; init; } = string.Empty;

    /// <summary>The key.</summary>
    public string Bind { get; init; } = string.Empty;
}

/// <summary>
/// Everything a hosted session needs, from a file.
/// </summary>
/// <remarks>
/// <para>
/// One source of truth for binds. The host reads this and binds the keys itself, so it works with
/// no macro tooling at all; the AutoHotkey manager will generate a script from the same file rather
/// than keeping a second copy of the mapping. A file that two things disagree about is worse than
/// no file.
/// </para>
/// <para>
/// JSON because it is in the box - no package, and a config a WPF front-end can read with the same
/// code. Comments and trailing commas are allowed when reading, since this is a file a person
/// edits, even though a writer cannot emit comments.
/// </para>
/// <para>
/// Anything omitted keeps its measured default. See <see cref="ModuleTuning"/> for why a config
/// does not restate the calibration.
/// </para>
/// </remarks>
public sealed record HostConfig
{
    /// <summary>The control pipe name.</summary>
    public string Pipe { get; init; } = "d2vpn";

    /// <summary>How often bound keys are polled, in milliseconds.</summary>
    /// <remarks>
    /// 1 ms by default. Some sequences here are frame-sensitive - a bullet is detached from its
    /// shooter inside about two seconds - and at 60 fps an 8 ms poll is half a frame of slop at
    /// each end.
    /// </remarks>
    public int PollMs { get; init; } = 1;

    /// <summary>The modules to host.</summary>
    public IReadOnlyList<ModuleConfig> Modules { get; init; } = [];

    /// <summary>Game pause.</summary>
    public PauseConfig Pause { get; init; } = new();

    /// <summary>A key that releases every module at once.</summary>
    public string? ReleaseBind { get; init; }

    /// <summary>Keys bound to reconnect on a module.</summary>
    public IReadOnlyList<ResetBind> ResetBinds { get; init; } = [];

    /// <summary>The in-game overlay.</summary>
    public OverlayConfig Overlay { get; init; } = new();

    /// <summary>The instance timer.</summary>
    public TimerConfig Timer { get; init; } = new();

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,

        // "topRight" rather than 1. A config is written by hand and read by a person.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,

        // Match the template. The reader is case-insensitive either way, but a file the window
        // rewrites should not come back in different casing from the one it was given.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Reads a config from JSON text.</summary>
    /// <exception cref="ArgumentException">The text is not valid, or the config is not usable.</exception>
    public static HostConfig Parse(string json)
    {
        HostConfig? config;

        try
        {
            config = JsonSerializer.Deserialize<HostConfig>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            // The line and position matter more than the exception type to someone editing a file.
            throw new ArgumentException($"config is not valid JSON: {ex.Message}", ex);
        }

        if (config is null)
        {
            throw new ArgumentException("config is empty");
        }

        config = config.Migrated();
        config.Validate();
        return config;
    }

    /// <summary>Reads a config from a file.</summary>
    public static HostConfig Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// Reads a config, fixing what can be fixed rather than refusing to load.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the window. A front end whose job is editing this file must not be stopped from opening
    /// by the file needing editing - a duplicate key would otherwise leave the only tool that can
    /// resolve it unable to start, and the operator in a text editor, which is what the window
    /// exists to avoid.
    /// </para>
    /// <para>
    /// So a repairable problem drops the smallest thing that resolves it - one bind, one module -
    /// and reports what it dropped. Only a config with nothing left to host, or one that is not
    /// JSON at all, still throws. The CLI keeps the strict <see cref="Load(string)"/>, where a
    /// person is reading the output and can fix it before anything runs.
    /// </para>
    /// </remarks>
    /// <param name="path">The file to read.</param>
    /// <param name="warnings">What had to be changed to make it usable.</param>
    public static HostConfig LoadForgiving(string path, out IReadOnlyList<string> warnings)
    {
        HostConfig? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(path), ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"config is not valid JSON: {ex.Message}", ex);
        }

        if (parsed is null)
        {
            throw new ArgumentException("config is empty");
        }

        var repaired = parsed.Migrated().Repair(out warnings);

        // Whatever is left still has to be hostable; repair only removes things.
        repaired.Validate();
        return repaired;
    }

    /// <summary>
    /// Returns a version of this config that will validate, and says what it had to change.
    /// </summary>
    private HostConfig Repair(out IReadOnlyList<string> warnings)
    {
        List<string> found = [];
        List<ModuleConfig> modules = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (var module in Modules)
        {
            string canonical;

            try
            {
                canonical = ModuleSpec.Parse(module.Name).Name;
            }
            catch (ArgumentException ex)
            {
                found.Add($"dropped '{module.Name}': {ex.Message}");
                continue;
            }

            if (!names.Add(canonical))
            {
                found.Add($"dropped a second '{canonical}'");
                continue;
            }

            modules.Add(module with
            {
                SlowMs = module.SlowMs is { } slow && slow < 1 ? null : module.SlowMs,
                BufferPackets = module.BufferPackets is { } packets && packets < 1
                    ? null
                    : module.BufferPackets
            });
        }

        // One key doing two things is the common one, and it is why this exists: the first use
        // keeps the key and every later one is cleared, so the file still loads and the window can
        // be used to sort it out.
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);

        string? Claim(string? key, string what)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (taken.Add(key))
            {
                return key;
            }

            found.Add($"unbound {what}: '{key}' was already in use");
            return null;
        }

        for (var i = 0; i < modules.Count; i++)
        {
            var module = modules[i];

            modules[i] = module with
            {
                Bind = Claim(module.Bind, module.Name),
                ResyncBind = Claim(module.ResyncBind, $"{module.Name} resync"),
                BufferBind = Claim(module.BufferBind, $"{module.Name} buffer")
            };
        }

        var pause = Pause with
        {
            Bind = Pause.Enabled ? Claim(Pause.Bind, "pause") : Pause.Bind
        };

        var release = Claim(ReleaseBind, "release");

        List<ResetBind> resets = [];

        foreach (var reset in ResetBinds)
        {
            if (!names.Contains(reset.Module))
            {
                found.Add($"dropped a reconnect key for '{reset.Module}', which is not hosted");
                continue;
            }

            if (Claim(reset.Bind, $"reconnect on {reset.Module}") is { } kept)
            {
                resets.Add(reset with { Bind = kept });
            }
        }

        warnings = found;

        return this with
        {
            PollMs = PollMs is < 1 or > 1000 ? 1 : PollMs,
            Overlay = Overlay with
            {
                Scale = Overlay.Scale is < 0.5 or > 4 ? 1.0 : Overlay.Scale,
                Opacity = Overlay.Opacity is < 0 or > 1 ? 0.72 : Overlay.Opacity
            },
            Timer = Repair(Timer),
            Modules = modules,
            Pause = pause,
            ReleaseBind = release,
            ResetBinds = resets
        };
    }

    /// <summary>
    /// Serialises this config.
    /// </summary>
    /// <remarks>
    /// <b>Comments do not survive this.</b> A JSON writer cannot emit them, so a hand-commented
    /// file rewritten from the window comes back as data alone. That is the cost of letting the
    /// window own the file; the commented template is always available again from
    /// <see cref="ConfigTemplate"/> via <c>d2vpn config init</c>.
    /// </remarks>
    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);

    /// <summary>
    /// Moves settings written in an older shape into the current one.
    /// </summary>
    /// <remarks>
    /// The overlay had a single corner and margin before it had two panels. Reading them into the
    /// module panel means an existing file keeps the position it was given, rather than quietly
    /// reverting to the top right the first time it is loaded.
    /// </remarks>
    public HostConfig Migrated()
    {
        if (Overlay.Corner is null && Overlay.MarginX is null && Overlay.MarginY is null)
        {
            return this;
        }

        return this with
        {
            Overlay = Overlay with
            {
                Modules = Overlay.Modules with
                {
                    Corner = Overlay.Corner ?? Overlay.Modules.Corner,
                    MarginX = Overlay.MarginX ?? Overlay.Modules.MarginX,
                    MarginY = Overlay.MarginY ?? Overlay.Modules.MarginY
                },

                // Cleared, so they are not written back and the move happens once.
                Corner = null,
                MarginX = null,
                MarginY = null
            }
        };
    }

    /// <summary>
    /// Checks the config can actually be hosted, throwing with what to fix.
    /// </summary>
    /// <remarks>
    /// Done here rather than at use, so a typo is reported once with a line the operator can act on
    /// instead of surfacing as a missing module halfway through a session.
    /// </remarks>
    /// <exception cref="ArgumentException">Something is wrong with it.</exception>
    public void Validate()
    {
        if (Modules.Count == 0 && !Pause.Enabled)
        {
            throw new ArgumentException(
                "config hosts nothing - add at least one module, or enable pause");
        }

        if (PollMs is < 1 or > 1000)
        {
            throw new ArgumentException($"pollMs {PollMs} is outside 1-1000");
        }

        if (Overlay.Scale is < 0.5 or > 4)
        {
            throw new ArgumentException(
                $"overlay scale {Overlay.Scale} is outside 0.5-4");
        }

        if (Overlay.Opacity is < 0 or > 1)
        {
            throw new ArgumentException(
                $"overlay opacity {Overlay.Opacity} is outside 0-1");
        }

        if (Timer.HideAfterS is < 1 or > 600)
        {
            throw new ArgumentException($"timer hideAfterS {Timer.HideAfterS} is outside 1-600");
        }

        if (Timer.EndAfterS is < 1 or > 600)
        {
            throw new ArgumentException($"timer endAfterS {Timer.EndAfterS} is outside 1-600");
        }

        // Hiding after the instance has already ended would mean the timer was never hidden at
        // all, only stopped.
        if (Timer.HideAfterS > Timer.EndAfterS)
        {
            throw new ArgumentException(
                $"timer hideAfterS {Timer.HideAfterS} is longer than endAfterS {Timer.EndAfterS}, "
                + "so the timer would end before it was ever hidden");
        }

        // Parsing every name up front turns a typo into one clear error rather than a module that
        // silently never appears.
        var specs = new List<ModuleSpec>();

        foreach (var module in Modules)
        {
            specs.Add(ModuleSpec.Parse(module.Name));

            if (module.SlowMs is { } slow && slow < 1)
            {
                throw new ArgumentException($"{module.Name}: slowMs {slow} must be positive");
            }

            if (module.BufferPackets is { } packets && packets < 1)
            {
                throw new ArgumentException(
                    $"{module.Name}: bufferPackets {packets} must be positive");
            }
        }

        ModuleSpec.EnsureNoConflicts(specs);

        var hosted = specs.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var reset in ResetBinds)
        {
            if (!hosted.Contains(reset.Module))
            {
                throw new ArgumentException(
                    $"resetBinds names '{reset.Module}', which is not hosted. Hosted: "
                    + string.Join(", ", hosted));
            }
        }

        // One key doing two things is always a mistake, and a silent one: whichever binding was
        // registered second simply replaces the first.
        var keys = Modules
            .Where(m => !string.IsNullOrWhiteSpace(m.Bind))
            .Select(m => (Key: m.Bind!, What: m.Name))
            .Concat(Modules
                .Where(m => !string.IsNullOrWhiteSpace(m.ResyncBind))
                .Select(m => (Key: m.ResyncBind!, What: $"{m.Name} resync")))
            .Concat(Modules
                .Where(m => !string.IsNullOrWhiteSpace(m.BufferBind))
                .Select(m => (Key: m.BufferBind!, What: $"{m.Name} buffer")))
            .Concat(ResetBinds
                .Where(r => !string.IsNullOrWhiteSpace(r.Bind))
                .Select(r => (Key: r.Bind, What: $"reset {r.Module}")))
            .ToList();

        if (!string.IsNullOrWhiteSpace(ReleaseBind))
        {
            keys.Add((ReleaseBind, "release"));
        }

        if (Pause.Enabled && !string.IsNullOrWhiteSpace(Pause.Bind))
        {
            keys.Add((Pause.Bind, "pause"));
        }

        var clash = keys
            .GroupBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        if (clash is not null)
        {
            throw new ArgumentException(
                $"'{clash.Key}' is bound to more than one thing: "
                + string.Join(" and ", clash.Select(k => k.What)));
        }
    }

    /// <summary>Puts a timer configuration back inside its limits.</summary>
    private static TimerConfig Repair(TimerConfig timer)
    {
        var hide = timer.HideAfterS is < 1 or > 600 ? 15 : timer.HideAfterS;
        var end = timer.EndAfterS is < 1 or > 600 ? 45 : timer.EndAfterS;

        return timer with
        {
            HideAfterS = hide,
            EndAfterS = hide > end ? hide : end
        };
    }

    /// <summary>The specs and their numbers, in config order.</summary>
    public IReadOnlyList<(ModuleSpec Spec, ModuleTuning Tuning)> Resolve() =>
        [.. Modules.Select(m => (ModuleSpec.Parse(m.Name), m.ToTuning()))];
}
