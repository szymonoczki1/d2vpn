using System.IO.Pipes;
using System.Runtime.Versioning;

namespace D2Vpn.Cli;

/// <summary>
/// Sends one command to a running host and prints the reply.
/// </summary>
/// <remarks>
/// <para>
/// The host prints a PowerShell snippet for talking to its pipe, which is fine for proving the
/// thing works and miserable as the way you actually change a setting. This is the same exchange as
/// one command.
/// </para>
/// <para>
/// It is also the simplest way for a macro to drive the host, though not the fastest: starting a
/// process costs tens of milliseconds, where opening the pipe directly costs well under one. For a
/// switch nobody notices; for a frame-sensitive toggle, bind a key in the config or have the script
/// hold the pipe open.
/// </para>
/// <para>
/// <b>Elevation.</b> The host creates its pipe as an administrator, so Windows will not let a
/// normal process open it. This has to run elevated too.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ControlClientCommand
{
    public static async Task<int> ExecuteAsync(
        string pipeName, string command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            Console.Error.WriteLine(
                "nothing to send. Try: d2vpn ctl status, or d2vpn ctl set 3074-ul resync off");
            return 1;
        }

        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(TimeSpan.FromSeconds(3), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine(
                $"no host is listening on \\\\.\\pipe\\{pipeName}. Start one with "
                + "'d2vpn host --config d2vpn.json'.");
            return 1;
        }
        catch (UnauthorizedAccessException)
        {
            // The usual cause, and worth naming rather than printing a bare access denied.
            Console.Error.WriteLine(
                $"access denied opening \\\\.\\pipe\\{pipeName}. The host runs elevated, so this "
                + "has to as well.");
            return 1;
        }

        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        await writer.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);

        var reply = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        if (reply is null)
        {
            Console.Error.WriteLine("the host closed the connection without replying");
            return 1;
        }

        Console.WriteLine(reply);

        // So a script can branch on the exit code instead of parsing the reply.
        return reply.StartsWith("err", StringComparison.Ordinal) ? 1 : 0;
    }
}
