using System.Runtime.Versioning;
using D2Vpn.Cli.Hotkeys;
using D2Vpn.Core.Diagnostics;
using D2Vpn.Core.Input;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Cli;

/// <summary>
/// Kills every live connection on a group, so the game reconnects.
/// </summary>
/// <remarks>
/// <para>
/// The only command here that is not a hold. Nothing is blocked and nothing is withheld - traffic
/// passes untouched the whole time, and one keypress injects a reset into each live connection.
/// The game notices at once, shows an error code, and reconnects by itself.
/// </para>
/// <para>
/// Two resets go out per connection, built from packets that connection recently sent. The outbound
/// one tells the peer this machine is finished; the inbound one is delivered up this machine's own
/// network stack and tells it the peer is. Killing only one end leaves the other believing the
/// connection is alive.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ReconnectCommand
{
    public static async Task<int> ExecuteAsync(
        PortGroup group,
        int triggerKey,
        short priority,
        int pollMs,
        int maxIdleSeconds,
        string? logPath,
        CancellationToken cancellationToken)
    {
        var module = new PassThroughModule(group.Name, group);
        using var sampler = new ConnectionSampler(TimeSpan.FromSeconds(maxIdleSeconds));

        using var session = logPath is null ? null : new SessionLog(logPath);
        session?.Write("config",
            $"group={group.Name} maxIdleS={maxIdleSeconds} priority={priority}");

        using var controller =
            new WinDivertTrafficController(module, priority, sampler: sampler);
        using var hotkeys = new HotkeyWatcher(TimeSpan.FromMilliseconds(pollMs));

        var requested = 0;
        var started = DateTimeOffset.UtcNow;

        controller.ResetsSent += (connections, packets) =>
        {
            session?.Write("reset", $"connections={connections} packets={packets}");

            Console.WriteLine(connections == 0
                ? $"\n[{DateTime.UtcNow:HH:mm:ss}] nothing to reset - no live connection had a "
                  + "sample in both directions"
                : $"\n[{DateTime.UtcNow:HH:mm:ss}] reset {connections} connection"
                  + $"{(connections == 1 ? string.Empty : "s")} with {packets} packets");
        };

        var keyLabel = KeyName.Describe(triggerKey);

        hotkeys.Bind(triggerKey, () =>
        {
            requested++;
            session?.Write("requested");
            controller.RequestReset();
        });

        hotkeys.Start();

        Console.WriteLine($"watching {group}");
        Console.WriteLine($"  trigger      : {keyLabel}  (polled every {pollMs} ms)");
        Console.WriteLine($"  live within  : {maxIdleSeconds}s of the last packet");
        Console.WriteLine();
        Console.WriteLine("Nothing is blocked. Traffic passes untouched until you press the key,");
        Console.WriteLine("which injects a reset into each live connection - one to the peer and");
        Console.WriteLine("one to this machine, since killing one end leaves the other believing");
        Console.WriteLine("the connection is alive.");
        Console.WriteLine();
        Console.WriteLine("Expect an error code and an automatic reconnect. A reference limiter");
        Console.WriteLine("reports the effect lasting ~6s solo and ~40s in a fireteam, and a");
        Console.WriteLine("capture here showed the game opening a new connection 330 ms later.");
        Console.WriteLine();

        if (session is not null)
        {
            Console.WriteLine($"  log          : {session.Path}");
            Console.WriteLine();
        }

        Console.WriteLine($"Press {keyLabel} to reconnect. Ctrl+C to exit.");
        Console.WriteLine();

        var run = controller.RunAsync(cancellationToken);

        while (!run.IsCompleted)
        {
            await Task.WhenAny(run, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);

            var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;

            // Live connections is the number that says whether a press will do anything. Zero
            // means no sample in both directions yet, and the key would be a no-op.
            Console.Write($"\r{elapsed,6:N0}s  "
                + $"seen {controller.PacketsSeen}  "
                + $"live {sampler.LiveConnections(DateTimeOffset.UtcNow).Count}  "
                + $"reset {controller.ResetsInjected}   ");
        }

        try
        {
            await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on Ctrl+C.
        }

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine($"requested {requested} time{(requested == 1 ? string.Empty : "s")}");
        Console.WriteLine($"packets seen      : {controller.PacketsSeen}");
        Console.WriteLine($"resets injected   : {controller.ResetsInjected}");
        Console.WriteLine($"dropped           : {controller.PacketsDropped} (should be 0)");
        Console.WriteLine();

        if (requested > 0 && controller.ResetsInjected == 0)
        {
            Console.WriteLine("Nothing was reset. A connection is only resettable once a packet");
            Console.WriteLine("carrying payload has been seen in BOTH directions, so give it a few");
            Console.WriteLine("seconds of traffic first - the live count on the status line says");
            Console.WriteLine("when a press will do something.");
            Console.WriteLine();
        }

        if (session is not null)
        {
            Console.WriteLine($"{session.Count} events written to {session.Path}");
            Console.WriteLine();
        }

        Console.WriteLine("Done - the handle is closed and traffic is back to normal.");
        return 0;
    }
}
