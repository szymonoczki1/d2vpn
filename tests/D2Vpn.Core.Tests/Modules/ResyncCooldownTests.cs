using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Modules;

/// <summary>
/// Without a cooldown a hold barely blocks. The game asks to reconnect every couple of seconds
/// while held, and opening a window on every request leaves the module open almost permanently:
/// a measured downlink hold ran six 3.5 s resyncs inside 25 seconds and dropped only 39% of
/// traffic. An object picked up during it registered instantly.
/// </summary>
public class ResyncCooldownTests
{
    private const int Ordinary = 500;
    private const int Request = 128;

    private static HoldModule Module(TimeSpan? cooldown = null) =>
        new("PVE", DestinyPorts.Peer,
            MeasuredDefaults.Peer3074 with { ResyncCooldown = cooldown ?? TimeSpan.FromSeconds(5) });

    [Fact]
    public void Repeated_requests_do_not_hold_the_module_permanently_open()
    {
        // The game asking every 2 s used to mean a window open every 2 s, forever.
        var module = Module();
        module.IsActive = true;

        var dropped = 0;
        var passed = 0;

        for (var step = 0; step < 400; step++)
        {
            var t = step * 0.1;

            // The game asks every 2 s while it is being held.
            if (step % 20 == 0)
            {
                module.Decide(TestPackets.Outbound(3074, Request, t));
                continue;
            }

            if (module.Decide(TestPackets.Outbound(3074, Ordinary, t)) == PacketVerdict.Drop)
            {
                dropped++;
            }
            else
            {
                passed++;
            }
        }

        // With a 3.5 s window and a 5 s cooldown the cycle is ~8.5 s, so most of it blocks.
        Assert.True(dropped > passed, $"dropped {dropped} vs passed {passed} - the hold is barely blocking");
    }

    [Fact]
    public void A_request_during_the_cooldown_is_counted_but_ignored()
    {
        var module = Module();
        module.IsActive = true;

        module.Decide(TestPackets.Outbound(3074, Request, 0));
        module.Decide(TestPackets.Outbound(3074, Ordinary, 4.0));   // window expires
        module.Decide(TestPackets.Outbound(3074, Request, 5.0));    // 1 s into blocking

        Assert.Equal(2, module.TriggersSeen);
        Assert.Equal(1, module.ResyncsStarted);
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Outbound(3074, Ordinary, 5.5)));
    }

    [Fact]
    public void A_request_inside_an_open_window_does_not_extend_it()
    {
        var module = Module();
        module.IsActive = true;

        module.Decide(TestPackets.Outbound(3074, Request, 0));
        module.Decide(TestPackets.Outbound(3074, Request, 2.0));

        // The window is a fixed 3.5 s from the request that opened it.
        Assert.Equal(PacketVerdict.Drop, module.Decide(TestPackets.Outbound(3074, Ordinary, 5.0)));
        Assert.Equal(1, module.ResyncsStarted);
    }

    [Fact]
    public void The_first_request_is_never_delayed_by_the_cooldown()
    {
        // Nothing has been blocked yet, so there is nothing to cool down from.
        var module = Module();
        module.IsActive = true;

        module.Decide(TestPackets.Outbound(3074, Request, 0.1));

        Assert.Equal(1, module.ResyncsStarted);
    }

    [Fact]
    public void A_zero_cooldown_restores_the_old_permanently_open_behaviour()
    {
        // Documents what the cooldown is for by showing its absence.
        var module = Module(TimeSpan.Zero);
        module.IsActive = true;

        module.Decide(TestPackets.Outbound(3074, Request, 0));
        module.Decide(TestPackets.Outbound(3074, Ordinary, 4.0));
        module.Decide(TestPackets.Outbound(3074, Request, 4.1));

        Assert.Equal(2, module.ResyncsStarted);
    }
}
