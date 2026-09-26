using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Modules;

/// <summary>
/// A hold that reports only totals is uninterpretable. One real run dropped 62% of everything and
/// survived 45 seconds, and there was no way to tell whether the resync had carried it or whether
/// the block had simply not been hard enough for the game to notice.
/// </summary>
public class InstrumentationTests
{
    private static HoldModule Module() =>
        new("PVE", DestinyPorts.Peer, MeasuredDefaults.Peer3074);

    [Fact]
    public void Trigger_sightings_are_counted()
    {
        var module = Module();
        module.IsActive = true;

        module.Decide(TestPackets.Outbound(3074, 500, 0));
        Assert.Equal(0, module.TriggersSeen);

        module.Decide(TestPackets.Outbound(3074, 128, 1));
        module.Decide(TestPackets.Outbound(3074, 136, 2));

        Assert.Equal(2, module.TriggersSeen);
    }

    [Fact]
    public void Repeated_requests_inside_one_window_count_as_one_resync()
    {
        // The game asks repeatedly while unanswered. Counting each as a new resync would suggest
        // the module is cycling when it is really holding one window open.
        var module = Module();
        module.IsActive = true;

        module.Decide(TestPackets.Outbound(3074, 128, 1.0));
        module.Decide(TestPackets.Outbound(3074, 128, 1.5));
        module.Decide(TestPackets.Outbound(3074, 128, 2.0));

        Assert.Equal(3, module.TriggersSeen);
        Assert.Equal(1, module.ResyncsStarted);
    }

    [Fact]
    public void A_request_after_a_window_expires_starts_a_new_resync()
    {
        var module = Module();
        module.IsActive = true;

        module.Decide(TestPackets.Outbound(3074, 128, 1.0));
        module.Decide(TestPackets.Outbound(3074, 500, 5.0));   // expires the window
        module.Decide(TestPackets.Outbound(3074, 128, 6.0));

        Assert.Equal(2, module.ResyncsStarted);
    }

    [Fact]
    public void Counters_reset_with_the_module()
    {
        var module = Module();
        module.IsActive = true;
        module.Decide(TestPackets.Outbound(3074, 128, 1.0));

        module.IsActive = false;

        Assert.Equal(0, module.TriggersSeen);
        Assert.Equal(0, module.ResyncsStarted);
    }

    [Fact]
    public void Zero_triggers_means_the_resync_cannot_have_run()
    {
        // The diagnostic the CLI keys on: no request seen, so a surviving hold was not the
        // resync's doing and something else needs explaining.
        var module = Module();
        module.IsActive = true;

        for (var t = 0.0; t < 30; t += 0.1)
        {
            module.Decide(TestPackets.Outbound(3074, 500, t));
        }

        Assert.Equal(0, module.TriggersSeen);
        Assert.Equal(0, module.ResyncsStarted);
    }
}
