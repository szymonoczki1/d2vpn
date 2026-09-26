namespace D2Vpn.Core.Input;

/// <summary>
/// Turns a key name into a Windows virtual-key code.
/// </summary>
/// <remarks>
/// Lives in Core rather than beside the hotkey watcher because it is a lookup table with no
/// platform calls in it, and therefore directly testable. The watcher that uses these codes does
/// need Windows, and stays in the CLI.
///
/// The names accepted here match the ones already in use: existing AutoHotkey scripts send
/// CapsLock to mean "hold 3074 uplink", and a reference limiter binds Mouse4, F9, F10 and F11 to
/// its modules. Numeric codes are accepted too, so a key with no name here is still bindable.
/// </remarks>
public static class KeyName
{
    private static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lmb"] = 0x01,
        ["rmb"] = 0x02,
        ["mmb"] = 0x04,
        ["mouse4"] = 0x05,
        ["mouse5"] = 0x06,
        ["backspace"] = 0x08,
        ["tab"] = 0x09,
        ["enter"] = 0x0D,
        ["shift"] = 0x10,
        ["ctrl"] = 0x11,
        ["alt"] = 0x12,
        ["pause"] = 0x13,
        ["capslock"] = 0x14,
        ["esc"] = 0x1B,
        ["space"] = 0x20,
        ["end"] = 0x23,
        ["home"] = 0x24,
        ["insert"] = 0x2D,
        ["delete"] = 0x2E,
        ["lshift"] = 0xA0,
        ["rshift"] = 0xA1,
        ["lctrl"] = 0xA2,
        ["rctrl"] = 0xA3,
        ["lalt"] = 0xA4,
        ["ralt"] = 0xA5,
        ["semicolon"] = 0xBA,
        ["equals"] = 0xBB,
        ["comma"] = 0xBC,
        ["minus"] = 0xBD,
        ["period"] = 0xBE,
        ["slash"] = 0xBF,
        ["backtick"] = 0xC0,
        ["lbracket"] = 0xDB,
        ["backslash"] = 0xDC,
        ["rbracket"] = 0xDD,
        ["quote"] = 0xDE
    };

    /// <summary>Parses a key name, a single character, or a numeric virtual-key code.</summary>
    /// <exception cref="ArgumentException">The name is not recognised.</exception>
    public static int Parse(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();

        if (Named.TryGetValue(name, out var known))
        {
            return known;
        }

        if (name.Length > 1 && name[0] is 'f' or 'F'
            && int.TryParse(name[1..], out var fn) && fn is >= 1 and <= 24)
        {
            return 0x70 + fn - 1;   // VK_F1 .. VK_F24
        }

        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= '0' and <= '9' or >= 'A' and <= 'Z')
            {
                return c;
            }
        }

        if (int.TryParse(name, out var raw) && raw is > 0 and < 256)
        {
            return raw;
        }

        throw new ArgumentException(
            $"Unrecognised key '{name}'. Try a name like capslock, mouse4 or f9, a single "
            + "letter or digit, or a virtual-key code.");
    }

    /// <summary>Renders a virtual-key code back as a name, for display.</summary>
    public static string Describe(int virtualKey)
    {
        foreach (var (name, code) in Named)
        {
            if (code == virtualKey)
            {
                return name;
            }
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"f{virtualKey - 0x70 + 1}";
        }

        if (virtualKey is >= '0' and <= '9' or >= 'A' and <= 'Z')
        {
            return ((char)virtualKey).ToString().ToLowerInvariant();
        }

        return $"vk{virtualKey}";
    }
}
