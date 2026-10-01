using System.IO.Pipes;

using D2Vpn.Core.Hosting;

namespace D2Vpn.Core.Tests.Hosting;

/// <summary>
/// The pipe transport, over a real pipe.
/// </summary>
/// <remarks>
/// <see cref="ControlProtocolTests"/> covers what the replies say; this covers that they arrive,
/// one line per command, over the thing an AutoHotkey script will actually open. It needs no
/// elevation because no WinDivert handle is involved - the runners are fakes.
/// </remarks>
public class NamedPipeControlServerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Commands_round_trip_one_line_each()
    {
        var name = UniquePipeName();
        using var host = new ModuleHost([new FakeRunner("3074-ul"), new FakeRunner("30k")]);
        using var server = new NamedPipeControlServer(host, name);
        using var cts = new CancellationTokenSource(Patience);

        var serving = server.RunAsync(cts.Token);

        await using var client = await ConnectAsync(name, cts.Token);
        using var reader = new StreamReader(client, leaveOpen: true);
        using var writer = new StreamWriter(client, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        await writer.WriteLineAsync("ping");
        Assert.Equal("ok pong", await reader.ReadLineAsync(cts.Token));

        await writer.WriteLineAsync("status");
        Assert.Equal("ok 3074-ul=0 30k=0", await reader.ReadLineAsync(cts.Token));

        await writer.WriteLineAsync("on 30k");
        Assert.Equal("ok 30k=1", await reader.ReadLineAsync(cts.Token));

        // The point of the whole exercise: a command over the pipe changed real module state.
        Assert.Equal(1, host.ActiveCount);

        await writer.WriteLineAsync("release");
        Assert.Equal("ok released=1", await reader.ReadLineAsync(cts.Token));
        Assert.Equal(0, host.ActiveCount);

        await cts.CancelAsync();
        await Finish(serving);
    }

    [Fact]
    public async Task A_bad_command_gets_an_error_and_the_connection_survives()
    {
        // A macro sending nonsense must not cost the operator their ability to release a hold.
        var name = UniquePipeName();
        using var host = new ModuleHost([new FakeRunner("30k")]);
        using var server = new NamedPipeControlServer(host, name);
        using var cts = new CancellationTokenSource(Patience);

        var serving = server.RunAsync(cts.Token);

        await using var client = await ConnectAsync(name, cts.Token);
        using var reader = new StreamReader(client, leaveOpen: true);
        using var writer = new StreamWriter(client, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        await writer.WriteLineAsync("nonsense");
        Assert.StartsWith("err", await reader.ReadLineAsync(cts.Token));

        await writer.WriteLineAsync("ping");
        Assert.Equal("ok pong", await reader.ReadLineAsync(cts.Token));

        await cts.CancelAsync();
        await Finish(serving);
    }

    [Fact]
    public async Task Quit_signals_shutdown()
    {
        var name = UniquePipeName();
        using var host = new ModuleHost([new FakeRunner("30k")]);
        using var server = new NamedPipeControlServer(host, name);
        using var cts = new CancellationTokenSource(Patience);

        var serving = server.RunAsync(cts.Token);

        await using var client = await ConnectAsync(name, cts.Token);
        using var reader = new StreamReader(client, leaveOpen: true);
        using var writer = new StreamWriter(client, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        await writer.WriteLineAsync("quit");
        Assert.Equal("ok bye", await reader.ReadLineAsync(cts.Token));

        await Finish(serving);

        Assert.True(server.ShutdownRequested.IsCancellationRequested);
    }

    [Fact]
    public async Task A_second_client_is_served_after_the_first_disconnects()
    {
        var name = UniquePipeName();
        using var host = new ModuleHost([new FakeRunner("30k")]);
        using var server = new NamedPipeControlServer(host, name);
        using var cts = new CancellationTokenSource(Patience);

        var serving = server.RunAsync(cts.Token);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var client = await ConnectAsync(name, cts.Token);
            using var reader = new StreamReader(client, leaveOpen: true);
            using var writer = new StreamWriter(client, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            await writer.WriteLineAsync("ping");
            Assert.Equal("ok pong", await reader.ReadLineAsync(cts.Token));
        }

        Assert.Equal(2, server.CommandsServed);

        await cts.CancelAsync();
        await Finish(serving);
    }

    private static string UniquePipeName() => $"d2vpn-test-{Guid.NewGuid():N}";

    private static async Task<NamedPipeClientStream> ConnectAsync(
        string name, CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous);

        // The server creates its pipe instance inside its own loop, so a client can arrive first.
        await client.ConnectAsync(TimeSpan.FromSeconds(5), cancellationToken);
        return client;
    }

    private static async Task Finish(Task serving)
    {
        try
        {
            await serving;
        }
        catch (OperationCanceledException)
        {
            // How the server stops.
        }
    }

    private sealed class FakeRunner(string name) : IModuleRunner, IControllable
    {
        public string Name => name;

        public bool IsActive { get; set; }

        public bool SupportsReset => false;

        public string? HandleKey { get; init; }

        public IReadOnlyList<IControllable> Controls => [this];

        public IModuleOptions? Options { get; init; }

        public ModuleStatus Status => new(name, IsActive, 0, 0, 0, 0, 0);

        public Task RunAsync(CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);

        public void RequestReset()
        {
        }

        public void Dispose()
        {
        }
    }
}
