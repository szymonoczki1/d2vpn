using D2Vpn.Core.Hosting;

namespace D2Vpn.Core.Tests.Hosting;

/// <summary>
/// The control protocol, which is a contract: an AutoHotkey script and a WPF front-end both
/// depend on these exact strings, so changing one is a breaking change rather than a refactor.
/// </summary>
public class ControlProtocolTests
{
    [Fact]
    public void Ping_answers_pong()
    {
        using var host = Host("30k");

        Assert.Equal("ok pong", ControlProtocol.Execute("ping", host).Response);
    }

    [Fact]
    public void List_names_every_module_in_order()
    {
        using var host = Host("3074-ul", "27k-ul", "30k");

        Assert.Equal("ok 3074-ul 27k-ul 30k", ControlProtocol.Execute("list", host).Response);
    }

    [Fact]
    public void Status_reports_every_module_as_a_flag()
    {
        using var host = Host("3074-ul", "30k");
        host.Set("30k", true);

        Assert.Equal("ok 3074-ul=0 30k=1", ControlProtocol.Execute("status", host).Response);
    }

    [Fact]
    public void Toggle_returns_the_new_state()
    {
        using var host = Host("27k-ul");

        Assert.Equal("ok 27k-ul=1", ControlProtocol.Execute("toggle 27k-ul", host).Response);
        Assert.Equal("ok 27k-ul=0", ControlProtocol.Execute("toggle 27k-ul", host).Response);
    }

    [Fact]
    public void On_and_off_are_idempotent_and_report_the_state()
    {
        // A macro that fires "on" twice wants to know the module is holding, not that its second
        // call was redundant.
        using var host = Host("30k");

        Assert.Equal("ok 30k=1", ControlProtocol.Execute("on 30k", host).Response);
        Assert.Equal("ok 30k=1", ControlProtocol.Execute("on 30k", host).Response);
        Assert.Equal("ok 30k=0", ControlProtocol.Execute("off 30k", host).Response);
        Assert.Equal("ok 30k=0", ControlProtocol.Execute("off 30k", host).Response);
    }

    [Fact]
    public void Release_reports_how_many_were_holding()
    {
        using var host = Host("a", "b", "c");
        host.Set("a", true);
        host.Set("b", true);

        Assert.Equal("ok released=2", ControlProtocol.Execute("release", host).Response);
        Assert.Equal("ok released=0", ControlProtocol.Execute("release", host).Response);
    }

    [Fact]
    public void Stat_reports_the_counters()
    {
        using var host = Host("30k");
        host.Set("30k", true);

        Assert.Equal(
            "ok 30k active=1 enabled=1 seen=10 dropped=4 buffered=3 replayed=2 resets=0",
            ControlProtocol.Execute("stat 30k", host).Response);
    }

    [Fact]
    public void Disabling_releases_and_refuses_to_be_turned_back_on()
    {
        // Enabled sits a layer above active: active asks whether it is holding, enabled whether it
        // may. Leaving a module holding while refusing to let anything turn it off would be the
        // worst of both, so disabling releases first.
        using var host = Host("30k");

        Assert.Equal("ok 30k=1", ControlProtocol.Execute("on 30k", host).Response);
        Assert.Equal("ok 30k enabled=0", ControlProtocol.Execute("disable 30k", host).Response);
        Assert.Equal(0, host.ActiveCount);

        Assert.StartsWith("err", ControlProtocol.Execute("on 30k", host).Response);
        Assert.StartsWith("err", ControlProtocol.Execute("toggle 30k", host).Response);
        Assert.Equal(0, host.ActiveCount);

        Assert.Equal("ok 30k enabled=1", ControlProtocol.Execute("enable 30k", host).Response);
        Assert.Equal("ok 30k=1", ControlProtocol.Execute("on 30k", host).Response);
    }

    [Fact]
    public void Disabling_is_reported_by_stat_and_survives_a_release()
    {
        using var host = Host("30k");

        ControlProtocol.Execute("disable 30k", host);

        Assert.Contains("enabled=0", ControlProtocol.Execute("stat 30k", host).Response);

        // Release is about holds, not about whether a module is allowed to exist.
        ControlProtocol.Execute("release", host);

        Assert.Contains("enabled=0", ControlProtocol.Execute("stat 30k", host).Response);
    }

    [Fact]
    public void Toggling_a_trigger_fires_it_instead_of_latching()
    {
        // A reconnect has no on state. Showing it as a switch that silently returns to off is the
        // kind of control nobody trusts, so a toggle fires it and reports no state.
        var reconnect = new FakeRunner("30k-rc", supportsReset: true)
        {
            Kind = ModuleKind.Trigger
        };

        using var host = new ModuleHost([reconnect]);

        Assert.Equal("ok 30k-rc=0", ControlProtocol.Execute("toggle 30k-rc", host).Response);
        Assert.Equal(0, host.ActiveCount);
        Assert.Equal("ok reset=30k-rc", ControlProtocol.Execute("reset 30k-rc", host).Response);
    }

    [Fact]
    public void Names_are_matched_case_insensitively()
    {
        // A macro author typing 30K meaning 30k should not be debugging a protocol.
        using var host = Host("30k");

        Assert.Equal("ok 30k=1", ControlProtocol.Execute("ON 30K", host).Response);
    }

    [Fact]
    public void Quit_asks_for_shutdown()
    {
        using var host = Host("30k");

        var result = ControlProtocol.Execute("quit", host);

        Assert.Equal("ok bye", result.Response);
        Assert.True(result.Shutdown);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_empty_line_is_an_error_not_a_crash(string? line)
    {
        using var host = Host("30k");

        Assert.Equal("err empty command", ControlProtocol.Execute(line, host).Response);
    }

    [Fact]
    public void An_unknown_verb_is_reported_without_throwing()
    {
        using var host = Host("30k");

        Assert.Equal(
            "err unknown command 'frobnicate'",
            ControlProtocol.Execute("frobnicate 30k", host).Response);
    }

    [Fact]
    public void A_command_needing_a_name_says_so_when_given_none()
    {
        using var host = Host("30k");

        Assert.StartsWith("err", ControlProtocol.Execute("toggle", host).Response);
        Assert.StartsWith("err", ControlProtocol.Execute("stat", host).Response);
    }

    [Fact]
    public void An_unknown_module_lists_what_is_hosted()
    {
        using var host = Host("3074-ul", "30k");

        var response = ControlProtocol.Execute("on 7500", host).Response;

        Assert.StartsWith("err", response);
        Assert.Contains("3074-ul", response);
    }

    [Fact]
    public void Reset_on_a_hold_is_an_error_not_a_silent_no_op()
    {
        using var host = Host("3074-ul");

        Assert.StartsWith("err", ControlProtocol.Execute("reset 3074-ul", host).Response);
    }

    [Fact]
    public void Reset_on_a_resettable_module_reports_the_name()
    {
        using var host = new ModuleHost([new FakeRunner("30k", supportsReset: true)]);

        Assert.Equal("ok reset=30k", ControlProtocol.Execute("reset 30k", host).Response);
    }

    [Fact]
    public void No_reply_is_ever_more_than_one_line()
    {
        // The whole framing. A client reads exactly one line per command, so a reply containing a
        // newline would desynchronise every command after it.
        using var host = Host("3074-ul", "27k-ul", "30k");
        host.Set("30k", true);

        string[] commands =
        [
            "ping", "list", "status", "stat 30k", "toggle 30k", "on 30k", "off 30k",
            "release", "quit", "bogus", "", "toggle nosuch"
        ];

        foreach (var command in commands)
        {
            var response = ControlProtocol.Execute(command, host).Response;

            Assert.DoesNotContain('\n', response);
            Assert.DoesNotContain('\r', response);
        }
    }

    [Fact]
    public void Every_reply_starts_with_ok_or_err()
    {
        using var host = Host("30k");

        string[] commands = ["ping", "list", "status", "stat 30k", "bogus", "", "on nosuch"];

        foreach (var command in commands)
        {
            var response = ControlProtocol.Execute(command, host).Response;

            Assert.True(
                response.StartsWith("ok", StringComparison.Ordinal)
                    || response.StartsWith("err", StringComparison.Ordinal),
                $"'{command}' answered '{response}'");
        }
    }

    private static ModuleHost Host(params string[] names) =>
        new([.. names.Select(n => new FakeRunner(n))]);

    private sealed class FakeRunner(string name, bool supportsReset = false) : IModuleRunner, IControllable
    {
        public string Name => name;

        public bool IsActive { get; set; }

        public bool SupportsReset => supportsReset;

        public string? HandleKey { get; init; }

        public IReadOnlyList<IControllable> Controls => [this];

        public ModuleKind Kind { get; init; } = ModuleKind.Hold;

        public bool IsEnabled { get; set; } = true;

        public IReadOnlyList<ModuleMetric> Metrics => [];

        public IModuleOptions? Options { get; init; }

        public ModuleStatus Status => new(name, IsActive, IsEnabled, 10, 4, 3, 2, 0);

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
