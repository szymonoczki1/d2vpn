using System.Runtime.Versioning;

using D2Vpn.Core.Modules;

namespace D2Vpn.Core.Interception;

/// <summary>
/// One module sharing a packet handle with others, with its own buffer and its own release.
/// </summary>
/// <remarks>
/// <para>
/// A WinDivert filter is built from a port group alone - protocol and port range, never direction -
/// so every module acting on one group wants the same handle. Four toggles on 3074 (uplink,
/// downlink, and a slow variant of each) cannot be four handles: the second would only see what the
/// first reinjected. They have to be four slots behind one.
/// </para>
/// <para>
/// <b>Each slot owns its buffer.</b> That is the whole reason this type exists rather than a list
/// of modules. Releasing an uplink hold has to replay the outbound packets it withheld and leave
/// the inbound ones a downlink hold is still holding, and a single buffer per handle cannot tell
/// them apart. One buffer per slot makes the question not arise.
/// </para>
/// <para>
/// Slots are consulted in declared order and the <b>first non-passing verdict wins</b>, so the slot
/// that withheld a packet is also the one whose buffer keeps it. Order therefore matters where two
/// slots cover the same direction: a block is declared before its slow variant, so turning both on
/// blocks rather than delays. Turning both on is contradictory anyway - this only fixes which way
/// it resolves.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ModuleSlot : IDisposable
{
    private bool _wasHolding;

    /// <summary>Creates a slot.</summary>
    /// <param name="module">The module deciding packets for this slot.</param>
    /// <param name="buffer">
    /// Where this slot's withheld packets are kept, or <see langword="null"/> to destroy them.
    /// A buffer may be supplied even when the module is not currently buffering: the module's
    /// verdict decides whether it is used, which is what lets buffering be toggled while running.
    /// </param>
    public ModuleSlot(ITrafficModule module, PacketBuffer? buffer = null)
    {
        Module = module;
        Buffer = buffer;
    }

    /// <summary>The module deciding packets for this slot.</summary>
    public ITrafficModule Module { get; }

    /// <summary>This slot's buffer, if it has one.</summary>
    public PacketBuffer? Buffer { get; }

    /// <summary>The name the control surface addresses this slot by.</summary>
    public string Name => Module.Name;

    /// <summary>Packets this slot withheld.</summary>
    public long Dropped { get; private set; }

    /// <summary>Packets this slot withheld and kept.</summary>
    public long Buffered { get; private set; }

    /// <summary>Packets replayed out of this slot's buffer.</summary>
    public long Replayed { get; private set; }

    /// <summary>Records that this slot withheld a packet.</summary>
    public void CountDropped() => Dropped++;

    /// <summary>Records that this slot kept a packet.</summary>
    public void CountBuffered() => Buffered++;

    /// <summary>Records replayed packets.</summary>
    public void CountReplayed(int count) => Replayed += count;

    // ---- TEMPORARY: drain cost, for measuring the release stall --------------------------
    //
    // A release replays the whole buffer in one pass on the receive thread, so a large backlog
    // both takes time and stops live traffic being read while it runs. Measured in game as
    // instant under ~100 packets and noticeable above ~150. These two exist to put a number on
    // that before it is fixed by draining in chunks, and should come out once it is.

    /// <summary>How many packets the last release replayed.</summary>
    public int LastDrainCount { get; private set; }

    /// <summary>How long the last release took to replay them.</summary>
    public TimeSpan LastDrainDuration { get; private set; }

    /// <summary>The slowest release this slot has had.</summary>
    public TimeSpan SlowestDrain { get; private set; }

    /// <summary>Records what a release cost.</summary>
    public void CountDrain(int sent, TimeSpan took)
    {
        if (sent == 0)
        {
            return;
        }

        LastDrainCount = sent;
        LastDrainDuration = took;

        if (took > SlowestDrain)
        {
            SlowestDrain = took;
        }
    }

    // ---- end TEMPORARY ------------------------------------------------------------------

    /// <summary>
    /// True when this slot has just been turned off and owes its held packets a replay.
    /// </summary>
    /// <remarks>
    /// Edge-triggered, and per slot rather than per handle. With one flag for the whole handle,
    /// releasing any module would have drained every module's backlog.
    /// </remarks>
    public bool TakeReleaseEdge()
    {
        var holding = Module.IsActive;
        var released = _wasHolding && !holding;
        _wasHolding = holding;
        return released;
    }

    /// <inheritdoc/>
    public void Dispose() => Buffer?.Dispose();
}
