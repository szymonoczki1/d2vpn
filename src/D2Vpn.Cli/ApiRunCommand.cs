using System.Runtime.Versioning;
using D2Vpn.Cli.Hotkeys;
using D2Vpn.Core.Diagnostics;
using D2Vpn.Core.Input;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Cli;

/// <summary>
/// Runs the TCP 7500 hold resident, toggled by a hotkey, until interrupted.
/// </summary>
/// <remarks>
/// Separate from <see cref="RunCommand"/> rather than a branch inside it, because almost nothing
/// they report is shared. A 3074 hold is judged on pass lists and resync cycles; this one is judged
/// on whether retransmissions are being suppressed, which is a different question with a different
/// failure mode - and a status line that answered both would answer neither clearly.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ApiRunCommand
{
    public static async Task<int> ExecuteAsync(
        PortGroup group,
        ApiBlockOptions options,
        int toggleKey,
        short priority,
        int pollMs,
        string? logPath,
        CancellationToken cancellationToken)
    {
        var module = new ApiBlockModule(group.Name, group, options);

        using var session = logPath is null ? null : new SessionLog(logPath);
        session?.Write("config", $"group={group.Name} captureQuietMs="
            + $"{options.CaptureQuiet.TotalMilliseconds:F0} captureBytes={options.CaptureBytes} "
            + $"maxHoldS={options.MaxHold.TotalSeconds:F0} "
            + $"quietHoldS={options.QuietHold.TotalSeconds:F0} "
            + $"quietRetransmitS={options.QuietRetransmit.TotalSeconds:F0} "
            + $"buffering={options.Buffering} priority={priority}");

        using var buffer = options.Buffering
            ? new PacketBuffer(2048, TimeSpan.FromSeconds(35))
            : null;

        // Only when replaying. A segment held for seconds acknowledges a position the connection
        // has long since passed, and delivering that as-is is a duplicate acknowledgement.
        var rewriter = options.Buffering ? new TcpAckRewriter() : null;

        using var controller =
            new WinDivertTrafficController(module, priority, buffer, rewriter);
        using var hotkeys = new HotkeyWatcher(TimeSpan.FromMilliseconds(pollMs));

        var toggleCount = 0;
        var started = DateTimeOffset.UtcNow;

        module.Disabled += (_, why) =>
        {
            session?.Write("self-disabled", why);
            Console.WriteLine($"\n[{DateTime.UtcNow:HH:mm:ss}] released by the module: {why}");
        };

        hotkeys.Bind(toggleKey, () =>
        {
            toggleCount++;
            var active = module.Toggle();
            session?.Write(active ? "hold" : "release");
            Console.WriteLine($"\n[{DateTime.UtcNow:HH:mm:ss}] {(active ? "HOLDING" : "released")}"
                + $"  ({toggleCount} toggle{(toggleCount == 1 ? string.Empty : "s")})");
        });

        hotkeys.Start();

        var keyLabel = KeyName.Describe(toggleKey);
        Console.WriteLine($"running {group} inbound");
        Console.WriteLine($"  toggle       : {keyLabel}  (polled every {pollMs} ms)");
        Console.WriteLine($"  capture      : "
            + (options.HoldEverything
                ? "everything inbound, for the whole hold"
                : $"until {options.CaptureQuiet.TotalMilliseconds:F0} ms quiet, then hold that "
                  + "slice and pass later data"));
        Console.WriteLine($"  ceiling      : {options.MaxHold.TotalSeconds:F0}s, or "
            + $"{options.QuietHold.TotalSeconds:F0}s with "
            + $"{options.QuietRetransmit.TotalSeconds:F0}s of silence");
        Console.WriteLine($"  buffering    : "
            + (options.Buffering
                ? "on - withheld segments are replayed on untick, ack numbers brought forward"
                : "off - dropped, so on untick you wait for the peer's next retransmission"));
        Console.WriteLine();
        Console.WriteLine("This is TCP, so the failure mode is a RESET and an error code, not a");
        Console.WriteLine("slow timeout. Unlike a 3074 uplink hold there is no resync to keep the");
        Console.WriteLine("connection alive - the module disables itself before the ceiling, and");
        Console.WriteLine("immediately if the peer resets.");
        Console.WriteLine();
        Console.WriteLine("Outbound is never touched.");
        Console.WriteLine();

        if (session is not null)
        {
            Console.WriteLine($"  log          : {session.Path}");
            Console.WriteLine();
        }

        Console.WriteLine($"Press {keyLabel} in-game to hold, again to release. Ctrl+C to exit.");
        Console.WriteLine();

        var run = controller.RunAsync(cancellationToken);

        while (!run.IsCompleted)
        {
            await Task.WhenAny(run, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);

            var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;

            // Retransmissions suppressed is the number that says whether this is working. A TCP
            // block that withheld a slice and then leaked looks identical in the totals.
            Console.Write($"\r{elapsed,6:N0}s  "
                + $"in {controller.InboundDropped}/{controller.InboundSeen}  "
                + $"withheld {module.SegmentsWithheld}  "
                + $"resent {module.RetransmitsSuppressed}  "
                + $"{(module.IsActive ? "HOLDING " : "idle    ")}   ");
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
        Console.WriteLine($"toggled {toggleCount} time{(toggleCount == 1 ? string.Empty : "s")}");
        Console.WriteLine($"inbound   : {controller.InboundDropped} withheld of {controller.InboundSeen}");
        Console.WriteLine($"outbound  : {controller.OutboundDropped} of {controller.OutboundSeen} (never touched)");
        Console.WriteLine($"segments withheld       : {module.SegmentsWithheld}");
        Console.WriteLine($"retransmits suppressed  : {module.RetransmitsSuppressed}");
        Console.WriteLine($"capture limit reached   : {module.CapacityReached}");
        Console.WriteLine($"peer resets             : {module.ResetsSeen}");

        if (rewriter is not null)
        {
            Console.WriteLine($"replayed                : {controller.PacketsReplayed}");
            Console.WriteLine($"  ack brought forward   : {rewriter.Rewritten}");
            Console.WriteLine($"  already current       : {rewriter.LeftAlone}");
        }

        Console.WriteLine();

        if (!options.Buffering)
        {
            Console.WriteLine("Without --buffer, a withheld segment is destroyed and the data only");
            Console.WriteLine("arrives when the peer next retransmits it. Each suppression doubles");
            Console.WriteLine("that wait, so after a few seconds of holding the peer is backed off");
            Console.WriteLine("to around a second - which is the delay felt after unticking.");
            Console.WriteLine();
        }

        if (!options.HoldEverything && module.SegmentsWithheld > 0 && toggleCount > 2
            && module.SegmentsWithheld <= toggleCount / 2)
        {
            Console.WriteLine("About one segment was withheld per hold, which means the capture");
            Console.WriteLine("window closed straight away and everything after it passed. That");
            Console.WriteLine("happens when the channel is quieter than the window - try");
            Console.WriteLine("--hold-all, or a larger --capture-quiet-ms.");
            Console.WriteLine();
        }

        if (module.SegmentsWithheld > 0 && module.RetransmitsSuppressed == 0)
        {
            Console.WriteLine("No retransmissions were suppressed. On TCP that means the hold");
            Console.WriteLine("leaked: the peer re-sent what was withheld and it got through, so");
            Console.WriteLine("nothing was actually held back. Check the capture window.");
            Console.WriteLine();
        }

        if (module.ResetsSeen > 0)
        {
            Console.WriteLine("The peer reset the connection, which is the ceiling being hit.");
            Console.WriteLine("Shorten the hold, or lower --max-hold-s so it releases sooner.");
            Console.WriteLine();
        }

        if (session is not null)
        {
            Console.WriteLine($"{session.Count} events written to {session.Path}");
            Console.WriteLine();
        }

        Console.WriteLine("Released - the handle is closed and traffic is back to normal.");
        return 0;
    }
}
