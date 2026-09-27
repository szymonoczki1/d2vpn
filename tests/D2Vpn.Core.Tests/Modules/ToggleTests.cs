using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Modules;

/// <summary>
/// A hotkey toggles from its own thread while the packet loop reads per-peer state, so the module
/// has to be safe to drive from two threads at once.
/// </summary>
public class ToggleTests
{
    private static HoldModule Module() =>
        new("PVE", DestinyPorts.Peer, MeasuredDefaults.Peer3074);

    [Fact]
    public void Toggle_flips_and_reports_the_new_state()
    {
        var module = Module();

        Assert.True(module.Toggle());
        Assert.True(module.IsActive);

        Assert.False(module.Toggle());
        Assert.False(module.IsActive);
    }

    [Fact]
    public void Toggling_off_restores_traffic_and_drops_peer_state()
    {
        var module = Module();
        module.Toggle();

        module.Decide(TestPackets.Outbound(3074, 128, 1.0));
        Assert.NotEmpty(module.PeerStates);

        module.Toggle();

        Assert.Empty(module.PeerStates);
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, 500, 2.0)));
    }

    [Fact]
    public void Re_arming_starts_from_a_clean_state()
    {
        var module = Module();
        module.Toggle();
        module.Decide(TestPackets.Outbound(3074, 128, 1.0));   // opens a resync
        module.Toggle();
        module.Toggle();

        // A window left over from the previous hold would wrongly pass this.
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Outbound(3074, 500, 2.0)));
    }

    [Fact]
    public async Task Toggling_concurrently_with_traffic_does_not_corrupt_state()
    {
        // Reset clears the dictionary the packet loop walks. Without a lock this throws or
        // produces torn reads; the failure is intermittent, which is why it is worth a test.
        var module = Module();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var packets = Task.Run(() =>
        {
            var t = 0.0;
            while (!cts.IsCancellationRequested)
            {
                t += 0.01;
                module.Decide(TestPackets.Outbound(3074, 500, t));
                module.Decide(TestPackets.Inbound(3074, 1258, t));
                _ = module.PeerStates.Count;
            }
        });

        var toggles = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                module.Toggle();
                Thread.Sleep(1);
            }
        });

        // Completing without an exception is the assertion.
        await Task.WhenAll(packets, toggles).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Many_peers_survive_concurrent_toggling()
    {
        var module = Module();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var packets = Task.Run(() =>
        {
            var t = 0.0;
            while (!cts.IsCancellationRequested)
            {
                t += 0.01;
                var peer = System.Net.IPAddress.Parse($"192.0.2.{(int)(t * 100) % 40 + 10}");
                module.Decide(TestPackets.Outbound(3074, 128, t, peer));
                module.Decide(TestPackets.Outbound(3074, 500, t, peer));
            }
        });

        var toggles = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                module.Toggle();
                Thread.Sleep(1);
            }
        });

        await Task.WhenAll(packets, toggles).WaitAsync(TimeSpan.FromSeconds(10));
    }
}
