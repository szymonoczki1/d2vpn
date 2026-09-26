using System.Runtime.Versioning;
using D2Vpn.Cli.Hotkeys;
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
        CancellationToken cancellationToken)
    {
        var module = new HoldModule(group.Name, group, options);
        module.SafetyTimeoutElapsed += m =>
            Console.WriteLine($"\n{m.Name}: safety timeout reached, released.");

        using var controller = new WinDivertTrafficController(module, priority);
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

        hotkeys.Bind(toggleKey, () =>
        {
            var active = module.Toggle();
            toggleCount++;
            Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] {(active ? "HOLDING" : "released")}"
                + $"  ({toggleCount} toggle{(toggleCount == 1 ? string.Empty : "s")})");
        });

        hotkeys.Start();

        Console.WriteLine($"running {group} {Describe(options.Direction)}");
        Console.WriteLine($"  toggle       : {keyLabel}  (polled every {pollMs} ms)");
        Console.WriteLine($"  untick delay : "
            + $"{(untickDelayMs > 0 ? $"{untickDelayMs} ms" : "none")}");
        Console.WriteLine($"  pass sizes   : {Format(options.PassLengths)}");
        Console.WriteLine($"  resync passes: {Format(options.ResyncPassLengths)}");
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
