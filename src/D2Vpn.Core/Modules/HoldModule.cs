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
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;

            // Releasing must restore normal traffic immediately and leave nothing behind, so that
            // toggling off is always safe even from a half-finished resync.
            Reset();
        }
    }

    /// <summary>Peers this module is currently tracking, for diagnostics and the UI.</summary>
    public IReadOnlyDictionary<string, HoldState> PeerStates =>
        _peers.ToDictionary(p => p.Key, p => p.Value.State);

    /// <inheritdoc/>
    public PacketVerdict Decide(PacketInfo packet)
    {
        if (!_isActive || !Group.Contains(packet.RemotePort))
        {
            return PacketVerdict.Pass;
        }

        if (HasOutlivedSafetyTimeout(packet.Timestamp))
        {
            // Disable rather than merely pass: the operator should not have to notice that a
            // module quietly stopped working, and leaving it "on but inert" invites exactly that.
            IsActive = false;
            SafetyTimeoutElapsed?.Invoke(this);
            return PacketVerdict.Pass;
        }

        if (!HoldsDirection(packet.Direction))
        {
            return PacketVerdict.Pass;
        }

        var peer = PeerFor(packet.RemoteAddress);
        return peer.Decide(packet, Options);
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _peers.Clear();
        _activatedAt = null;
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

        public HoldState State { get; private set; } = HoldState.Blocking;

        public PacketVerdict Decide(PacketInfo packet, HoldModuleOptions options)
        {
            if (State == HoldState.Resyncing)
            {
                if (packet.Timestamp < _resyncEndsAt)
                {
                    return PacketVerdict.Pass;
                }

                State = HoldState.Blocking;
            }

            // A reconnect request both passes and starts a resync. It has to pass for the peer to
            // receive it at all, and the resync is what lets the reply and the exchange that
            // follows get through - without which the peer simply asks again, and keeps asking.
            if (options.ResyncTriggerLengths.Contains(packet.PayloadLength))
            {
                State = HoldState.Resyncing;
                _resyncEndsAt = packet.Timestamp + options.ResyncDuration;
                return PacketVerdict.Pass;
            }

            return options.PassLengths.Contains(packet.PayloadLength)
                ? PacketVerdict.Pass
                : PacketVerdict.Drop;
        }
    }
}
