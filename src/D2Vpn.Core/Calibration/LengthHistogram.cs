using D2Vpn.Core.Interception;

namespace D2Vpn.Core.Calibration;

/// <summary>
/// Counts how often each payload size was seen, for one direction of one port group.
/// </summary>
/// <remarks>
/// Payload size is the only usable fingerprint for this traffic: it is encrypted, so content
/// tells us nothing, but message <em>kinds</em> have characteristic sizes. See
/// <see cref="PacketInfo.PayloadLength"/> for why the unit is payload and never frame.
/// </remarks>
public sealed class LengthHistogram
{
    private readonly Dictionary<int, int> _counts = [];

    /// <summary>Total packets recorded.</summary>
    public int Total { get; private set; }

    /// <summary>Distinct payload sizes recorded, ascending.</summary>
    public IReadOnlyList<int> DistinctLengths => [.. _counts.Keys.Order()];

    /// <summary>Records one packet of <paramref name="payloadLength"/> bytes.</summary>
    public void Add(int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        _counts[payloadLength] = _counts.GetValueOrDefault(payloadLength) + 1;
        Total++;
    }

    /// <summary>How many packets of exactly <paramref name="payloadLength"/> bytes were seen.</summary>
    public int CountOf(int payloadLength) => _counts.GetValueOrDefault(payloadLength);

    /// <summary>True when a packet of this size was ever seen.</summary>
    public bool Contains(int payloadLength) => _counts.ContainsKey(payloadLength);

    /// <summary>The most frequent sizes first, at most <paramref name="count"/> of them.</summary>
    public IReadOnlyList<LengthCount> MostCommon(int count = 10) =>
    [
        .. _counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                  .Take(count).Select(kv => new LengthCount(kv.Key, kv.Value))
    ];

    /// <summary>
    /// The largest payload size seen, or <see langword="null"/> when nothing was recorded.
    /// </summary>
    /// <remarks>
    /// This is the query behind calibrating a weapon-swap trigger: the user performs the action
    /// and takes the largest packet the action produced.
    /// </remarks>
    public int? Largest => _counts.Count == 0 ? null : _counts.Keys.Max();

    /// <summary>Sizes present here but absent from <paramref name="other"/>, ascending.</summary>
    /// <remarks>
    /// The core calibration operation. Run once while traffic is normal and once while a module is
    /// holding, and the difference is the set of messages the game only emits when its peer has
    /// gone quiet - the packets a module must let through to keep the session alive.
    /// </remarks>
    public IReadOnlyList<int> LengthsNotIn(LengthHistogram other) =>
        [.. _counts.Keys.Where(l => !other.Contains(l)).Order()];
}

/// <summary>A payload size and how often it occurred.</summary>
/// <param name="PayloadLength">Payload size in bytes.</param>
/// <param name="Count">Number of packets seen at that size.</param>
public readonly record struct LengthCount(int PayloadLength, int Count);
