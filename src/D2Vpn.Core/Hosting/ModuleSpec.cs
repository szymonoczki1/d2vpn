using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Hosting;

/// <summary>
/// One module named the way the control surface addresses it, parsed from text.
/// </summary>
/// <param name="Name">The canonical name, such as <c>27k-ul</c>.</param>
/// <param name="Group">The ports it acts on.</param>
/// <param name="Direction">Which direction it holds.</param>
/// <param name="Slow">True for the delay-line variant rather than the block.</param>
/// <remarks>
/// <para>
/// The spelling is <c>group[-direction][-slow]</c>: <c>3074-ul</c>, <c>27k-full</c>,
/// <c>30k-slow</c>. A macro or a UI binds to these strings, so parsing them is a contract and
/// lives here with tests rather than in a command's argument handling.
/// </para>
/// <para>
/// <b>Reconnect is not a spec.</b> It was tempting to give it one - <c>30k-rc</c> alongside
/// <c>30k</c> - but a reset is built from a sampler watching the same port group the hold acts on,
/// and the two must share a WinDivert handle: a second handle on an overlapping filter would see a
/// packet the first had already withheld. So a TCP module supports <c>reset</c> as a capability of
/// itself, and there is one name, not two. This is also what the single-module command already
/// does.
/// </para>
/// </remarks>
public readonly record struct ModuleSpec(
    string Name,
    PortGroup Group,
    HoldDirection Direction,
    bool Slow)
{
    /// <summary>
    /// Parses a spec, or throws explaining what was wrong with it.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The group is unknown, the direction is not one of the three, or the combination has no
    /// measured defaults behind it.
    /// </exception>
    public static ModuleSpec Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("a module name cannot be empty");
        }

        var parts = text.Trim().ToLowerInvariant().Split('-', StringSplitOptions.TrimEntries);

        var group = DestinyPorts.ByName(parts[0])
            ?? throw new ArgumentException(
                $"unknown group '{parts[0]}' in '{text}'. Known: "
                + string.Join(", ", DestinyPorts.All.Select(g => g.Name)));

        var slow = false;
        HoldDirection? direction = null;

        foreach (var part in parts.Skip(1))
        {
            switch (part)
            {
                case "slow":
                    slow = true;
                    break;
                case "ul" or "up" or "uplink":
                    direction = HoldDirection.Uplink;
                    break;
                case "dl" or "down" or "downlink":
                    direction = HoldDirection.Downlink;
                    break;
                case "full" or "both":
                    direction = HoldDirection.Both;
                    break;
                default:
                    throw new ArgumentException(
                        $"'{part}' in '{text}' is not a direction or 'slow'. "
                        + "Use ul, dl, full, or slow.");
            }
        }

        // TCP groups are inbound-only holds and take no direction. Saying one is a misunderstanding
        // worth reporting rather than quietly ignoring.
        if (group.Protocol == PortProtocol.Tcp)
        {
            if (direction is not null)
            {
                throw new ArgumentException(
                    $"'{group.Name}' is a TCP inbound hold and takes no direction - "
                    + $"use '{group.Name}'{(slow ? "-slow" : string.Empty)}");
            }

            return new ModuleSpec(Canonical(group, null, slow), group, HoldDirection.Downlink, slow);
        }

        var resolved = direction ?? HoldDirection.Uplink;

        if (MeasuredDefaults.For(group, resolved) is null)
        {
            throw new ArgumentException(
                $"no measured defaults for {group.Name} {resolved} - "
                + "that combination has not been calibrated");
        }

        return new ModuleSpec(Canonical(group, resolved, slow), group, resolved, slow);
    }

    private static string Canonical(PortGroup group, HoldDirection? direction, bool slow)
    {
        var suffix = direction switch
        {
            HoldDirection.Uplink => "-ul",
            HoldDirection.Downlink => "-dl",
            HoldDirection.Both => "-full",
            _ => string.Empty
        };

        return $"{group.Name}{suffix}{(slow ? "-slow" : string.Empty)}";
    }
}
