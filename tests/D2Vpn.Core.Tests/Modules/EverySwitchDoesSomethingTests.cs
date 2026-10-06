using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Tests.Modules;

/// <summary>
/// Every shipped hold, every switch, asserted to actually do something.
/// </summary>
/// <remarks>
/// <para>
/// Written after buffering was found to be inert on a downlink hold - visible in the window,
/// saved to the config, and changing no verdict at all. It survived because the tests around it
/// pinned the behaviour rather than the intent: one was called
/// <c>A_blocked_inbound_packet_is_dropped_even_with_buffering_on</c> and passed for months.
/// A test that encodes an assumption cannot catch the assumption being wrong.
/// </para>
/// <para>
/// So these assert the weakest thing that is still worth knowing: flipping a switch has to change
/// an answer somewhere. Not which answer - that is the measured defaults' business and differs per
/// module - only that the control is connected to something. A switch with no observable effect is
/// the failure this is for, and it is the one that cannot be noticed from the outside.
/// </para>
/// <para>
/// Every shipped set is covered by construction: <see cref="Shipped"/> is the list, so a new
/// module added without a signature shows up here as a failure rather than as silence.
/// </para>
/// </remarks>
public class EverySwitchDoesSomethingTests
{
    /// <summary>Every hold the app can actually host, with a port its group contains.</summary>
    public static TheoryData<string> Shipped =>
    [
        "3074-ul", "3074-dl", "3074-full",
        "27k-ul", "27k-dl", "27k-full"
    ];

    private static (HoldModuleOptions Options, PortGroup Group, int Port) Resolve(string name)
    {
        var group = name.StartsWith("3074", StringComparison.Ordinal)
            ? DestinyPorts.Peer
            : DestinyPorts.Players;

        var direction = name.EndsWith("-dl", StringComparison.Ordinal)
            ? HoldDirection.Downlink
            : name.EndsWith("-full", StringComparison.Ordinal)
                ? HoldDirection.Both
                : HoldDirection.Uplink;

        return (MeasuredDefaults.For(group, direction)!, group, group.FromPort);
    }

    /// <summary>
    /// A payload length this module blocks outright: in no pass list and over any threshold.
    /// </summary>
    /// <remarks>
    /// Searched rather than hardcoded, so it stays correct when a signature changes. Every
    /// assertion below needs a packet the module has no reason to let through; picking one by hand
    /// is how a test quietly stops testing anything the next time a list grows.
    /// </remarks>
    private static int BlockedSize(HoldModuleOptions options)
    {
        for (var size = 401; size < 4000; size++)
        {
            if (options.PassLengths.Contains(size)
                || options.ResyncPassLengths?.Contains(size) == true
                || options.ResyncTriggerLengths.Contains(size)
                || size <= options.ResyncPassMaxLength)
            {
                continue;
            }

            return size;
        }

        throw new InvalidOperationException("no size this module blocks - that cannot be right");
    }

    private static PacketInfoMaker Held(HoldModuleOptions options, int port) =>
        options.Direction == HoldDirection.Downlink
            ? (size, at) => TestPackets.Inbound(port, size, at)
            : (size, at) => TestPackets.Outbound(port, size, at);

    private delegate D2Vpn.Core.Interception.PacketInfo PacketInfoMaker(int size, double at);

    // ---- the hold itself ----

    [Theory]
    [MemberData(nameof(Shipped))]
    public void An_active_hold_blocks_something(string name)
    {
        var (options, group, port) = Resolve(name);
        var module = new HoldModule(name, group, options) { IsActive = true };

        Assert.NotEqual(
            PacketVerdict.Pass,
            module.Decide(Held(options, port)(BlockedSize(options), 0)));
    }

    [Theory]
    [MemberData(nameof(Shipped))]
    public void An_inactive_hold_blocks_nothing(string name)
    {
        var (options, group, port) = Resolve(name);
        var module = new HoldModule(name, group, options);

        Assert.Equal(
            PacketVerdict.Pass,
            module.Decide(Held(options, port)(BlockedSize(options), 0)));
    }

    // ---- the buffer switch ----

    [Theory]
    [MemberData(nameof(Shipped))]
    public void Buffering_changes_what_happens_to_a_blocked_packet(string name)
    {
        // The one that was broken. With buffering off a blocked packet is destroyed; with it on
        // the same packet is kept. If both answers are the same, the switch is decoration.
        var (options, group, port) = Resolve(name);
        var size = BlockedSize(options);

        var off = new HoldModule(name, group, options with { Buffering = false })
        {
            IsActive = true
        };

        var on = new HoldModule(name, group, options with { Buffering = true })
        {
            IsActive = true
        };

        Assert.Equal(PacketVerdict.Drop, off.Decide(Held(options, port)(size, 0)));
        Assert.Equal(PacketVerdict.Buffer, on.Decide(Held(options, port)(size, 0)));
    }

    // ---- the resync switch ----

    [Theory]
    [MemberData(nameof(Shipped))]
    public void A_trigger_opens_a_window_that_was_not_open(string name)
    {
        // Something must cross after a trigger that would not have crossed before it, or the
        // resync is doing nothing. The probe is whatever the window allows and the standing list
        // does not - if there is no such size, the window cannot be observed at all and that is
        // itself worth failing on.
        var (options, group, port) = Resolve(name);
        var module = new HoldModule(name, group, options) { IsActive = true };
        var held = Held(options, port);

        var probe = WindowOnlySize(options);
        Assert.NotEqual(PacketVerdict.Pass, module.Decide(held(probe, 0)));

        module.Decide(Trigger(options, port));
        Assert.Equal(PacketVerdict.Pass, module.Decide(held(probe, 1.1)));
    }

    [Theory]
    [MemberData(nameof(Shipped))]
    public void Disarming_the_resync_stops_the_window_opening(string name)
    {
        // How the window is turned off: the trigger list is cleared, so nothing can open one.
        var (options, group, port) = Resolve(name);

        var module = new HoldModule(
            name, group, options with { ResyncTriggerLengths = new HashSet<int>() })
        {
            IsActive = true
        };

        module.Decide(Trigger(options, port));

        Assert.NotEqual(
            PacketVerdict.Pass,
            module.Decide(Held(options, port)(WindowOnlySize(options), 1.1)));
    }

    /// <summary>A size the resync window allows and the standing list does not.</summary>
    private static int WindowOnlySize(HoldModuleOptions options)
    {
        if (options.ResyncPassLengths is { } allowed)
        {
            foreach (var size in allowed)
            {
                if (!options.PassLengths.Contains(size))
                {
                    return size;
                }
            }
        }

        // A null resync list means the window passes everything, so anything blocked will do.
        if (options.ResyncPassLengths is null)
        {
            return BlockedSize(options);
        }

        throw new InvalidOperationException(
            $"every size {nameof(options.ResyncPassLengths)} allows is already passed standing, "
            + "so this module's resync window cannot be observed to do anything");
    }

    /// <summary>A packet that opens this module's resync window.</summary>
    private static D2Vpn.Core.Interception.PacketInfo Trigger(HoldModuleOptions options, int port)
    {
        var size = options.ResyncTriggerLengths.First();

        return options.EffectiveTriggerDirection == PacketDirection.Inbound
            ? TestPackets.Inbound(port, size, 1.0)
            : TestPackets.Outbound(port, size, 1.0);
    }
}
