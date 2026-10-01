using System.Runtime.Versioning;

using D2Vpn.Core.Diagnostics;
using D2Vpn.Core.Hosting;
using D2Vpn.Core.Processes;

namespace D2Vpn.Cli;

/// <summary>
/// Runs several modules at once, driven over a named pipe rather than by hotkeys.
/// </summary>
/// <remarks>
/// <para>
/// The surface the AutoHotkey manager and the WPF UI are both built on. Every other command owns
/// one module and its own hotkey loop, which is why combining two meant writing a third command.
/// </para>
/// <para>
/// <b>No hotkeys here, deliberately.</b> The decision behind this project is that a macro triggers
/// a module directly instead of synthesising a keypress for a global listener to notice, so this
/// command binds nothing and waits to be told. Use <c>run</c> for a single module with a key.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class HostCommand
{
    public static async Task<int> ExecuteAsync(
        IReadOnlyList<ModuleSpec> specs,
        string pipeName,
        string? logPath,
        string? pauseProcess,
        TimeSpan? pauseTimeout,
        CancellationToken cancellationToken)
    {
        List<IModuleRunner> runners = [];

        try
        {
            // One handle per port group, with every module on that group as a slot behind it.
            runners.AddRange(ModuleRunnerFactory.CreateAll(specs));

            if (pauseProcess is not null)
            {
                var process = NtProcessControl.Open(pauseProcess);
                runners.Add(new GamePauseRunner(
                    new GamePauseSession(process, pauseTimeout), "pause"));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");

            foreach (var runner in runners)
            {
                runner.Dispose();
            }

            return 1;
        }

        using var host = new ModuleHost(runners);
        using var log = logPath is null ? null : new SessionLog(logPath);
        using var server = new NamedPipeControlServer(host, pipeName);

        log?.Write("config", $"modules={string.Join(",", host.Names)} pipe={pipeName}");

        host.StateChanged += (name, active) =>
        {
            log?.Write(active ? "hold" : "release", $"module={name}");
            Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] {name} {(active ? "HOLDING" : "released")}");
        };

        server.CommandServed += (request, response) =>
            log?.Write("control", $"in={request} out={response}");

        Console.WriteLine("DIVERT MODE - packets really are dropped while a module is holding.");
        Console.WriteLine("Any other limiter must be fully EXITED, not just idle.");
        Console.WriteLine();
        var toggles = host.Names.Count;
        Console.WriteLine(
            $"hosting {toggles} toggle{(toggles == 1 ? "" : "s")} "
            + $"across {runners.Count} handle{(runners.Count == 1 ? "" : "s")}");

        foreach (var runner in runners)
        {
            var names = string.Join(" ", runner.Controls.Select(c => c.Name));
            Console.WriteLine($"  {names,-34} {runner.HandleKey ?? "no packet handle"}");
        }

        Console.WriteLine();
        Console.WriteLine($"  control      : {server.Path}");

        if (log is not null)
        {
            Console.WriteLine($"  log          : {log.Path}");
        }

        Console.WriteLine();
        Console.WriteLine("Nothing is holding until something tells it to. One line in, one line");
        Console.WriteLine("out - try it from PowerShell:");
        Console.WriteLine();
        Console.WriteLine($"  $p = [IO.Pipes.NamedPipeClientStream]::new('.', '{pipeName}', 'InOut')");
        Console.WriteLine("  $p.Connect(); $r = [IO.StreamReader]::new($p)");
        Console.WriteLine("  $w = [IO.StreamWriter]::new($p); $w.AutoFlush = $true");
        Console.WriteLine("  $w.WriteLine('status'); $r.ReadLine()");
        Console.WriteLine();
        Console.WriteLine("Commands: ping list status stat <m> toggle <m> on <m> off <m>");
        Console.WriteLine("          opts <m> set <m> resync|buffer on|off");
        Console.WriteLine("          release reset <m> quit");
        Console.WriteLine();
        Console.WriteLine("Ctrl+C releases every module and closes every handle.");
        Console.WriteLine();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, server.ShutdownRequested);

        var modules = host.RunAsync(stopping.Token);
        var control = server.RunAsync(stopping.Token);
        var started = DateTimeOffset.UtcNow;

        var failure = 0;

        try
        {
            while (!modules.IsCompleted && !stopping.IsCancellationRequested)
            {
                await Task.WhenAny(modules, Task.Delay(500, CancellationToken.None))
                    .ConfigureAwait(false);

                var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;
                var snapshot = host.Snapshot();

                var states = string.Join(' ', snapshot.Select(s =>
                    s.IsActive ? $"[{s.Name}]" : s.Name));

                Console.Write($"\r{elapsed,6:N0}s  {states}  "
                    + $"dropped {snapshot.Sum(s => s.PacketsDropped)}  "
                    + $"cmds {server.CommandsServed}   ");
            }

            await modules.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C, or a client sending quit.
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.Error.WriteLine($"\nerror: a module failed: {ex.Message}");
            failure = 1;
        }
        finally
        {
            // Releasing here as well as in the host means the summary describes a finished session.
            host.ReleaseAll();

            await stopping.CancelAsync().ConfigureAwait(false);

            try
            {
                await control.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected once the pipe is told to stop.
            }
        }

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine($"commands served   : {server.CommandsServed}");
        Console.WriteLine();

        foreach (var status in host.Snapshot())
        {
            Console.WriteLine($"  {status.ToLine()}");
        }

        if (log is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"{log.Count} events written to {log.Path}");
        }

        Console.WriteLine();
        Console.WriteLine("Released - every handle is closed and traffic is back to normal.");
        return failure;
    }
}
