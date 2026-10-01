using System.IO.Pipes;
using System.Runtime.Versioning;

namespace D2Vpn.Core.Hosting;

/// <summary>
/// Carries <see cref="ControlProtocol"/> over a local named pipe.
/// </summary>
/// <remarks>
/// <para>
/// A named pipe rather than a socket or an HTTP endpoint. It is the lowest-latency local transport
/// Windows offers, it is reachable from AutoHotkey v2 with <c>FileOpen</c> and no library, and it
/// listens on nothing the network can see. Latency matters here: the hotkey path this replaces
/// polls at 1 ms because sequences in this game are frame-sensitive, and a control surface that
/// added tens of milliseconds would be a downgrade dressed as an improvement.
/// </para>
/// <para>
/// One client at a time, served in a loop. Several front-ends connecting at once is not a case
/// worth the complexity: a macro script and a UI both toggling the same modules would be racing
/// each other regardless of what the transport allowed.
/// </para>
/// <para>
/// <b>Elevation.</b> The host needs administrator rights for WinDivert, so this pipe is created by
/// a high-integrity process. Windows will not let a medium-integrity process - an AutoHotkey script
/// started normally - open it. Run the script elevated too, or start it from the elevated host.
/// There is no way around this that does not involve weakening the pipe's security descriptor.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeControlServer : IDisposable
{
    private readonly ModuleHost _host;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();

    private bool _disposed;

    /// <summary>Creates a server for a host.</summary>
    /// <param name="host">The modules to control.</param>
    /// <param name="pipeName">The pipe name, without the <c>\\.\pipe\</c> prefix.</param>
    public NamedPipeControlServer(ModuleHost host, string pipeName)
    {
        _host = host;
        _pipeName = pipeName;
    }

    /// <summary>The full path a client opens.</summary>
    public string Path => $@"\\.\pipe\{_pipeName}";

    /// <summary>How many commands have been served.</summary>
    public int CommandsServed { get; private set; }

    /// <summary>Signalled when a client sends <c>quit</c>.</summary>
    public CancellationToken ShutdownRequested => _shutdown.Token;

    /// <summary>Raised for each command, with the request and the reply.</summary>
    public event Action<string, string>? CommandServed;

    /// <summary>Accepts clients until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdown.Token);

        while (!linked.IsCancellationRequested)
        {
            try
            {
                await ServeOneClientAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A client that vanished mid-command. The next one is unaffected, and tearing the
                // host down over it would leave modules held with nothing able to release them.
            }
        }
    }

    private async Task ServeOneClientAsync(CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

        using var reader = new StreamReader(pipe, leaveOpen: true);

        // AutoWrite so a script does not have to know to flush, and NewLine fixed to "\n" so a
        // client reading a line is not left waiting for a second byte it never sends.
        using var writer = new StreamWriter(pipe, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                return;
            }

            var result = ControlProtocol.Execute(line, _host);

            CommandsServed++;
            CommandServed?.Invoke(line, result.Response);

            await writer.WriteLineAsync(result.Response).ConfigureAwait(false);

            if (result.Shutdown)
            {
                await _shutdown.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
