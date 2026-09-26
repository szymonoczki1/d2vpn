using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Modules;

/// <summary>
/// A resync that stops blocking entirely defeats the hold. Measured: a 3.5 s window every 8.5 s
/// blocked 53% of downlink exactly as designed, and a buff picked up during it still arrived
/// instantly - a pickup confirmation is one packet, and any open window carries it.
/// </summary>
public class SelectiveResyncTests
{
    private const int Ordinary = 600;
    private const int Request = 128;
    private const int Handshake = 233;

    private static HoldModule Downlink() =>
        new("3074 DL", DestinyPorts.Peer, MeasuredDefaults.Peer3074Downlink);

    [Fact]
    public void An_open_window_passes_the_handshake_and_nothing_else()
    {
        var module = Downlink();
        module.IsActive = true;
        module.Decide(TestPackets.Outbound(3074, Request, 1.0));

        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, Handshake, 1.5)));
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Inbound(3074, 51, 1.6)));
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, Ordinary, 1.7)));
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, 1258, 1.8)));
    }

    [Fact]
    public void A_hold_stays_near_total_even_while_resyncing_repeatedly()
    {
        // The failure this replaces: 53% blocked was enough for state to leak through.
        var module = Downlink();
        module.IsActive = true;

        var dropped = 0;
        var passed = 0;

        // Stops short of the 25 s safety timeout, which disables the module and passes everything
        // by design - correct behaviour, but not what this test is about.
        for (var step = 0; step < 200; step++)
        {
            var t = step * 0.1;

            if (step % 20 == 0)
            {
                module.Decide(TestPackets.Outbound(3074, Request, t));
                continue;
            }

            if (module.Decide(TestPackets.Inbound(3074, Ordinary, t)) == PacketVerdict.Drop)
            {
                dropped++;
            }
            else
            {
                passed++;
            }
        }

        Assert.Equal(0, passed);
        Assert.True(dropped > 150);
    }

    [Fact]
    public void Handshake_sizes_are_blocked_outside_a_window()
    {
        // They are the resync, not a standing exemption: outside one there is nothing to complete.
        var module = Downlink();
        module.IsActive = true;

        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Inbound(3074, Handshake, 0.5)));
    }

    [Fact]
    public void Uplink_also_answers_the_handshake_rather_than_opening_the_gate()
    {
        // Validated at 96% blocked over 60 seconds, with kills failing to register.
        var module = new HoldModule("3074 UL", DestinyPorts.Peer, MeasuredDefaults.Peer3074);
        module.IsActive = true;
        module.Decide(TestPackets.Outbound(3074, Request, 1.0));

        Assert.NotNull(MeasuredDefaults.Peer3074.ResyncPassLengths);
        Assert.Equal(PacketVerdict.Pass, module.Decide(TestPackets.Outbound(3074, 1258, 1.5)));

        // Ordinary game state stays blocked even with a window open - this is what stops a kill
        // reaching the server.
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Outbound(3074, 478, 1.6)));
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Outbound(3074, 726, 1.7)));
    }

    [Fact]
    public void Both_directions_now_answer_the_handshake()
    {
        Assert.NotNull(MeasuredDefaults.Peer3074.ResyncPassLengths);
        Assert.NotNull(MeasuredDefaults.Peer3074Downlink.ResyncPassLengths);
    }
}
