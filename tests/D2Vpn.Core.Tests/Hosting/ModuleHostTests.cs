using D2Vpn.Core.Hosting;

namespace D2Vpn.Core.Tests.Hosting;

/// <summary>
/// The host's bookkeeping, against fake runners.
/// </summary>
/// <remarks>
/// Everything here runs without administrator rights or a network, which is the point of the
/// <see cref="IModuleRunner"/> seam: the state a front-end reads and the release paths a frozen
/// session depends on are testable, while the WinDivert handle stays behind an interface.
/// </remarks>
public class ModuleHostTests
{
    [Fact]
    public void Toggling_flips_one_module_and_leaves_the_others()
    {
        using var host = new ModuleHost([Fake("3074-ul"), Fake("30k")]);

        Assert.True(host.Toggle("3074-ul"));
        Assert.Equal(1, host.ActiveCount);

        var status = host.Snapshot();
        Assert.True(status.Single(s => s.Name == "3074-ul").IsActive);
        Assert.False(status.Single(s => s.Name == "30k").IsActive);

        Assert.False(host.Toggle("3074-ul"));
        Assert.Equal(0, host.ActiveCount);
    }

    [Fact]
    public void Setting_a_state_it_already_has_reports_no_change()
    {
        using var host = new ModuleHost([Fake("30k")]);

        Assert.True(host.Set("30k", true));
        Assert.False(host.Set("30k", true));
        Assert.True(host.Set("30k", false));
        Assert.False(host.Set("30k", false));
    }

    [Fact]
    public void Releasing_everything_returns_how_many_were_holding()
    {
        using var host = new ModuleHost([Fake("a"), Fake("b"), Fake("c")]);

        host.Set("a", true);
        host.Set("c", true);

        Assert.Equal(2, host.ReleaseAll());
        Assert.Equal(0, host.ActiveCount);

        // Safe to call again, which is what makes it usable as both a panic button and a shutdown
        // path without the caller tracking whether it already ran.
        Assert.Equal(0, host.ReleaseAll());
    }

    [Fact]
    public void State_changes_are_announced_once_each()
    {
        using var host = new ModuleHost([Fake("a"), Fake("b")]);

        List<(string Name, bool Active)> seen = [];
        host.StateChanged += (name, active) => seen.Add((name, active));

        host.Toggle("a");
        host.Set("b", true);
        host.Set("b", true);
        host.ReleaseAll();

        Assert.Equal(
            [("a", true), ("b", true), ("a", false), ("b", false)],
            seen);
    }

    [Fact]
    public void Duplicate_names_are_rejected()
    {
        // Names are the address a macro binds to, so two modules answering to one name is a
        // configuration error rather than something to resolve arbitrarily.
        var ex = Assert.Throws<ArgumentException>(() =>
            new ModuleHost([Fake("30k"), Fake("30k")]));

        Assert.Contains("30k", ex.Message);
    }

    [Fact]
    public void Two_modules_claiming_one_handle_are_rejected()
    {
        // The failure that has no symptom. A WinDivert filter is keyed on the port group alone, so
        // 3074-ul and 3074-dl produce identical filters; hosting both would leave the second
        // seeing only what the first reinjected, with nothing to explain why it did nothing.
        var ex = Assert.Throws<ArgumentException>(() => new ModuleHost(
        [
            Fake("3074-ul", handleKey: "udp and remotePort == 3074"),
            Fake("3074-dl", handleKey: "udp and remotePort == 3074")
        ]));

        Assert.Contains("3074-ul", ex.Message);
        Assert.Contains("3074-dl", ex.Message);
        Assert.Contains("same ports", ex.Message);
    }

    [Fact]
    public void Modules_on_different_handles_are_allowed()
    {
        using var host = new ModuleHost(
        [
            Fake("3074-ul", handleKey: "udp and remotePort == 3074"),
            Fake("30k", handleKey: "tcp and remotePort >= 30000")
        ]);

        Assert.Equal(2, host.Names.Count);
    }

    [Fact]
    public void Runners_without_a_handle_never_collide()
    {
        // Every other test in this file hosts fakes that open nothing, and they must stay hostable
        // together regardless of how many there are.
        using var host = new ModuleHost([Fake("a"), Fake("b"), Fake("c")]);

        Assert.Equal(3, host.Names.Count);
    }

    [Fact]
    public void A_host_with_no_modules_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ModuleHost([]));
    }

    [Fact]
    public void An_unknown_name_says_what_is_hosted()
    {
        using var host = new ModuleHost([Fake("3074-ul"), Fake("27k-ul")]);

        var ex = Assert.Throws<KeyNotFoundException>(() => host.Toggle("7500"));

        Assert.Contains("3074-ul", ex.Message);
        Assert.Contains("27k-ul", ex.Message);
    }

    [Fact]
    public void Reset_is_refused_on_a_module_that_cannot_reset()
    {
        var resettable = new FakeRunner("30k", supportsReset: true, fails: false);
        using var host = new ModuleHost([Fake("3074-ul"), resettable]);

        host.RequestReset("30k");
        Assert.Equal(1, resettable.Resets);

        Assert.Throws<InvalidOperationException>(() => host.RequestReset("3074-ul"));
    }

    [Fact]
    public async Task Running_releases_everything_when_cancelled()
    {
        // The guarantee that matters: a session ending with modules still marked active would
        // report a hold that nothing is left to release.
        var runners = new[] { Fake("a"), Fake("b") };
        using var host = new ModuleHost(runners);
        using var cts = new CancellationTokenSource();

        host.Set("a", true);
        host.Set("b", true);

        var run = host.RunAsync(cts.Token);
        await cts.CancelAsync();

        // Cancellation is the normal way a session ends, so the host returns rather than throwing.
        await run;

        Assert.Equal(0, host.ActiveCount);
    }

    [Fact]
    public async Task A_module_that_throws_takes_the_host_down_and_releases()
    {
        // A handle that has stopped diverting while the status line still claims a hold is worse
        // than stopping, so a failure is not swallowed.
        using var host = new ModuleHost([Fake("a"), Fake("bad", fails: true)]);
        host.Set("a", true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.RunAsync(CancellationToken.None));

        Assert.Equal(0, host.ActiveCount);
    }

    [Fact]
    public void Disposing_releases_and_disposes_every_runner()
    {
        var runners = new[] { Fake("a"), Fake("b") };
        var host = new ModuleHost(runners);

        host.Set("a", true);
        host.Dispose();

        Assert.All(runners, r => Assert.True(r.Disposed));
        Assert.All(runners, r => Assert.False(r.IsActive));
    }

    private static FakeRunner Fake(
        string name,
        bool supportsReset = false,
        bool fails = false,
        string? handleKey = null) =>
        new(name, supportsReset, fails) { HandleKey = handleKey };

    private sealed class FakeRunner(string name, bool supportsReset, bool fails) : IModuleRunner
    {
        public string Name => name;

        public bool IsActive { get; set; }

        public bool SupportsReset => supportsReset;

        public string? HandleKey { get; init; }

        public int Resets { get; private set; }

        public bool Disposed { get; private set; }

        public ModuleStatus Status => new(name, IsActive, 10, 4, 3, 2, Resets);

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            if (fails)
            {
                throw new InvalidOperationException("handle failed");
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public void RequestReset() => Resets++;

        public void Dispose() => Disposed = true;
    }
}
