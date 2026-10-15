using System.Runtime.Versioning;

using D2Vpn.Core.Diagnostics;
using D2Vpn.Core.Input;
using D2Vpn.Core.Hosting;
using D2Vpn.Core.Processes;

namespace D2Vpn.Cli;

/// <summary>
/// Runs several modules at once, driven over a named pipe rather than by hotkeys.
/// </summary>
/// <remarks>
/// <para>
/// The surface the window and any external script are both built on. Every other command owns
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
        HostConfig config,
        string? logPath,
        CancellationToken cancellationToken)
    {
        var pipeName = config.Pipe;
        List<IModuleRunner> runners = [];

        try
        {
            // One handle per port group, with every module on that group as a slot behind it.
            runners.AddRange(ModuleRunnerFactory.CreateAll(config.Resolve()));

            if (config.Pause.Enabled)
            {
                var process = NtProcessControl.Open(config.Pause.Process);
                var timeout = config.Pause.SafetyTimeoutS > 0
                    ? TimeSpan.FromSeconds(config.Pause.SafetyTimeoutS)
                    : (TimeSpan?)null;

                var pause = new GamePauseRunner(new GamePauseSession(process, timeout), "pause")
                {
                    IsEnabled = config.Pause.ModuleEnabled
                };

                runners.Add(pause);
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

        // Bound here rather than in the host, which stays input-free: a macro drives the pipe
        // directly, and these keys are so the app is usable with no macro tooling at all. Both read
        // the same file, so there is one mapping rather than two that can disagree.
        using var hotkeys = new HotkeyWatcher(TimeSpan.FromMilliseconds(config.PollMs));
        var bound = BindKeys(config, host, hotkeys, log);
        hotkeys.Start();

        if (bound.Count > 0)
        {
            Console.WriteLine("  keys:");

            foreach (var (key, what) in bound)
            {
                Console.WriteLine($"    {key,-12} {what}");
            }

            Console.WriteLine();
        }

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

    /// <summary>
    /// Binds the keys a config names, returning what was bound for display.
    /// </summary>
    /// <remarks>
    /// A bad key name is reported and skipped rather than failing the session. Losing one binding
    /// is a nuisance; refusing to start because of a typo in the eleventh one, when the other ten
    /// would have worked, is worse.
    /// </remarks>
    private static List<(string Key, string What)> BindKeys(
        HostConfig config, ModuleHost host, HotkeyWatcher hotkeys, SessionLog? log)
    {
        List<(string, string)> bound = [];

        void Bind(string? key, string what, Action action)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            int code;

            try
            {
                code = KeyName.Parse(key);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"warning: cannot bind '{key}' to {what}: {ex.Message}");
                return;
            }

            hotkeys.Bind(code, action);
            bound.Add((KeyName.Describe(code), what));
        }

        foreach (var module in config.Modules)
        {
            var name = ModuleSpec.Parse(module.Name).Name;
            Bind(module.Bind, $"toggle {name}", () => host.Toggle(name));

            Bind(module.ResyncBind, $"{name} resync on/off",
                () => FlipSwitch(host, name, resync: true));

            Bind(module.BufferBind, $"{name} buffer on/off",
                () => FlipSwitch(host, name, resync: false));
        }

        if (config.Pause.Enabled)
        {
            Bind(config.Pause.Bind, "toggle pause", () => host.Toggle("pause"));
        }

        Bind(config.ReleaseBind, "release everything", () => host.ReleaseAll());

        foreach (var reset in config.ResetBinds)
        {
            var name = ModuleSpec.Parse(reset.Module).Name;

            Bind(reset.Bind, $"reset {name}", () =>
            {
                try
                {
                    host.RequestReset(name);
                }
                catch (InvalidOperationException ex)
                {
                    // Validation already refused a reset bind on a module that is not hosted, so
                    // this is the module being a hold rather than a TCP one.
                    Console.Error.WriteLine($"\n{ex.Message}");
                }
            });
        }

        log?.Write("binds", string.Join(" ", bound.Select(b => $"{b.Item1}={b.Item2}")));
        return bound;
    }

    /// <summary>
    /// Flips one of a module's switches and says what it became.
    /// </summary>
    /// <remarks>
    /// Printed rather than silent: a switch has no visible effect until the next packet is
    /// withheld, so a key that reported nothing would be indistinguishable from one that was not
    /// bound.
    /// </remarks>
    private static void FlipSwitch(ModuleHost host, string name, bool resync)
    {
        if (host.OptionsFor(name) is not { } options)
        {
            Console.Error.WriteLine($"\n{name} has no switches");
            return;
        }

        try
        {
            if (resync)
            {
                if (!options.SupportsResync)
                {
                    Console.Error.WriteLine($"\n{name} has no resync or keepalive to switch");
                    return;
                }

                options.ResyncEnabled = !options.ResyncEnabled;
            }
            else
            {
                options.BufferEnabled = !options.BufferEnabled;
            }

            Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] {name} {options.ToLine()}");
        }
        catch (InvalidOperationException ex)
        {
            // A delay line refusing to give up its buffer is the expected case here.
            Console.Error.WriteLine($"\n{ex.Message}");
        }
    }
}
