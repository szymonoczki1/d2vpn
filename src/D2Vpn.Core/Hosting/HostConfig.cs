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
    /// <summary>Whether to host it at all.</summary>
    public bool Enabled { get; init; }

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

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,

        // Match the template. The reader is case-insensitive either way, but a file the window
        // rewrites should not come back in different casing from the one it was given.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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

        var repaired = parsed.Repair(out warnings);

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

    /// <summary>The specs and their numbers, in config order.</summary>
    public IReadOnlyList<(ModuleSpec Spec, ModuleTuning Tuning)> Resolve() =>
        [.. Modules.Select(m => (ModuleSpec.Parse(m.Name), m.ToTuning()))];
}
