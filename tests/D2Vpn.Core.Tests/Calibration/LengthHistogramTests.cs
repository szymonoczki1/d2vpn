using D2Vpn.Core.Calibration;

namespace D2Vpn.Core.Tests.Calibration;

public class LengthHistogramTests
{
    [Fact]
    public void Counts_accumulate_per_length()
    {
        var h = new LengthHistogram();
        h.Add(198);
        h.Add(198);
        h.Add(128);

        Assert.Equal(3, h.Total);
        Assert.Equal(2, h.CountOf(198));
        Assert.Equal(1, h.CountOf(128));
        Assert.Equal(0, h.CountOf(999));
    }

    [Fact]
    public void DistinctLengths_are_ascending()
    {
        var h = new LengthHistogram();
        foreach (var len in new[] { 1166, 51, 198, 128 })
        {
            h.Add(len);
        }

        Assert.Equal([51, 128, 198, 1166], h.DistinctLengths);
    }

    [Fact]
    public void Largest_is_null_until_something_is_recorded()
    {
        var h = new LengthHistogram();
        Assert.Null(h.Largest);

        h.Add(184);
        Assert.Equal(184, h.Largest);
    }

    [Fact]
    public void MostCommon_orders_by_count_then_length()
    {
        var h = new LengthHistogram();
        h.Add(200); h.Add(200); h.Add(200);
        h.Add(100); h.Add(100);
        h.Add(300);

        var top = h.MostCommon(2);

        Assert.Equal(new LengthCount(200, 3), top[0]);
        Assert.Equal(new LengthCount(100, 2), top[1]);
    }

    [Fact]
    public void LengthsNotIn_returns_only_what_the_other_side_never_saw()
    {
        var baseline = new LengthHistogram();
        baseline.Add(570);
        baseline.Add(586);

        var active = new LengthHistogram();
        active.Add(128);
        active.Add(570);
        active.Add(1166);

        Assert.Equal([128, 1166], active.LengthsNotIn(baseline));
    }

    [Fact]
    public void Negative_lengths_are_rejected()
    {
        var h = new LengthHistogram();
        Assert.Throws<ArgumentOutOfRangeException>(() => h.Add(-1));
    }
}
