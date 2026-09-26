using D2Vpn.Core.Modules;

namespace D2Vpn.Core.Tests.Modules;

/// <summary>
/// The uplink resync list is taken from what a reference limiter actually permits. Capturing one
/// of its uplink holds showed exactly 20 packets crossing in 30 seconds, in three cycles of the
/// same exchange.
/// </summary>
public class ObservedExchangeTests
{
    /// <summary>Every outbound size seen crossing a reference limiter's uplink hold.</summary>
    private static readonly int[] Permitted =
        [51, 128, 136, 174, 190, 198, 206, 233, 238, 1166];

    /// <summary>
    /// Sizes a previous list carried that the reference never let out even once.
    /// </summary>
    /// <remarks>
    /// They came from watching our own resync window, which opened fully - so they recorded what
    /// was flowing rather than what the exchange needs. Being the largest, they were also the
    /// likeliest to carry a kill through a hold.
    /// </remarks>
    private static readonly int[] NeverPermitted = [720, 795, 1254, 1258];

    [Fact]
    public void Everything_the_reference_permits_can_cross_during_a_resync()
    {
        var options = MeasuredDefaults.Peer3074;

        foreach (var length in Permitted)
        {
            var crosses = options.ResyncPassLengths!.Contains(length)
                || options.PassLengths.Contains(length);

            Assert.True(crosses, $"{length} is part of the observed exchange and must cross");
        }
    }

    [Fact]
    public void Sizes_the_reference_never_permits_are_not_on_either_list()
    {
        var options = MeasuredDefaults.Peer3074;

        foreach (var length in NeverPermitted)
        {
            Assert.DoesNotContain(length, options.ResyncPassLengths!);
            Assert.DoesNotContain(length, options.PassLengths);
        }
    }

    [Fact]
    public void Ordinary_uplink_sizes_are_on_neither_list()
    {
        // The most common sizes in normal outbound traffic, which carry game state.
        var options = MeasuredDefaults.Peer3074;

        foreach (var ordinary in new[] { 214, 222, 230, 470, 478, 486 })
        {
            Assert.DoesNotContain(ordinary, options.ResyncPassLengths!);
            Assert.DoesNotContain(ordinary, options.PassLengths);
        }
    }
}
