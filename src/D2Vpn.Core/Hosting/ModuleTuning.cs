namespace D2Vpn.Core.Hosting;

/// <summary>
/// Per-module numbers a config file may override, on top of the measured defaults.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is <see langword="null"/>-by-default and means "leave the measured value alone".
/// That is deliberate: the defaults in <c>MeasuredDefaults</c> and <c>ApiBlockOptions</c> came out
/// of calibration runs recorded in <c>docs/protocol.md</c>, and a config that restated them would
/// quietly fork from the measurements the next time one was corrected.
/// </para>
/// <para>
/// So a config carries only what the operator has deliberately changed, and anything it omits
/// tracks the measurement.
/// </para>
/// </remarks>
public sealed record ModuleTuning
{
    /// <summary>
    /// Whether the module participates at all when the session starts.
    /// </summary>
    /// <remarks>
    /// Restored like any other setting, so a module turned off stays off next launch. What is not
    /// restored is whether it was <em>holding</em> - coming back to a session that is already
    /// withholding traffic, with no keypress to explain it, is not something anyone wants.
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether the resync window, or the TCP keepalive, starts enabled.</summary>
    public bool Resync { get; init; } = true;

    /// <summary>Whether withheld packets start being kept for replay.</summary>
    public bool Buffer { get; init; } = true;

    /// <summary>
    /// The delay depth for a slow module, in milliseconds.
    /// </summary>
    /// <remarks>
    /// On a UDP hold this is the rate cap - one packet per peer per interval. On TCP it is how long
    /// each segment waits before going out; 30k slow was measured better at 8 s than the reference's
    /// stated 6 s, which is exactly the kind of thing this exists to record.
    /// </remarks>
    public int? SlowMs { get; init; }

    /// <summary>How long a peer must stay blocked before another resync may open, in seconds.</summary>
    public int? ResyncCooldownS { get; init; }

    /// <summary>How many packets the buffer may hold.</summary>
    public int? BufferPackets { get; init; }

    /// <summary>
    /// How long a held packet may wait before it is discarded rather than sent stale, in seconds.
    /// </summary>
    /// <remarks>
    /// Ignored for TCP, where an ordered stream wants no age limit at all: a segment dropped for
    /// being stale blocks everything behind it for good.
    /// </remarks>
    public int? BufferAgeS { get; init; }

    /// <summary>The measured defaults, unchanged.</summary>
    public static ModuleTuning Default { get; } = new();
}
