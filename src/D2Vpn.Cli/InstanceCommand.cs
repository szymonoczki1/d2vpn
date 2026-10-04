using System.Runtime.Versioning;

using D2Vpn.Core.Diagnostics;
using D2Vpn.Core.Hosting;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Cli;

/// <summary>
/// Reports how long the current instance has lasted, from 30k traffic.
/// </summary>
/// <remarks>
/// <para>
/// Both the measurement and the thing being measured. The rule - a connection opening to a server
/// this session was not already talking to - has exactly one observation behind it, from finding 6,
/// where a reconnect reopened to the same server and the real instance change forty seconds later
/// went to a different one. This command is how that becomes more than one observation.
/// </para>
/// <para>
/// Sniff mode: packets are copied and continue up the stack untouched, so it cannot affect a
/// session and can be left running for as long as it takes.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class InstanceCommand
{
    public static async Task<int> ExecuteAsync(
        PortGroup group, short priority, string? logPath, CancellationToken cancellationToken)
    {
        // From the same config the window uses, so the command measures what the app will do.
        var timer = TimerFor(logPath);

        var watcher = new InstanceWatcher(
            silence: TimeSpan.FromSeconds(timer.EndAfterS),
            dormantAfter: TimeSpan.FromSeconds(timer.HideAfterS));
        using var log = logPath is null ? null : new SessionLog(logPath);

        log?.Write("config", $"group={group.Name} priority={priority}");

        var started = DateTimeOffset.UtcNow;

        watcher.Changed += change =>
        {
            var lasted = change.Previous is null
                ? string.Empty
                : $"  (previous lasted {(change.At - started).TotalSeconds:F1}s)";

            log?.Write("change", $"server={Mask(change.Server)} reason={change.Reason}");

            Console.WriteLine(
                $"\n[{DateTime.Now:HH:mm:ss}] instance changed - {change.Reason}{lasted}");
        };

        Console.WriteLine($"watching {group} for instance changes  (sniff mode - nothing is modified)");
        Console.WriteLine();
        Console.WriteLine("What to do, in the game:");
        Console.WriteLine("  1. leave it in orbit or the tower for a few seconds");
        Console.WriteLine("  2. travel somewhere - a patrol zone, a strike, back to orbit");
        Console.WriteLine("  3. do that two or three more times, and say roughly when");
        Console.WriteLine();
        Console.WriteLine("The timer should restart as each new area finishes loading, and stop");
        Console.WriteLine("when you leave one. Either firing while you stand still, or failing to");
        Console.WriteLine("fire on a real change, is what this is looking for.");
        Console.WriteLine();
        // Read from the configuration rather than written in, so the description cannot drift
        // from the waits actually in force - it said 30s while the default had become 45s.
        Console.WriteLine("Three ways it can end:");
        Console.WriteLine("  the server closing its connection   - leaving an activity");
        Console.WriteLine($"  {timer.EndAfterS}s of silence on the group         - the backstop, for an error");
        Console.WriteLine("    code, which drops the connection without closing it politely.");
        Console.WriteLine($"    That is {timer.EndAfterS / 3.02:F0} of the 3.02s heartbeats this port carries.");
        Console.WriteLine("  nothing at all for a reconnect      - that is an RST, not a close");
        Console.WriteLine("");
        Console.WriteLine($"After {timer.HideAfterS}s of silence the timer is hidden but keeps running, so a");
        Console.WriteLine("loading screen does not cost the elapsed time. Coming back to the same");
        Console.WriteLine($"server inside {timer.EndAfterS}s continues it rather than starting again.");
        Console.WriteLine();
        Console.WriteLine("Press your reconnect key during the run as well: it must NOT end the");
        Console.WriteLine("instance. This is sniff mode, so it runs alongside 'd2vpn reconnect");
        Console.WriteLine("30k' - a sniffing handle takes nothing away from a diverting one.");
        Console.WriteLine();

        if (log is not null)
        {
            Console.WriteLine($"  log: {log.Path}");
            Console.WriteLine();
        }

        Console.WriteLine("Ctrl+C to stop.");
        Console.WriteLine();

        var module = new PassThroughModule(group.Name, group);
        using var source = new WinDivertPacketSource(group, priority);

        var packets = 0L;

        var pump = Task.Run(async () =>
        {
            await foreach (var packet in source.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                packets++;
                watcher.Observe(packet);
            }
        }, cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;

                // Silence cannot be noticed from a packet, there being none, so something
                // has to ask. Leaving this out made the backstop dead code: an error code
                // followed by seventy seconds of nothing ended no instance at all.
                watcher.Tick(now);

                // Padded to a fixed width. The status is rewritten over itself with a carriage
                // return, so a shorter line leaves the tail of a longer one behind - "not in one"
                // replacing "instance 50.9s hidden, 25s left" printed the packet count twice.
                var line = $"{(now - started).TotalSeconds,6:N0}s  "
                    + $"{State(watcher, now)}  "
                    + $"changes {watcher.Changes}  "
                    + $"ends {watcher.Ends}  "
                    + $"quiet {watcher.Quiet(now).TotalSeconds,5:F1}s  "
                    + $"packets {packets}";

                Console.Write("\r" + line.PadRight(Width));

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C.
        }

        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine($"instance changes  : {watcher.Changes}");
        Console.WriteLine($"instances ended   : {watcher.Ends}");
        Console.WriteLine("  by a close      : "
            + $"{watcher.Ends - watcher.QuietEnds - watcher.Superseded}");
        Console.WriteLine($"  by going quiet  : {watcher.QuietEnds}");
        Console.WriteLine($"  superseded      : {watcher.Superseded}");
        Console.WriteLine($"ignored after rc  : {watcher.Suppressed}");
        Console.WriteLine($"packets seen      : {packets}");

        if (watcher.Changes == 0)
        {
            Console.WriteLine();
            Console.WriteLine("Nothing fired. Either no connection opened on this group while the");
            Console.WriteLine("run lasted, or the signal is not what finding 6 suggested - which is");
            Console.WriteLine("the thing worth knowing either way.");
        }

        if (log is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"{log.Count} events written to {log.Path}");
        }

        _ = module;
        return 0;
    }

    /// <summary>
    /// The timer settings, from the shared config, falling back to the defaults.
    /// </summary>
    /// <remarks>
    /// Read rather than hardcoded so this command measures the waits the window will actually use.
    /// A measurement of different numbers from the ones that ship is not a measurement of anything.
    /// </remarks>
    private static TimerConfig TimerFor(string? _)
    {
        try
        {
            var path = ConfigLocation.Default;

            return File.Exists(path) ? HostConfig.Load(path).Timer : new TimerConfig();
        }
        catch (Exception)
        {
            // A config that will not load is the host's problem to report, not this command's.
            return new TimerConfig();
        }
    }

    /// <summary>
    /// How wide the status line is held, so it always covers whatever it replaces.
    /// </summary>
    /// <remarks>
    /// Fixed rather than read from the console, which reports zero when output is redirected and
    /// would then pad to nothing.
    /// </remarks>
    private const int Width = 96;

    /// <summary>In an instance and for how long, or not in one.</summary>
    private static string State(InstanceWatcher watcher, DateTimeOffset now)
    {
        if (!watcher.InInstance)
        {
            return "        not in one";
        }

        var elapsed = $"instance {watcher.Elapsed(now).TotalSeconds,7:F1}s";

        // Dormant is still in the instance, so it says so rather than going blank - and shows how
        // long is left before it would be declared over.
        return watcher.IsDormant(now)
            ? $"{elapsed} hidden, {watcher.UntilEnd(now).TotalSeconds:F0}s left"
            : elapsed;
    }

    /// <summary>
    /// A server address with its host part removed.
    /// </summary>
    /// <remarks>
    /// Enough to tell one server from another in a log, without putting a real address in a file
    /// that might end up in the repository. The repository's own rule, applied where it is easy to
    /// forget.
    /// </remarks>
    private static string Mask(System.Net.IPAddress address)
    {
        var parts = address.ToString().Split('.');

        return parts.Length == 4
            ? $"{parts[0]}.{parts[1]}.x.x"
            : "non-ipv4";
    }
}
