using System.Runtime.Versioning;

using D2Vpn.Cli.Hotkeys;
using D2Vpn.Core.Diagnostics;
using D2Vpn.Core.Input;
using D2Vpn.Core.Processes;

namespace D2Vpn.Cli;

/// <summary>
/// Freezes the game itself, rather than its traffic.
/// </summary>
/// <remarks>
/// <para>
/// The only command here that touches no packets. Every other module leaves the game running and
/// takes its network away; this suspends the process, so nothing is sent, received, drawn or
/// simulated until it is let go.
/// </para>
/// <para>
/// It carries a safety timeout that the traffic holds do not, and on by default. A hold that goes
/// wrong ends in an error code and the game reconnects itself; a suspend that goes wrong ends in a
/// process that will never run again.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class PauseCommand
{
    public static async Task<int> ExecuteAsync(
        string processName,
        int toggleKey,
        int pollMs,
        TimeSpan? safetyTimeout,
        string? logPath,
        CancellationToken cancellationToken)
    {
        IProcessControl process;

        try
        {
            process = NtProcessControl.Open(processName);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        using var session = new GamePauseSession(process, safetyTimeout);
        using var log = logPath is null ? null : new SessionLog(logPath);

        log?.Write("config",
            $"process={processName} safetyS="
            + $"{(safetyTimeout is { } t ? t.TotalSeconds.ToString("N0") : "none")}");

        session.Paused += _ =>
        {
            log?.Write("paused");
            Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] PAUSED");
        };

        session.Resumed += (held, reason) =>
        {
            log?.Write("resumed", $"heldS={held.TotalSeconds:F1} reason={reason}");

            var note = reason switch
            {
                ResumeReason.SafetyTimeout => "  SAFETY TIMEOUT - ended for you",
                ResumeReason.ProcessGone => "  the game exited while paused",
                ResumeReason.Shutdown => "  on shutdown",
                _ => string.Empty
            };

            Console.WriteLine(
                $"\n[{DateTime.Now:HH:mm:ss}] resumed after {held.TotalSeconds:F1}s{note}");
        };

        using var hotkeys = new HotkeyWatcher(TimeSpan.FromMilliseconds(pollMs));
        var keyLabel = KeyName.Describe(toggleKey);

        hotkeys.Bind(toggleKey, () => session.Toggle(DateTimeOffset.UtcNow));
        hotkeys.Start();

        Console.WriteLine($"pausing {process.Name} (pid {((NtProcessControl)process).Id})");
        Console.WriteLine($"  toggle       : {keyLabel}  (polled every {pollMs} ms)");
        Console.WriteLine($"  safety limit : "
            + (safetyTimeout is { } limit
                ? $"{limit.TotalSeconds:N0}s, then resumed automatically"
                : "NONE - see the warning below"));

        if (log is not null)
        {
            Console.WriteLine($"  log          : {log.Path}");
        }

        Console.WriteLine();
        Console.WriteLine("This suspends the game process. Nothing is sent, received, drawn or");
        Console.WriteLine("simulated while it is held - to the peers it looks like an uplink and");
        Console.WriteLine("downlink hold at once, and to you it looks like a frozen window.");
        Console.WriteLine();
        Console.WriteLine("Press the key again to let go. Ctrl+C resumes on the way out.");
        Console.WriteLine();

        if (safetyTimeout is null)
        {
            Console.WriteLine("WARNING: with no safety limit, a pause runs until you end it. If");
            Console.WriteLine("this process is killed outright while the game is paused, there is");
            Console.WriteLine("no cleanup path and the game stays frozen until you kill it too.");
            Console.WriteLine();
        }

        var started = DateTimeOffset.UtcNow;

        try
        {
            while (!cancellationToken.IsCancellationRequested && process.IsAlive)
            {
                var now = DateTimeOffset.UtcNow;

                // The safety timeout lives here: nothing else is running while the game is
                // suspended, so this loop is the only thing that can end a pause the operator
                // has not ended themselves.
                session.Tick(now);

                var elapsed = (now - started).TotalSeconds;
                var state = session.IsPaused
                    ? $"PAUSED {session.Elapsed(now).TotalSeconds,5:F1}s"
                    : "running        ";

                Console.Write($"\r{elapsed,6:N0}s  {state}  "
                    + $"pauses {session.Pauses}  "
                    + $"total {session.TotalPaused.TotalSeconds,5:F1}s   ");

                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            // Dispose resumes, but doing it here as well means the summary below reports a
            // finished session rather than one still holding.
            session.Release(DateTimeOffset.UtcNow, ResumeReason.Shutdown);
        }

        Console.WriteLine();
        Console.WriteLine();

        if (!process.IsAlive)
        {
            Console.WriteLine($"{processName} exited.");
            Console.WriteLine();
        }

        Console.WriteLine($"paused {session.Pauses} time{(session.Pauses == 1 ? "" : "s")}");
        Console.WriteLine($"total paused      : {session.TotalPaused.TotalSeconds:F1}s");
        Console.WriteLine($"longest pause     : {session.LongestPause.TotalSeconds:F1}s");

        if (session.SafetyStops > 0)
        {
            Console.WriteLine($"safety stops      : {session.SafetyStops}");
            Console.WriteLine();
            Console.WriteLine("A pause hit the safety limit and was ended for you. Raise it with");
            Console.WriteLine("--timeout-s if you meant to hold that long.");
        }

        if (log is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"{log.Count} events written to {log.Path}");
        }

        Console.WriteLine();
        Console.WriteLine("Done - the game is running.");
        return 0;
    }
}
