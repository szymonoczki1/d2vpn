namespace D2Vpn.Core.Calibration;

/// <summary>
/// One packet as the analyser sees it: when it arrived and how big its payload was.
/// </summary>
/// <param name="Offset">Seconds since the first packet of the run.</param>
/// <param name="Length">Transport payload size in bytes.</param>
public readonly record struct Observation(double Offset, int Length);

/// <summary>
/// The result of looking for holds in one stream of packets.
/// </summary>
/// <param name="BaselineRate">Uplink rate when the game is sending, in packets per second.</param>
/// <param name="Holds">Every collapse found, in time order.</param>
public readonly record struct StreamAnalysis(double BaselineRate, IReadOnlyList<HoldSpan> Holds)
{
    /// <summary>True when at least one collapse was found.</summary>
    public bool HoldDetected => Holds.Count > 0;

    /// <summary>
    /// Looks for sustained collapses in uplink.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run against one peer's traffic rather than everything at once. Destiny talks to several
    /// peers on 3074 simultaneously, each on its own ~10 Hz tick, and they come and go during a
    /// session. An aggregate rate blends them, so a peer that goes quiet is diluted by the peers
    /// that did not. Every reference limiter keeps its state per remote address for the same reason.
    /// </para>
    /// <para>
    /// <b>All collapses, not the longest one.</b> A single keypress does not produce a single
    /// block. Measured across four peers at once: a 20-second hold produced a collapse at s13-16,
    /// a return to full rate at s17-20, then a second collapse at s21-28. The middle stretch is
    /// the limiter letting traffic through while the game re-establishes with its peer - the
    /// reference implementation does exactly that, returning "allow" for most packets while a
    /// resync is in flight. Reporting only the longest collapse would discard half the evidence.
    /// </para>
    /// </remarks>
    public static StreamAnalysis Analyse(IReadOnlyList<Observation> outbound, CalibrationOptions options)
    {
        if (outbound.Count == 0)
        {
            return new StreamAnalysis(0, []);
        }

        var bucketSeconds = options.BucketSize.TotalSeconds;
        var ordered = outbound.OrderBy(o => o.Offset).ToArray();
        var lastBucket = (int)(ordered[^1].Offset / bucketSeconds);

        var counts = new int[lastBucket + 1];
        foreach (var o in ordered)
        {
            counts[Math.Clamp((int)(o.Offset / bucketSeconds), 0, lastBucket)]++;
        }

        // A peer is only analysable between its first and last packet. Offsets are global so that
        // every peer shares one timeline, which means a peer that joins late carries leading zeros
        // and one that leaves early carries trailing zeros - and silence is not a hold. Measured:
        // a peer whose first packet arrived at s44 was reported as "HELD 5-44s, 0% of normal",
        // and the leading zeros dragged its baseline to 2/s when its real rate was ~18/s.
        var firstBucket = (int)(ordered[0].Offset / bucketSeconds);
        var live = counts[firstBucket..(lastBucket + 1)];

        var baselineRate = ComputeBaselineRate(live, options);

        var holds = new List<HoldSpan>();
        foreach (var (liveStart, liveEnd) in FindCollapses(live, baselineRate, options))
        {
            var start = liveStart + firstBucket;
            var end = liveEnd + firstBucket;
            var refined = RefineStart(ordered, start * bucketSeconds, bucketSeconds, options);
            holds.Add(new HoldSpan(refined, (end + 1) * bucketSeconds, start, end));
        }

        return new StreamAnalysis(baselineRate, holds);
    }

    /// <summary>
    /// The rate uplink runs at when the game is actually sending, as an upper percentile.
    /// </summary>
    /// <remarks>
    /// Two earlier attempts failed on real runs in opposite directions. The median of the opening
    /// five buckets read 20 packets/second off a run that mostly sat at 10, because those seconds
    /// caught a busy patch. The median of the whole run then reported 3 packets/second for a run
    /// carrying 531 packets in 60 seconds - traffic here alternates between quiet and busy, and a
    /// median can land in the quiet mode and put the collapse threshold below what a real hold
    /// produces. An upper percentile describes the busy mode directly.
    /// </remarks>
    private static double ComputeBaselineRate(int[] counts, CalibrationOptions options)
    {
        var sample = counts.Order().ToArray();
        var index = Math.Clamp((int)(sample.Length * options.BaselinePercentile), 0, sample.Length - 1);
        return sample[index] / options.BucketSize.TotalSeconds;
    }

    /// <summary>
    /// Moves the start of the hold from a bucket edge onto the moment traffic actually stopped.
    /// </summary>
    /// <remarks>
    /// One-second buckets are right for spotting a collapse and too coarse for its edge. A hold
    /// begins partway through a second, so the opening bucket holds ordinary packets from before
    /// the keypress and then the hold. Those strays enter the signature as sizes that are plainly
    /// normal game state - in one measured run, three packets in the opening bucket produced three
    /// bogus sizes. Ordinary traffic arrives every 50-100 ms; once a hold engages gaps become
    /// seconds, so the first large gap marks the real boundary.
    /// </remarks>
    private static double RefineStart(
        Observation[] ordered, double coarseStart, double bucketSeconds, CalibrationOptions options)
    {
        var searchFrom = coarseStart - bucketSeconds;
        var searchTo = coarseStart + (2 * bucketSeconds);
        var minGap = options.MinimumHoldGap.TotalSeconds;

        for (var i = 1; i < ordered.Length; i++)
        {
            var previous = ordered[i - 1].Offset;
            if (previous < searchFrom)
            {
                continue;
            }

            if (previous > searchTo)
            {
                break;
            }

            if (ordered[i].Offset - previous >= minGap)
            {
                return previous;
            }
        }

        // No clear transition, so trust the bucket edge. Slightly late beats inventing a boundary
        // that no gap in the traffic supports.
        return coarseStart;
    }

    /// <summary>
    /// Finds every stretch of collapsed uplink, in time order.
    /// </summary>
    /// <remarks>
    /// Runs are found with the lenient <see cref="CalibrationOptions.BucketTolerance"/> so a
    /// handshake burst partway through a hold does not split one in two, then kept only if the
    /// run's mean rate clears the strict <see cref="CalibrationOptions.CollapseThreshold"/> - a
    /// window must be quiet on average, not merely quiet in places. The opening warmup buckets
    /// cannot match, so a run that starts before traffic ramps up will not report its own quiet
    /// opening as a hold.
    /// </remarks>
    private static List<(int Start, int End)> FindCollapses(
        int[] counts, double baselineRate, CalibrationOptions options)
    {
        var found = new List<(int Start, int End)>();
        if (baselineRate <= 0)
        {
            return found;
        }

        var bucketSeconds = options.BucketSize.TotalSeconds;
        var perBucketLimit = baselineRate * options.BucketTolerance * bucketSeconds;
        var meanLimit = baselineRate * options.CollapseThreshold;

        var burstLimit = baselineRate * options.ReleaseBurstFactor * bucketSeconds;

        void Accept(int start, int end)
        {
            var length = end - start + 1;
            if (length < options.MinimumCollapsedBuckets)
            {
                return;
            }

            // A collapse opening right after a release burst is the tail of that release settling,
            // not a new hold. Without this, draining a buffer manufactures a phantom hold whose
            // sizes then enter the signature.
            if (start > 0 && counts[start - 1] > burstLimit)
            {
                return;
            }

            var total = 0;
            for (var i = start; i <= end; i++)
            {
                total += counts[i];
            }

            if (total / (length * bucketSeconds) <= meanLimit)
            {
                found.Add((start, end));
            }
        }

        var runStart = -1;
        for (var i = options.WarmupBuckets; i < counts.Length; i++)
        {
            if (counts[i] <= perBucketLimit)
            {
                if (runStart < 0)
                {
                    runStart = i;
                }
            }
            else if (runStart >= 0)
            {
                Accept(runStart, i - 1);
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            Accept(runStart, counts.Length - 1);
        }

        return found;
    }
}

/// <summary>
/// One stretch of collapsed uplink.
/// </summary>
/// <param name="Start">Offset of the last packet before the collapse.</param>
/// <param name="End">Offset the collapse ends at.</param>
/// <param name="StartBucket">First bucket of the collapse, for display.</param>
/// <param name="EndBucket">Last bucket of the collapse, for display.</param>
public readonly record struct HoldSpan(double Start, double End, int StartBucket, int EndBucket);
