using System.Runtime.Versioning;
using D2Vpn.Cli.Hotkeys;
using D2Vpn.Core.Diagnostics;
using D2Vpn.Core.Input;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Cli;

/// <summary>
/// Runs a module resident, toggled by a hotkey, until interrupted.
/// </summary>
/// <remarks>
/// The difference between a validated module and a usable one. <c>hold</c> runs on a timer, which
/// is right for a measurement and useless in play: a desync has to start and stop on the frame the
/// player wants, not on a stopwatch. It is also a prerequisite for anything that has to be held
/// and released repeatedly while watching what changes.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class RunCommand
{
    public static async Task<int> ExecuteAsync(
        PortGroup group,
        HoldModuleOptions options,
        int toggleKey,
        short priority,
        bool logPassed,
        int pollMs,
        int untickDelayMs,
        string? logPath,
        int goodKey,
        int badKey,
        CancellationToken cancellationToken)
    {
        var module = new HoldModule(group.Name, group, options);
        module.SafetyTimeoutElapsed += m =>
            Console.WriteLine($"\n{m.Name}: safety timeout reached, released.");

        using var session = logPath is null ? null : new SessionLog(logPath);

        // Without this a log cannot be compared against another one. Three sessions of labelled
        // trials were recorded before it became clear that none of them stated which settings
        // produced them, which makes them individually readable and collectively useless.
        session?.Write("config", $"group={group.Name} direction={options.Direction} "
            + $"pass=[{Format(options.PassLengths)}] "
            + $"resyncPass=[{Format(options.ResyncPassLengths)}] "
            + $"triggers=[{Format(options.ResyncTriggerLengths)}] "
            + $"resyncMs={options.ResyncDuration.TotalMilliseconds:N0} "
            + $"cooldownMs={options.ResyncCooldown.TotalMilliseconds:N0} "
            + $"untickMs={untickDelayMs} pollMs={pollMs} priority={priority}");
        using var buffer = options.Buffering
            ? new PacketBuffer(options.BufferCapacity, options.BufferMaxAge)
            : null;

        using var controller = new WinDivertTrafficController(module, priority, buffer);
        using var hotkeys = new HotkeyWatcher(TimeSpan.FromMilliseconds(pollMs));

        // Every size that crossed while holding, and how often. A hold that works most of the time
        // fails on specific packets, and the totals cannot say which.
        var passedWhileHolding = new Dictionary<int, int>();
        var started = DateTimeOffset.UtcNow;

        controller.PacketPassedWhileHolding += packet =>
        {
            // Traffic in the unheld direction passes by design and is not a leak. Logging it
            // buries the handful of packets that matter under hundreds that never could.
            var isHeld = options.Direction switch
            {
                HoldDirection.Uplink => packet.IsOutbound,
                HoldDirection.Downlink => packet.IsInbound,
                _ => true
            };

            if (!isHeld)
            {
                return;
            }

            lock (passedWhileHolding)
            {
                passedWhileHolding[packet.PayloadLength] =
                    passedWhileHolding.GetValueOrDefault(packet.PayloadLength) + 1;
            }

            if (logPassed)
            {
                var at = (packet.Timestamp - started).TotalSeconds;
                Console.WriteLine($"\n[{at,7:N2}s] {(packet.IsOutbound ? "OUT" : " in")} "
                    + $"{packet.PayloadLength} bytes");
            }
        };

        var keyLabel = KeyName.Describe(toggleKey);
        var toggleCount = 0;

        var marked = (Worked: 0, Failed: 0);

        hotkeys.Bind(toggleKey, () =>
        {
            toggleCount++;

            if (module.IsActive && untickDelayMs > 0)
            {
                // Keep blocking past the keypress, on the theory that the game batches what the
                // player did and sends it on its next tick - so an action taken just before
                // releasing could leave on a packet that goes out just after, when the block is
                // already gone. Untested; it has not been shown to help.
                session?.Write("release-requested", $"{untickDelayMs}ms");
                Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] releasing in {untickDelayMs} ms");

                _ = Task.Run(async () =>
                {
                    await Task.Delay(untickDelayMs).ConfigureAwait(false);
                    module.IsActive = false;
                    session?.Write("release");
                    Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] released");
                });

                return;
            }

            var active = module.Toggle();
            session?.Write(active ? "hold" : "release");
            Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] {(active ? "HOLDING" : "released")}"
                + $"  ({toggleCount} toggle{(toggleCount == 1 ? string.Empty : "s")})");
        });

        if (session is not null)
        {
            // Marking the outcome as it happens is what turns a recording into labelled trials.
            hotkeys.Bind(goodKey, () =>
            {
                marked.Worked++;
                session.Write("worked");
                Console.WriteLine($"\n  marked WORKED   ({marked.Worked} worked, {marked.Failed} failed)");
            });

            hotkeys.Bind(badKey, () =>
            {
                marked.Failed++;
                session.Write("failed");
                Console.WriteLine($"\n  marked FAILED   ({marked.Worked} worked, {marked.Failed} failed)");
            });
        }

        hotkeys.Start();

        Console.WriteLine($"running {group} {Describe(options.Direction)}");
        Console.WriteLine($"  toggle       : {keyLabel}  (polled every {pollMs} ms)");
        Console.WriteLine($"  untick delay : "
            + $"{(untickDelayMs > 0 ? $"{untickDelayMs} ms" : "none")}");
        Console.WriteLine($"  pass sizes   : {Format(options.PassLengths)}");
        Console.WriteLine($"  resync passes: {Format(options.ResyncPassLengths)}");
        Console.WriteLine($"  buffering    : "
            + (options.Buffering
                ? $"on, up to {options.BufferCapacity} packets or "
                  + $"{options.BufferMaxAge.TotalSeconds:N0}s old"
                : "off - blocked packets are destroyed"));
        Console.WriteLine($"  safety limit : "
            + $"{(options.SafetyTimeout is { } t ? $"{t.TotalSeconds:N0}s" : "none")}");
        Console.WriteLine();
        if (options.PassLengths.Count > 0 || options.ResyncTriggerLengths.Count > 0)
        {
            Console.WriteLine("For sequences of a second or two - weapon swaps, bullet detach - the");
            Console.WriteLine("game never has time to ask for a reconnect, so the resync never runs");
            Console.WriteLine("and the standing pass list is the only thing that can leak. Try");
            Console.WriteLine("--no-pass --no-resync there: a hold that short cannot hit the ceiling.");
            Console.WriteLine();
        }

        if (session is not null)
        {
            Console.WriteLine($"  log          : {session.Path}");
            Console.WriteLine($"  mark worked  : {KeyName.Describe(goodKey)}");
            Console.WriteLine($"  mark failed  : {KeyName.Describe(badKey)}");
        }

        Console.WriteLine();
        Console.WriteLine($"Press {keyLabel} in-game to hold, again to release. Ctrl+C to exit.");
        Console.WriteLine("The key is not swallowed - the game still receives it.");
        Console.WriteLine();

        var run = controller.RunAsync(cancellationToken);

        while (!run.IsCompleted)
        {
            await Task.WhenAny(run, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);

            var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;
            var held = module.IsActive;

            Console.Write($"\r{elapsed,6:N0}s  "
                + $"UL {controller.OutboundDropped}/{controller.OutboundSeen}  "
                + $"DL {controller.InboundDropped}/{controller.InboundSeen}  "
                + $"resyncs {module.ResyncsStarted}  "
                + $"{(held ? "HOLDING " : "idle    ")}   ");
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
        Console.WriteLine($"uplink   : {controller.OutboundDropped} dropped of {controller.OutboundSeen}");
        Console.WriteLine($"downlink : {controller.InboundDropped} dropped of {controller.InboundSeen}");
        if (buffer is not null)
        {
            Console.WriteLine($"buffered : {controller.PacketsBuffered}, "
                + $"replayed {controller.PacketsReplayed}, "
                + $"evicted {buffer.Evicted}, expired {buffer.Expired}");
        }

        Console.WriteLine($"reconnect requests : {module.TriggersSeen}");
        Console.WriteLine($"resync windows     : {module.ResyncsStarted}");

        if (passedWhileHolding.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("sizes that crossed while holding:");

            IReadOnlySet<int> handshake = options.ResyncPassLengths ?? new HashSet<int>();
            foreach (var (length, count) in passedWhileHolding.OrderByDescending(e => e.Value))
            {
                var role = options.PassLengths.Contains(length) ? "standing pass"
                    : handshake.Contains(length) ? "resync pass"
                    : "UNEXPECTED";

                Console.WriteLine($"  {length,6} bytes  x{count,-5}  {role}");
            }

            Console.WriteLine();
            Console.WriteLine("Anything here could have carried the action that failed to detach.");
            Console.WriteLine("Sizes that also occur in ordinary traffic are the ones to suspect.");
        }

        Console.WriteLine();
        if (session is not null)
        {
            Console.WriteLine($"{session.Count} events written to {session.Path}"
                + $"  ({marked.Worked} worked, {marked.Failed} failed)");
            Console.WriteLine("Line that up against a capture taken alongside, and the attempts");
            Console.WriteLine("that worked can be compared against the ones that did not.");
            Console.WriteLine();
        }

        Console.WriteLine("Released - the handle is closed and traffic is back to normal.");
        return 0;
    }

    private static string Describe(HoldDirection direction) => direction switch
    {
        HoldDirection.Uplink => "uplink",
        HoldDirection.Downlink => "downlink",
        _ => "both directions"
    };

    private static string Format(IReadOnlySet<int>? lengths) =>
        lengths is null ? "(everything)"
        : lengths.Count == 0 ? "(nothing)"
        : string.Join(", ", lengths.Order());
}
