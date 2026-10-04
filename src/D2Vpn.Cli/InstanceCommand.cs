using System.Runtime.Versioning;

using D2Vpn.Core.Diagnostics;
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
        var watcher = new InstanceWatcher();
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
        Console.WriteLine("The timer should reset as each new area finishes loading, and at no");
        Console.WriteLine("other time. A reset that fires while you are standing still, or one");
        Console.WriteLine("that fails to fire on a change, is what this is looking for.");
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

                Console.Write($"\r{(now - started).TotalSeconds,6:N0}s  "
                    + $"{State(watcher, now)}  "
                    + $"changes {watcher.Changes}  "
                    + $"ends {watcher.Ends}  "
                    + $"packets {packets}   ");

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

    /// <summary>In an instance and for how long, or not in one.</summary>
    private static string State(InstanceWatcher watcher, DateTimeOffset now) =>
        watcher.InInstance
            ? $"instance {watcher.Elapsed(now).TotalSeconds,7:F1}s"
            : "        not in one";

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
