using System.Net;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Modules;

/// <summary>
/// Withholds inbound API traffic on TCP 7500 while active.
/// </summary>
/// <remarks>
/// <para>
/// <b>A TCP block is not a UDP block.</b> Dropping a UDP packet destroys it and nothing follows.
/// Dropping a TCP segment only delays it: the sender gets no acknowledgement and re-sends the same
/// data, again and again, with the same sequence number. A module that simply drops what it does
/// not like leaks at the first retransmission - roughly 200 ms later - and has achieved nothing.
/// </para>
/// <para>
/// So the mechanism here is in two phases, following what the reference implementation does.
/// </para>
/// <list type="number">
/// <item>
/// <b>Capturing.</b> Every inbound segment carrying data is withheld and its sequence number
/// recorded. Bare acknowledgements are left alone - they carry nothing, and stopping them only
/// stalls the conversation.
/// </item>
/// <item>
/// <b>Holding.</b> Once nothing new has been withheld for <see cref="ApiBlockOptions.CaptureQuiet"/>,
/// the sender has run out of window and is only repeating itself. From then on <em>only
/// retransmissions of already-withheld sequence numbers are blocked</em>, and genuinely new data
/// passes. That is what turns a block into a hold: a fixed slice of the conversation is kept back
/// while the connection otherwise carries on.
/// </item>
/// </list>
/// <para>
/// <b>The failure mode is a reset, not a timeout.</b> Held too long, the peer abandons the
/// connection and the game shows an error code. An inbound RST therefore disables the module
/// immediately rather than being treated as ordinary traffic, and two timers disable it before that
/// point is reached. This is the opposite of the 3074 uplink hold, which has no known ceiling.
/// </para>
/// <para>
/// Outbound is never touched. Blocking it would stop the acknowledgements this machine owes, which
/// makes the peer retransmit harder and reset sooner, for no gain that the prior art claims.
/// </para>
/// </remarks>
public sealed class ApiBlockModule : ITrafficModule
{
    private readonly Dictionary<Connection, ConnectionState> _connections = [];
    private readonly Lock _gate = new();
    private bool _isActive;

    /// <inheritdoc/>
    public PortGroup Group { get; }

    /// <inheritdoc/>
    public string Name { get; }

    /// <summary>How the module behaves.</summary>
    public ApiBlockOptions Options { get; }

    /// <summary>Segments withheld since the module was created.</summary>
    public long SegmentsWithheld { get; private set; }

    /// <summary>Retransmissions suppressed since the module was created.</summary>
    /// <remarks>
    /// The diagnostic that says whether this is working at all. A TCP block with zero suppressed
    /// retransmissions blocked once and then leaked, which looks identical in the totals.
    /// </remarks>
    public long RetransmitsSuppressed { get; private set; }

    /// <summary>Connections that reached their capture limit.</summary>
    public long CapacityReached { get; private set; }

    /// <summary>Resets seen from a peer while holding.</summary>
    /// <remarks>
    /// Non-zero means the hold went too far at least once. The module disables itself on the first
    /// one, so the count is how often the ceiling was actually hit rather than avoided.
    /// </remarks>
    public long ResetsSeen { get; private set; }

    /// <summary>Raised when the module turns itself off, with why.</summary>
    public event Action<ApiBlockModule, string>? Disabled;

    /// <inheritdoc/>
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _isActive;
            }
        }
        set
        {
            lock (_gate)
            {
                SetActive(value);
            }
        }
    }

    /// <summary>Creates the module.</summary>
    public ApiBlockModule(string name, PortGroup group, ApiBlockOptions? options = null)
    {
        Name = name;
        Group = group;
        Options = options ?? new ApiBlockOptions();
    }

    /// <summary>Flips the module and reports the new state.</summary>
    public bool Toggle()
    {
        lock (_gate)
        {
            SetActive(!_isActive);
            return _isActive;
        }
    }

    /// <inheritdoc/>
    public PacketVerdict Decide(PacketInfo packet)
    {
        lock (_gate)
        {
            return DecideCore(packet);
        }
    }

    private PacketVerdict DecideCore(PacketInfo packet)
    {
        if (!_isActive || !Group.Contains(packet.RemotePort))
        {
            return PacketVerdict.Pass;
        }

        // Driven from packet timestamps rather than a clock, so a whole hold can be played through
        // in a test with no driver running. Checked on every packet including outbound ones, which
        // matters here: while holding, inbound may fall silent entirely, and outbound is then the
        // only thing still arriving to notice that the ceiling has been reached.
        if (ExpireIfDue(packet.Timestamp) is { } reason)
        {
            SetActive(false);
            Disabled?.Invoke(this, reason);
            return PacketVerdict.Pass;
        }

        if (packet.IsOutbound)
        {
            return PacketVerdict.Pass;
        }

        if (packet.IsReset)
        {
            // The peer has given up. Holding past this achieves nothing and the game is already
            // showing an error code, so stop rather than keep blocking a connection that is gone.
            ResetsSeen++;
            SetActive(false);
            Disabled?.Invoke(this, "the peer reset the connection");
            return PacketVerdict.Pass;
        }

        if (packet.SequenceNumber is not { } sequence)
        {
            // Not TCP. It cannot belong to this module's traffic, so it is none of its business.
            return PacketVerdict.Pass;
        }

        var state = StateFor(packet);

        // The sender has stopped producing new data and is only repeating itself. From here only
        // what is already withheld stays withheld.
        if (!state.Captured
            && state.Withheld.Count > 0
            && packet.Timestamp - state.LastWithheldAt >= Options.CaptureQuiet)
        {
            state.Captured = true;
        }

        if (!state.Captured)
        {
            if (!packet.IsTcpData)
            {
                return PacketVerdict.Pass;
            }

            state.Withheld[sequence] = packet.PayloadLength;
            state.LastWithheldAt = packet.Timestamp;
            SegmentsWithheld++;
            return Block();
        }

        if (!state.Withheld.ContainsKey(sequence))
        {
            // New data, arriving after the slice was captured. Letting it through is what keeps
            // this a hold on a fixed slice rather than a block on the whole connection.
            return PacketVerdict.Pass;
        }

        state.LastRetransmitAt = packet.Timestamp;
        RetransmitsSuppressed++;

        if (state.CapacityAt is null && state.Span >= Options.CaptureBytes)
        {
            state.CapacityAt = packet.Timestamp;
            CapacityReached++;
        }

        return Block();
    }

    /// <summary>
    /// What to do with a segment being withheld.
    /// </summary>
    /// <remarks>
    /// Buffering is offered but off by default, and the reasoning is not the same as on 3074. There
    /// a held packet is the only copy, so keeping it is the difference between an action arriving
    /// late and never happening. Here the sender is still holding its own copy and retransmitting
    /// it, so what is withheld is not lost - and replaying a TCP segment verbatim sends a stale
    /// acknowledgement number, which the reference implementation rewrites before it replays.
    /// Until that rewriting exists here, buffering on this port is unproven rather than useful.
    /// </remarks>
    private PacketVerdict Block() =>
        Options.Buffering ? PacketVerdict.Buffer : PacketVerdict.Drop;

    /// <summary>
    /// Whether the hold has run past a limit, and which one.
    /// </summary>
    /// <remarks>
    /// Two limits rather than one, because there are two ways a hold outlives its usefulness. It
    /// can simply run too long, or the peer can stop retransmitting - having given up on the data
    /// rather than on the connection - after which holding gains nothing and only risks the reset.
    /// </remarks>
    private string? ExpireIfDue(DateTimeOffset now)
    {
        foreach (var state in _connections.Values)
        {
            if (state.CapacityAt is not { } capacity)
            {
                continue;
            }

            var held = now - capacity;

            if (held >= Options.MaxHold)
            {
                return $"held {held.TotalSeconds:F0}s, past the {Options.MaxHold.TotalSeconds:F0}s limit";
            }

            if (held >= Options.QuietHold && now - state.LastRetransmitAt >= Options.QuietRetransmit)
            {
                return $"held {held.TotalSeconds:F0}s with no retransmission for "
                    + $"{(now - state.LastRetransmitAt).TotalSeconds:F0}s";
            }
        }

        return null;
    }

    private ConnectionState StateFor(PacketInfo packet)
    {
        var key = new Connection(packet.RemoteAddress, packet.LocalPort);

        if (!_connections.TryGetValue(key, out var state))
        {
            state = new ConnectionState();
            _connections[key] = state;
        }

        return state;
    }

    /// <summary>Sets the active flag, clearing per-connection state on any change.</summary>
    /// <remarks>
    /// State must not survive a toggle. A sequence number withheld during one hold means nothing
    /// during the next: the connection has moved on, and matching against it would suppress
    /// whatever happens to land on the same number.
    /// </remarks>
    private void SetActive(bool active)
    {
        if (active == _isActive)
        {
            return;
        }

        _isActive = active;
        _connections.Clear();
    }

    /// <inheritdoc/>
    /// <remarks>Diagnostic counters are deliberately left alone; they describe a run, not state.</remarks>
    public void Reset()
    {
        lock (_gate)
        {
            _isActive = false;
            _connections.Clear();
        }
    }

    /// <summary>Clears the diagnostic counters.</summary>
    public void ResetStatistics()
    {
        lock (_gate)
        {
            SegmentsWithheld = 0;
            RetransmitsSuppressed = 0;
            CapacityReached = 0;
            ResetsSeen = 0;
        }
    }

    /// <summary>One TCP connection, which is a peer and the local port talking to it.</summary>
    private readonly record struct Connection(IPAddress Address, int LocalPort);

    /// <summary>What has been withheld from one connection.</summary>
    private sealed class ConnectionState
    {
        /// <summary>Sequence number to payload length, for everything withheld.</summary>
        public Dictionary<uint, int> Withheld { get; } = [];

        /// <summary>When something was last withheld for the first time.</summary>
        public DateTimeOffset LastWithheldAt { get; set; }

        /// <summary>When a retransmission was last suppressed.</summary>
        public DateTimeOffset LastRetransmitAt { get; set; }

        /// <summary>True once the sender stopped producing new data.</summary>
        public bool Captured { get; set; }

        /// <summary>When the withheld slice first covered the capture limit.</summary>
        public DateTimeOffset? CapacityAt { get; set; }

        /// <summary>
        /// How many bytes the withheld slice covers, from its lowest sequence number to its
        /// highest end.
        /// </summary>
        /// <remarks>
        /// Measured as a span rather than as a sum of payloads, so a retransmission already counted
        /// does not count again and an overlapping one is not double-counted.
        /// </remarks>
        public long Span
        {
            get
            {
                if (Withheld.Count == 0)
                {
                    return 0;
                }

                var lowest = uint.MaxValue;
                var highest = 0u;

                foreach (var (sequence, length) in Withheld)
                {
                    if (sequence < lowest)
                    {
                        lowest = sequence;
                    }

                    var end = sequence + (uint)length;
                    if (end > highest)
                    {
                        highest = end;
                    }
                }

                return highest - lowest;
            }
        }
    }
}
