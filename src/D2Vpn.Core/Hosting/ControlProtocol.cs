namespace D2Vpn.Core.Hosting;

/// <summary>What executing one control line produced.</summary>
/// <param name="Response">The single line to send back.</param>
/// <param name="Shutdown">True if the caller asked the host to stop.</param>
public readonly record struct ControlResult(string Response, bool Shutdown);

/// <summary>
/// The text protocol the control surface speaks, independent of how bytes get there.
/// </summary>
/// <remarks>
/// <para>
/// One request line in, <b>exactly one</b> response line out. That is the whole framing: no
/// lengths, no JSON, no multi-line replies to reassemble. The reason is the consumer - AutoHotkey
/// v2 has no JSON parser in the box, and a script that can call <c>ReadLine</c> once and split on
/// spaces needs no library at all. A WPF front-end loses nothing by it.
/// </para>
/// <para>
/// Replies start with <c>ok</c> or <c>err</c>, so a caller can branch on the first token without
/// understanding the rest.
/// </para>
/// <para>
/// Commands:
/// </para>
/// <code>
///   ping                 -> ok pong
///   list                 -> ok 3074-ul 27k-ul 30k
///   status               -> ok 3074-ul=0 27k-ul=1 30k=0
///   stat &lt;name&gt;     -> ok &lt;name&gt; active=1 seen=.. dropped=.. ..
///   toggle &lt;name&gt;   -> ok &lt;name&gt;=1
///   on &lt;name&gt;       -> ok &lt;name&gt;=1
///   off &lt;name&gt;      -> ok &lt;name&gt;=0
///   release              -> ok released=2
///   enable &lt;name&gt;   -> ok 30k enabled=1
///   disable &lt;name&gt;  -> ok 30k enabled=0
///   opts &lt;name&gt;     -> ok 3074-ul resync=on buffer=on
///   set &lt;name&gt; resync off  -> ok 3074-ul resync=off buffer=on
///   set &lt;name&gt; buffer on   -> ok 3074-ul resync=off buffer=on
///   reset &lt;name&gt;    -> ok reset=&lt;name&gt;
///   quit                 -> ok bye
/// </code>
/// <para>
/// Everything is parsed case-insensitively, and module names are matched the same way, because a
/// macro author typing <c>30K</c> meaning <c>30k</c> should not be debugging a protocol.
/// </para>
/// </remarks>
public static class ControlProtocol
{
    /// <summary>Runs one line against a host.</summary>
    /// <remarks>
    /// Never throws for a bad request: a malformed line from a macro becomes an <c>err</c> reply,
    /// because a control connection dropping mid-session would leave the operator with modules
    /// they cannot release.
    /// </remarks>
    public static ControlResult Execute(string? line, ModuleHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var parts = (line ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            return new ControlResult("err empty command", false);
        }

        var verb = parts[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? parts[1] : null;

        try
        {
            return verb switch
            {
                "ping" => Ok("pong"),
                "list" => Ok(string.Join(' ', host.Names)),
                "status" => Ok(string.Join(
                    ' ',
                    host.Snapshot().Select(s => $"{s.Name}={(s.IsActive ? 1 : 0)}"))),
                "stat" => WithName(argument, name => Ok(Stat(host, name))),
                "toggle" => WithName(argument, name => Toggle(host, name)),
                "on" => WithName(argument, name => SetTo(host, name, true)),
                "off" => WithName(argument, name => SetTo(host, name, false)),
                "release" => Ok($"released={host.ReleaseAll()}"),
                "enable" => WithName(argument, name => Enable(host, name, true)),
                "disable" => WithName(argument, name => Enable(host, name, false)),
                "opts" => WithName(argument, name => Ok(Opts(host, name))),
                "set" => Set(host, parts),
                "reset" => WithName(argument, name => Reset(host, name)),
                "quit" or "exit" => new ControlResult("ok bye", true),
                _ => new ControlResult($"err unknown command '{parts[0]}'", false)
            };
        }
        catch (KeyNotFoundException ex)
        {
            return new ControlResult($"err {ex.Message}", false);
        }
        catch (InvalidOperationException ex)
        {
            return new ControlResult($"err {ex.Message}", false);
        }
    }

    private static ControlResult Toggle(ModuleHost host, string name)
    {
        var resolved = Resolve(host, name);
        return Ok($"{resolved}={Flag(host.Toggle(resolved))}");
    }

    private static ControlResult SetTo(ModuleHost host, string name, bool active)
    {
        var resolved = Resolve(host, name);
        host.Set(resolved, active);

        // The state, not whether this call changed it. A macro that sends "on" twice wants to know
        // the module is holding, not that it was already holding.
        return Ok($"{resolved}={Flag(active)}");
    }

    /// <summary>
    /// <c>set &lt;name&gt; resync|buffer on|off</c>, the switches that change while running.
    /// </summary>
    private static ControlResult Set(ModuleHost host, string[] parts)
    {
        if (parts.Length < 4)
        {
            return new ControlResult(
                "err set needs a module, a switch and a state: set 3074-ul resync off", false);
        }

        var resolved = Resolve(host, parts[1]);
        var options = host.OptionsFor(resolved)
            ?? throw new InvalidOperationException(
                $"'{resolved}' has nothing to set");

        var state = parts[3].ToLowerInvariant() switch
        {
            "on" or "1" or "true" => true,
            "off" or "0" or "false" => false,
            _ => throw new InvalidOperationException(
                $"'{parts[3]}' is not a state - use on or off")
        };

        switch (parts[2].ToLowerInvariant())
        {
            case "resync" or "autoresync" or "keepalive":
                if (!options.SupportsResync)
                {
                    throw new InvalidOperationException(
                        $"'{resolved}' has no resync or keepalive to switch");
                }

                options.ResyncEnabled = state;
                break;

            case "buffer" or "buffering":
                options.BufferEnabled = state;
                break;

            default:
                throw new InvalidOperationException(
                    $"'{parts[2]}' is not a switch - use resync or buffer");
        }

        return Ok($"{resolved} {options.ToLine()}");
    }

    private static string Opts(ModuleHost host, string name)
    {
        var resolved = Resolve(host, name);

        return host.OptionsFor(resolved) is { } options
            ? $"{resolved} {options.ToLine()}"
            : $"{resolved} resync=n/a buffer=n/a";
    }

    private static ControlResult Enable(ModuleHost host, string name, bool enabled)
    {
        var resolved = Resolve(host, name);
        host.SetEnabled(resolved, enabled);

        // The state, not whether this call changed it, for the same reason "on" reports it.
        return Ok($"{resolved} enabled={(enabled ? 1 : 0)}");
    }

    private static ControlResult Reset(ModuleHost host, string name)
    {
        var resolved = Resolve(host, name);
        host.RequestReset(resolved);
        return Ok($"reset={resolved}");
    }

    private static string Stat(ModuleHost host, string name)
    {
        var resolved = Resolve(host, name);

        return host.Snapshot().First(s =>
            string.Equals(s.Name, resolved, StringComparison.OrdinalIgnoreCase)).ToLine();
    }

    /// <summary>
    /// Maps a caller's spelling onto the hosted name, so matching is case-insensitive.
    /// </summary>
    private static string Resolve(ModuleHost host, string name) =>
        host.Names.FirstOrDefault(n =>
            string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException(
            $"no module called '{name}'. Hosted: {string.Join(", ", host.Names)}");

    private static ControlResult WithName(string? argument, Func<string, ControlResult> act) =>
        string.IsNullOrWhiteSpace(argument)
            ? new ControlResult("err that command needs a module name", false)
            : act(argument);

    private static ControlResult Ok(string payload) =>
        new(payload.Length == 0 ? "ok" : $"ok {payload}", false);

    private static int Flag(bool active) => active ? 1 : 0;
}
