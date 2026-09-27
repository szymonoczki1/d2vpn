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
    /// <summary>
    /// How long a hold must run before zero suppressed retransmissions means anything.
    /// </summary>
    /// <remarks>
    /// The peer only re-sends once its retransmission timer expires, measured on this channel
    /// at roughly 3.4 s between attempts. A shorter hold ends before the first one is due, so
    /// zero suppressed is the expected result rather than evidence of a leak.
    /// </remarks>
    private static readonly TimeSpan RetransmitPatience = TimeSpan.FromSeconds(5);

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

        // Aged out past the hold ceiling rather than before it. A 44.7 s hold with a 35 s
        // limit expired its earliest segment and replayed 10 of 11 withheld - the one piece of
        // data the game had been waiting longest for. Unlike a position update, an API segment
        // does not go stale on its own: TCP will deliver it in order whenever it arrives, and the
        // receiver is still asking for exactly it.
        // Nothing expires here, unlike on 3074. Capacity is the only bound, and a hold holds one
        // or two segments against 2048 - so it is a guard against a case that has never arisen
        // rather than a working limit. See PacketBuffer.NeverExpires for why an ordered stream
        // does not want an age limit at all.
        using var buffer = options.Buffering
            ? new PacketBuffer(2048, PacketBuffer.NeverExpires)
            : null;

        // Only when replaying. A segment held for seconds acknowledges a position the connection
        // has long since passed, and delivering that as-is is a duplicate acknowledgement.
        var rewriter = options.Buffering ? new TcpAckRewriter() : null;

        using var controller =
            new WinDivertTrafficController(module, priority, buffer, rewriter);
        using var hotkeys = new HotkeyWatcher(TimeSpan.FromMilliseconds(pollMs));

        var toggleCount = 0;
        var started = DateTimeOffset.UtcNow;
        var holdStarted = DateTimeOffset.UtcNow;
        var longestHold = TimeSpan.Zero;

        module.Disabled += (_, why) =>
        {
            // Recorded here too, not only on a hotkey release. The hold that ends by itself
            // is the one whose length is the measurement - it is where the ceiling is.
            var lasted = DateTimeOffset.UtcNow - holdStarted;
            if (lasted > longestHold)
            {
                longestHold = lasted;
            }

            session?.Write("self-disabled", $"{why} heldFor={lasted.TotalSeconds:F2}s");
            Console.WriteLine($"\n[{DateTime.UtcNow:HH:mm:ss}] released by the module after "
                + $"{lasted.TotalSeconds:F1}s: {why}");
        };

        hotkeys.Bind(toggleKey, () =>
        {
            toggleCount++;
            var active = module.Toggle();

            if (active)
            {
                holdStarted = DateTimeOffset.UtcNow;
                session?.Write("hold");
            }
            else
            {
                var lasted = DateTimeOffset.UtcNow - holdStarted;
                if (lasted > longestHold)
                {
                    longestHold = lasted;
                }

                session?.Write("release", $"heldFor={lasted.TotalSeconds:F2}s");
            }
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
        Console.WriteLine($"  ceiling      : "
            + (options.MaxHold > TimeSpan.Zero
                ? $"{options.MaxHold.TotalSeconds:F0}s"
                : "none - hold as long as you like, RST still releases")
            + (options.QuietHold > TimeSpan.Zero
                ? $", or {options.QuietHold.TotalSeconds:F0}s with "
                  + $"{options.QuietRetransmit.TotalSeconds:F0}s of silence"
                : string.Empty));
        Console.WriteLine($"  wireshark    : an inbound capture shows segments this tool then");
        Console.WriteLine($"                 drops - npcap sits below the filter, so seeing them");
        Console.WriteLine($"                 there does not mean they reached the game");
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

        if (options.MaxHold <= TimeSpan.Zero && options.QuietHold <= TimeSpan.Zero)
        {
            Console.WriteLine("Uncapped: this hold ends when you untick, or when the peer resets");
            Console.WriteLine("  the connection - which is an error code. Measured here, 75s");
            Console.WriteLine("  released cleanly and 86s was reset. --safe caps it at 60s.");
            Console.WriteLine();
        }

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
        Console.WriteLine($"longest hold            : {longestHold.TotalSeconds:F1}s");

        if (rewriter is not null && buffer is not null)
        {
            Console.WriteLine($"replayed                : {controller.PacketsReplayed}");
            Console.WriteLine($"  ack brought forward   : {rewriter.Rewritten}");
            Console.WriteLine($"  already current       : {rewriter.LeftAlone}");
            Console.WriteLine($"  expired unsent        : {buffer.Expired}");
            Console.WriteLine($"  evicted unsent        : {buffer.Evicted}");
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

        // Only meaningful once a hold ran long enough for the peer to try again. Measured on
        // this channel the retransmissions come about 3.4 s apart, so a one-second hold ends
        // before the first is due and zero suppressed is simply correct. Warning on that said
        // the hold had leaked when it had not.
        if (module.SegmentsWithheld > 0
            && module.RetransmitsSuppressed == 0
            && longestHold > RetransmitPatience)
        {
            Console.WriteLine($"No retransmissions were suppressed across a "
                + $"{longestHold.TotalSeconds:F0}s hold, which is long enough for the peer");
            Console.WriteLine("to have tried again - so the hold leaked: what was withheld got");
            Console.WriteLine("through on a re-send and nothing was actually held back.");
            Console.WriteLine();
        }

        if (module.ResetsSeen > 0)
        {
            Console.WriteLine($"The peer reset the connection after {longestHold.TotalSeconds:F0}s, "
                + "which is where the");
            Console.WriteLine("ceiling actually is on this connection.");

            if (options.MaxHold <= TimeSpan.Zero)
            {
                Console.WriteLine($"That is the measurement. Set --max-hold-s a few seconds below "
                    + $"{longestHold.TotalSeconds:F0}");
                Console.WriteLine("to hold as long as possible without reaching it.");
            }
            else
            {
                Console.WriteLine("Lower --max-hold-s so it releases before this point.");
            }

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
