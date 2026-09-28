using System.Net;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Modules;

/// <summary>
/// Holds traffic on a port group in one direction while leaving the session alive.
/// </summary>
/// <remarks>
/// <para>
/// This is not a switch that stays off, and that distinction is the whole design. Measured on four
/// peers simultaneously, a 20-second hold ran <em>block, resync, block</em>: the limiter stopped
/// blocking partway through so the game could re-establish with its peer, then resumed. With that
/// behaviour disabled, the same hold produced a retry storm - one reconnect request repeated at
/// 3-4/s for its whole length - and the session died at about 30 seconds. With it, holds have run
/// past 20 minutes.
/// </para>
/// <para>
/// So a module does two things. It passes the handshake sizes that keep a peer from giving up, and
/// it recognises a reconnect request and stops blocking until the exchange has had time to finish.
/// A pass-list on its own buys 30 seconds; the difference is the resync.
/// </para>
/// <para>
/// State is per remote address, because the game talks to several peers at once on its own tick
/// and they join and leave mid-session. Time comes from packet timestamps rather than a clock, so
/// an entire hold can be driven through in a unit test with no driver and no game.
/// </para>
/// <para>Evidence for all of the above is in <c>docs/protocol.md</c>, findings 10 through 14b.</para>
/// </remarks>
public sealed class HoldModule : ITrafficModule
{
    // Toggling comes from a hotkey thread while the packet loop reads per-peer state, so every
    // path that touches that state takes this. Decide runs per packet - a few tens per second -
    // so an uncontended lock costs nothing worth measuring.
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PeerHold> _peers = [];
    private bool _isActive;
    private DateTimeOffset? _activatedAt;

    /// <inheritdoc/>
    public PortGroup Group { get; }

    /// <inheritdoc/>
    public string Name { get; }

    /// <summary>The settings this module runs with.</summary>
    public HoldModuleOptions Options { get; }

    /// <summary>
    /// Raised when the module disables itself because <see cref="HoldModuleOptions.SafetyTimeout"/>
    /// elapsed.
    /// </summary>
    public event Action<HoldModule>? SafetyTimeoutElapsed;

    /// <summary>Creates a module for <paramref name="group"/>.</summary>
    public HoldModule(string name, PortGroup group, HoldModuleOptions options)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Module name must not be blank.", nameof(name))
            : name;

        Group = group.Validate();
        Options = options;
    }

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
                if (_isActive == value)
                {
                    return;
                }

                _isActive = value;

                // Releasing must restore normal traffic immediately and leave nothing behind, so
                // that toggling off is always safe even from a half-finished resync.
                ResetCore();
            }
        }
    }

    /// <summary>Flips the hold and reports the new state, for a hotkey to act on.</summary>
    public bool Toggle()
    {
        lock (_gate)
        {
            IsActiveCore = !_isActive;
            return _isActive;
        }
    }

    private bool IsActiveCore
    {
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            ResetCore();
        }
    }

    /// <summary>
    /// Trigger-sized packets seen since the last <see cref="Reset"/>.
    /// </summary>
    /// <remarks>
    /// The single most useful number when a hold behaves unexpectedly. Zero means the game never
    /// asked to reconnect, or asked in a size the trigger list does not contain - either way the
    /// resync cannot have run, and a hold is only as good as its pass list.
    /// </remarks>
    public long TriggersSeen { get; private set; }

    /// <summary>Resync windows started since the last <see cref="Reset"/>.</summary>
    public long ResyncsStarted { get; private set; }

    /// <summary>Peers this module is currently tracking, for diagnostics and the UI.</summary>
    public IReadOnlyDictionary<string, HoldState> PeerStates
    {
        get
        {
            lock (_gate)
            {
                return _peers.ToDictionary(p => p.Key, p => p.Value.State);
            }
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

        if (HasOutlivedSafetyTimeout(packet.Timestamp))
        {
            // Disable rather than merely pass: the operator should not have to notice that a
            // module quietly stopped working, and leaving it "on but inert" invites exactly that.
            IsActiveCore = false;
            SafetyTimeoutElapsed?.Invoke(this);
            return PacketVerdict.Pass;
        }

        var peer = PeerFor(packet.RemoteAddress);

        // The trigger is watched before the direction gate, because it does not necessarily travel
        // the way the hold does. Whichever direction is held, it is this machine that notices a
        // peer has gone quiet and sends the reconnect request - so during a downlink hold the
        // request is outbound, in traffic the module is not blocking. Gating first would mean
        // passing it unexamined and never resyncing at all.
        if (packet.Direction == Options.EffectiveTriggerDirection
            && Options.ResyncTriggerLengths.Contains(packet.PayloadLength))
        {
            TriggersSeen++;
            if (peer.TryBeginResync(packet.Timestamp, Options))
            {
                ResyncsStarted++;
            }
        }

        if (!HoldsDirection(packet.Direction))
        {
            return PacketVerdict.Pass;
        }

        return peer.Decide(packet, Options);
    }

    /// <inheritdoc/>
    /// <summary>
    /// Forgets per-peer state. Diagnostic counters are deliberately left alone.
    /// </summary>
    /// <remarks>
    /// Clearing the counters here is what made a working run look broken. Reset runs on disposal,
    /// and disposal happens inside the controller's own shutdown - so anything reading the counters
    /// after the run finished got zeroes. A hold that had resynced correctly reported "reconnect
    /// requests seen: 0", and only a packet capture showed the requests had been there all along.
    ///
    /// The counters describe a run, not live state, so they outlive it. Use
    /// <see cref="ResetStatistics"/> to clear them deliberately.
    /// </remarks>
    public void Reset()
    {
        lock (_gate)
        {
            ResetCore();
        }
    }

    private void ResetCore()
    {
        _peers.Clear();
        _activatedAt = null;
    }

    /// <summary>Clears the diagnostic counters.</summary>
    public void ResetStatistics()
    {
        lock (_gate)
        {
            TriggersSeen = 0;
            ResyncsStarted = 0;
        }
    }

    /// <summary>
    /// Whether this module acts on a packet travelling the given way.
    /// </summary>
    /// <remarks>
    /// Traffic the module does not hold passes untouched rather than falling through to the
    /// filter. An earlier boolean got this wrong: turning off "inbound always passes" did not
    /// produce a downlink hold, it dropped inbound packets against an uplink pass list, which
    /// blocks both directions using the wrong signature for one of them.
    /// </remarks>
    private bool HoldsDirection(PacketDirection direction) => Options.Direction switch
    {
        HoldDirection.Uplink => direction == PacketDirection.Outbound,
        HoldDirection.Downlink => direction == PacketDirection.Inbound,
        HoldDirection.Both => true,
        _ => false
    };

    private bool HasOutlivedSafetyTimeout(DateTimeOffset now)
    {
        if (Options.SafetyTimeout is not { } limit)
        {
            return false;
        }

        // The clock starts at the first packet after activation rather than at the keypress, so a
        // module that is enabled while nothing is flowing does not burn its budget waiting.
        _activatedAt ??= now;
        return now - _activatedAt.Value >= limit;
    }

    private PeerHold PeerFor(IPAddress address)
    {
        var key = address.ToString();
        if (!_peers.TryGetValue(key, out var peer))
        {
            peer = _peers[key] = new PeerHold();
        }

        return peer;
    }

    /// <summary>Hold state for one remote address.</summary>
    private sealed class PeerHold
    {
        private DateTimeOffset _resyncEndsAt;
        private DateTimeOffset _blockingSince = DateTimeOffset.MinValue;

        public HoldState State { get; private set; } = HoldState.Blocking;

        /// <summary>
        /// Opens a resync window, unless this peer has not been blocked long enough yet.
        /// </summary>
        /// <returns><see langword="true"/> when a new window actually opened.</returns>
        /// <remarks>
        /// <para>
        /// A window is a fixed length from the request that opened it, and requests arriving
        /// inside it are counted but change nothing. Extending on each request looks reasonable -
        /// the game is still asking, so the exchange has not finished - and is a trap: requests
        /// arrive every couple of seconds while a hold is on, which is faster than the window
        /// lasts, so an extendable window never closes and the module never blocks again.
        /// </para>
        /// <para>
        /// A request during the cooldown is also ignored. Together these give a bounded cycle:
        /// open for the window, then blocked for at least the cooldown.
        /// </para>
        /// </remarks>
        public bool TryBeginResync(DateTimeOffset at, HoldModuleOptions options)
        {
            if (State == HoldState.Resyncing)
            {
                return false;
            }

            if (_blockingSince != DateTimeOffset.MinValue
                && at - _blockingSince < options.ResyncCooldown)
            {
                return false;
            }

            State = HoldState.Resyncing;
            _resyncEndsAt = at + options.ResyncDuration;
            return true;
        }

        public PacketVerdict Decide(PacketInfo packet, HoldModuleOptions options)
        {
            if (State == HoldState.Resyncing)
            {
                if (packet.Timestamp < _resyncEndsAt)
                {
                    // A resync that stops blocking entirely defeats the hold: one packet is enough
                    // to carry a pickup or a buff. Where the handshake sizes are known, only they
                    // cross and the block stays total for everything else.
                    //
                    // The standing pass list still applies. It was briefly overridden here, which
                    // meant a reconnect request opened a window and was then dropped by it - the
                    // request never reaching the peer it was meant to reach.
                    if (options.ResyncPassLengths is not { } allowed)
                    {
                        return PacketVerdict.Pass;
                    }

                    // The threshold is here because an exact-size list did not replicate across
                    // two 27k calibrations: they shared one size out of fifteen. What did hold is
                    // that every size passed is either small or one of two large ones, so the
                    // small end is expressed as a bound rather than enumerated.
                    return allowed.Contains(packet.PayloadLength)
                        || packet.PayloadLength <= options.ResyncPassMaxLength
                        || options.PassLengths.Contains(packet.PayloadLength)
                            ? PacketVerdict.Pass
                            : Blocked(packet, options);
                }

                State = HoldState.Blocking;
                _blockingSince = packet.Timestamp;
            }

            return options.PassLengths.Contains(packet.PayloadLength)
                ? PacketVerdict.Pass
                : Blocked(packet, options);
        }

        /// <summary>
        /// What to do with a packet the hold is blocking: hold it for replay, or destroy it.
        /// </summary>
        /// <remarks>
        /// Decided per packet rather than per hold, because the asymmetry is between directions
        /// and not between modules. A hold covering both directions still buffers only what it
        /// sends, which is the only case with a result behind it.
        /// </remarks>
        private static PacketVerdict Blocked(PacketInfo packet, HoldModuleOptions options) =>
            // Slow implies buffering: a packet cannot be delivered late unless it was kept.
            // It is not restricted to outbound the way plain buffering is, because the reason for
            // that restriction - the server re-sends inbound state anyway - does not apply to a
            // delay. Delaying inbound is the whole of what the reference limiter's 30k variant
            // does. Scope still comes from the direction gate above, so an uplink hold delays
            // only uplink.
            (options.SlowMode || options.Buffering)
                && (packet.IsOutbound || options.BufferInbound || options.SlowMode)
                    ? PacketVerdict.Buffer
                    : PacketVerdict.Drop;
    }
}
