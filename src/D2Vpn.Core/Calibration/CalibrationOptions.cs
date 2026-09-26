namespace D2Vpn.Core.Calibration;

/// <summary>
/// Tuning for how a block window is found in a stream of packets.
/// </summary>
/// <remarks>
/// The defaults come from measured behaviour on UDP 3074: a steady 10 Hz uplink tick that fell to
/// roughly 0.6 packets/second while a module held. See <c>docs/protocol.md</c>.
/// </remarks>
public sealed record CalibrationOptions
{
    /// <summary>Width of a counting bucket. One second matches the tick rates involved.</summary>
    public TimeSpan BucketSize { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Buckets at the start of a run that cannot themselves be part of a detected window.
    /// </summary>
    /// <remarks>
    /// Guards against a run that begins before traffic ramps up reporting its own quiet opening
    /// as a hold. This does not affect the rate calculation - see <see cref="SignatureCalibrator"/>.
    /// </remarks>
    public int WarmupBuckets { get; init; } = 5;

    /// <summary>
    /// Minimum ratio of in-hold frequency to normal frequency for a size to count as signature.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Membership is the wrong test. Payload sizes step in small increments because encrypted
    /// payloads pad, so a few hundred baseline packets spread across a hundred-odd distinct sizes
    /// leave most sizes with a count of one or two purely by chance. A size appearing in both
    /// phases therefore means very little on its own.
    /// </para>
    /// <para>
    /// Concentration means a great deal. In a measured run, size 1166 was 33% of in-hold traffic
    /// against 0.36% of normal - 93 times enriched - while 487 was 17% against 1.79%, still 9
    /// times. Anything below this ratio occurs about as often during a hold as it does normally,
    /// which is what a genuine collision looks like.
    /// </para>
    /// </remarks>
    public double MinimumEnrichment { get; init; } = 3.0;

    /// <summary>
    /// Fraction of the baseline rate a candidate window's <em>mean</em> must stay under.
    /// </summary>
    /// <remarks>
    /// Measured collapse was 10/s to 0.6/s, a ratio of 0.06, so 0.25 leaves generous headroom
    /// while staying far below ordinary jitter.
    /// </remarks>
    public double CollapseThreshold { get; init; } = 0.25;

    /// <summary>
    /// Fraction of the baseline rate a <em>single</em> bucket may reach without ending the window.
    /// </summary>
    /// <remarks>
    /// Looser than <see cref="CollapseThreshold"/> on purpose. A block is not silent: partway
    /// through, the game gives up on its unacknowledged peer and emits a handshake, which arrives
    /// as a short burst. In the reference capture that burst put 4 packets into one second against
    /// a baseline of 10. Judging buckets individually at the strict threshold would cut the window
    /// in half and discard most of the signature, so boundaries are found leniently and the window
    /// as a whole is then checked against <see cref="CollapseThreshold"/>.
    /// </remarks>
    public double BucketTolerance { get; init; } = 0.5;

    /// <summary>Consecutive collapsed buckets required before a window is believed.</summary>
    /// <remarks>Guards against a single quiet second being mistaken for a hold.</remarks>
    public int MinimumCollapsedBuckets { get; init; } = 2;

    /// <summary>
    /// Gap in uplink that marks where a hold actually began, inside the opening bucket.
    /// </summary>
    /// <remarks>
    /// Ordinary uplink on 3074 arrives every 50-100 ms at 10-20 packets/second. Once a hold
    /// engages, gaps stretch to whole seconds. Half a second sits far above normal jitter and far
    /// below a real hold's silence, so it separates them without tuning.
    /// </remarks>
    public TimeSpan MinimumHoldGap { get; init; } = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// Percentile of per-bucket uplink counts taken as the normal rate.
    /// </summary>
    /// <remarks>
    /// Upper percentile rather than a median, because traffic on these ports alternates between
    /// quiet and busy rather than holding one pace. A median can land in the quiet mode and set a
    /// collapse threshold below what a real hold produces. See <see cref="SignatureCalibrator"/>.
    /// </remarks>
    public double BaselinePercentile { get; init; } = 0.75;

    /// <summary>The defaults.</summary>
    public static CalibrationOptions Default { get; } = new();
}
