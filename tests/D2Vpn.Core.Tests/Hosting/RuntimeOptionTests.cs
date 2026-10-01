using D2Vpn.Core.Hosting;
using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Hosting;

/// <summary>
/// Autoresync and buffering, switched while a session runs.
/// </summary>
/// <remarks>
/// The property that matters is reversibility: turning a switch off and on again has to restore
/// the measured settings, not an emptied version of them. That is why the implementations derive
/// live options from a kept baseline instead of mutating in place.
/// </remarks>
public class RuntimeOptionTests
{
    [Fact]
    public void A_udp_hold_starts_with_both_switches_on()
    {
        var toggles = new HoldModuleToggles(Hold());

        Assert.True(toggles.SupportsResync);
        Assert.True(toggles.ResyncEnabled);
        Assert.True(toggles.BufferEnabled);
        Assert.Equal("resync=on buffer=on", toggles.ToLine());
    }

    [Fact]
    public void Turning_resync_off_clears_the_triggers_and_back_on_restores_them()
    {
        // Clearing the trigger lengths is how a window is prevented from ever opening, and it is
        // what --no-resync does. Restoring has to put the measured set back.
        var module = Hold();
        var measured = module.Options.ResyncTriggerLengths.Order().ToArray();
        var toggles = new HoldModuleToggles(module);

        toggles.ResyncEnabled = false;
        Assert.Empty(module.Options.ResyncTriggerLengths);

        toggles.ResyncEnabled = true;
        Assert.Equal(measured, module.Options.ResyncTriggerLengths.Order());
    }

    [Fact]
    public void Turning_the_buffer_off_and_on_reaches_the_module()
    {
        var module = Hold();
        var toggles = new HoldModuleToggles(module);

        toggles.BufferEnabled = false;
        Assert.False(module.Options.Buffering);

        toggles.BufferEnabled = true;
        Assert.True(module.Options.Buffering);
    }

    [Fact]
    public void Switching_resync_does_not_disturb_the_buffer()
    {
        // Both switches derive from one baseline, so a bug here would have each clobber the other.
        var module = Hold();
        var toggles = new HoldModuleToggles(module);

        toggles.BufferEnabled = false;
        toggles.ResyncEnabled = false;
        toggles.ResyncEnabled = true;

        Assert.False(toggles.BufferEnabled);
        Assert.False(module.Options.Buffering);
    }

    [Fact]
    public void A_delay_line_refuses_to_give_up_its_buffer()
    {
        // Not a weaker setting but a broken one: a packet cannot be delivered late unless it is
        // kept. The CLI rejects the same combination at startup.
        var module = Hold(slow: true);
        var toggles = new HoldModuleToggles(module);

        Assert.False(toggles.SupportsBuffer);
        Assert.Throws<InvalidOperationException>(() => toggles.BufferEnabled = false);
        Assert.True(module.Options.Buffering);
    }

    [Fact]
    public void A_30k_hold_exposes_its_keepalive_as_resync()
    {
        // On TCP the switch is the pass list - the 73-byte heartbeat, without which a hold dies in
        // about twenty seconds. Same switch, different field from UDP.
        var module = new ApiBlockModule("30k", DestinyPorts.Instance, ApiBlockOptions.Instance30k);
        var toggles = new ApiBlockToggles(module);

        Assert.True(toggles.SupportsResync);
        Assert.Contains(73, module.Options.PassLengths);

        toggles.ResyncEnabled = false;
        Assert.Empty(module.Options.PassLengths);

        toggles.ResyncEnabled = true;
        Assert.Contains(73, module.Options.PassLengths);
    }

    [Fact]
    public void A_7500_hold_has_no_keepalive_to_switch()
    {
        // Nothing has been identified on 7500 and its pass list is empty, so the switch is not
        // applicable rather than off - which is what "n/a" says.
        var module = new ApiBlockModule("7500", DestinyPorts.Api, ApiBlockOptions.Api7500);
        var toggles = new ApiBlockToggles(module);

        Assert.False(toggles.SupportsResync);
        Assert.Equal("resync=n/a buffer=on", toggles.ToLine());
    }

    [Fact]
    public void A_30k_delay_line_keeps_its_keepalive_but_not_its_buffer_switch()
    {
        var module = new ApiBlockModule(
            "30k-slow", DestinyPorts.Instance, ApiBlockOptions.Instance30kSlow);
        var toggles = new ApiBlockToggles(module);

        Assert.True(toggles.SupportsResync);
        Assert.False(toggles.SupportsBuffer);
        Assert.Throws<InvalidOperationException>(() => toggles.BufferEnabled = false);
    }

    [Fact]
    public void The_protocol_reads_and_writes_the_switches()
    {
        var module = Hold();
        using var host = new ModuleHost(
            [new FakeRunner("3074-ul") { Options = new HoldModuleToggles(module) }]);

        Assert.Equal(
            "ok 3074-ul resync=on buffer=on",
            ControlProtocol.Execute("opts 3074-ul", host).Response);

        Assert.Equal(
            "ok 3074-ul resync=off buffer=on",
            ControlProtocol.Execute("set 3074-ul resync off", host).Response);

        Assert.Empty(module.Options.ResyncTriggerLengths);

        Assert.Equal(
            "ok 3074-ul resync=off buffer=off",
            ControlProtocol.Execute("set 3074-ul buffer 0", host).Response);

        Assert.False(module.Options.Buffering);
    }

    [Fact]
    public void The_protocol_reports_bad_set_commands_without_throwing()
    {
        using var host = new ModuleHost(
            [new FakeRunner("3074-ul") { Options = new HoldModuleToggles(Hold()) }]);

        foreach (var command in new[]
                 {
                     "set", "set 3074-ul", "set 3074-ul resync",
                     "set 3074-ul resync maybe", "set 3074-ul sideways on",
                     "set nosuch resync on"
                 })
        {
            var response = ControlProtocol.Execute(command, host).Response;

            Assert.StartsWith("err", response);
            Assert.DoesNotContain('\n', response);
        }
    }

    [Fact]
    public void A_module_with_no_switches_says_so_rather_than_failing()
    {
        using var host = new ModuleHost([new FakeRunner("pause")]);

        Assert.Equal(
            "ok pause resync=n/a buffer=n/a",
            ControlProtocol.Execute("opts pause", host).Response);

        Assert.StartsWith("err", ControlProtocol.Execute("set pause buffer on", host).Response);
    }

    private static HoldModule Hold(bool slow = false)
    {
        var options = MeasuredDefaults.Peer3074 with { Buffering = true, SlowMode = slow };
        return new HoldModule("3074-ul", DestinyPorts.Peer, options);
    }

    private sealed class FakeRunner(string name) : IModuleRunner, IControllable
    {
        public string Name => name;

        public string? HandleKey => null;

        public IReadOnlyList<IControllable> Controls => [this];

        public ModuleKind Kind { get; init; } = ModuleKind.Hold;

        public bool IsEnabled { get; set; } = true;

        public IReadOnlyList<ModuleMetric> Metrics => [];

        public IModuleOptions? Options { get; init; }

        public bool IsActive { get; set; }

        public bool SupportsReset => false;

        public ModuleStatus Status => new(name, IsActive, IsEnabled, 0, 0, 0, 0, 0);

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
