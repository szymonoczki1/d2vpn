using System.Net;

using D2Vpn.Core.Interception;

namespace D2Vpn.Core.Network;

/// <summary>Why the instance timer restarted.</summary>
public enum InstanceChangeReason
{
    /// <summary>The first instance seen since the watcher started.</summary>
    First,

    /// <summary>A connection opened to a server this session had not been talking to.</summary>
    NewServer,

    /// <summary>
    /// Traffic came back after the group had gone quiet, so an instance is running again.
    /// </summary>
    /// <remarks>
    /// Without this, ending on silence could not be undone by anything but a fresh connection:
    /// traffic resuming on a connection that was never re-opened would leave the timer stopped for
    /// good. It only applies while not in an instance, so it cannot fire during one.
    /// </remarks>
    ResumedAfterQuiet
}

/// <summary>Why the instance ended.</summary>
public enum InstanceEndReason
{
    /// <summary>
    /// The server closed the connection gracefully, which is what leaving an activity looks like.
    /// </summary>
    /// <remarks>
    /// Measured: leaving an activity produced inbound <c>[FIN, ACK]</c> from the instance server on
    /// port 30000. A reset does not - it produces RST, which is what this tool's own reconnect
    /// injects, and is the reason the two can be told apart at all.
    /// </remarks>
    ServerClosed,

    /// <summary>
    /// The group went silent for long enough that the connection cannot still be live.
    /// </summary>
    /// <remarks>
    /// The backstop for an ending that is not polite about it. An error code drops the connection
    /// without the server saying goodbye, so nothing closes gracefully and the timer would
    /// otherwise count on for ever.
    ///
    /// The threshold has measurement behind it rather than being a guess: 30k carries a 73-byte
    /// heartbeat every 3.02 s (finding 50), so half a minute of nothing is ten missed beats.
    /// </remarks>
    WentQuiet
}

/// <summary>What happened when the instance changed.</summary>
/// <param name="At">When it was noticed.</param>
/// <param name="Server">The server now being talked to.</param>
/// <param name="Previous">The server before it, if there was one.</param>
/// <param name="Reason">Why it counted as a change.</param>
public readonly record struct InstanceChange(
    DateTimeOffset At,
    IPAddress Server,
    IPAddress? Previous,
    InstanceChangeReason Reason);

/// <summary>
/// Works out when the game has moved to a new instance, from 30k traffic alone.
/// </summary>
/// <remarks>
/// <para>
/// The signal is a TCP connection opening to a <b>server address this session was not already
/// talking to</b>. It comes from finding 6, where a reconnect reopened to the same server 330 ms
/// later and the real instance change, forty seconds on, was a SYN to a different one. The
/// address is the discriminator, not the SYN.
/// </para>
/// <para>
/// <b>Our own reconnect must not trip it.</b> Injecting resets makes the game reopen immediately,
/// and although finding 6 saw it return to the same server there is no guarantee it always will.
/// So <see cref="Suppress"/> is called whenever this tool causes a reset, and anything inside that
/// window is ignored. Belt and braces: the address check would usually be enough on its own.
/// </para>
/// <para>
/// <b>This is a hypothesis with one observation behind it.</b> Whether every instance change
/// produces it, and whether it ever fires without one, is what <c>d2vpn instance</c> is for. Until
/// that has been run, treat the timer as indicative.
/// </para>
/// </remarks>
public sealed class InstanceWatcher
{
    private readonly Dictionary<IPAddress, DateTimeOffset> _lastSeen = [];

    /// <summary>
    /// Local ports with a live connection to each server, and when each was last heard from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The instance ends when the <b>last</b> connection to its server closes, not the first. It is
    /// not established that the game ever holds several at once - an earlier reading of two
    /// captures said so and was wrong, they were separate events four hundred seconds apart - so
    /// this is a guard rather than a response to observed behaviour. It costs nothing if there is
    /// only ever one connection, and makes the question not need an answer.
    /// </para>
    /// <para>
    /// Timestamped because a connection can stop existing without saying so. A reset leaves no FIN
    /// behind, so without pruning, one reconnect would leave a port in here permanently, the set
    /// would never empty, and every later departure would fall through to the silence backstop
    /// instead of being noticed at once.
    /// </para>
    /// <para>
    /// Populated from any packet rather than only from a SYN, so a connection that was already
    /// open when this started watching is still counted.
    /// </para>
    /// </remarks>
    private readonly Dictionary<IPAddress, Dictionary<int, DateTimeOffset>> _live = [];
    private readonly TimeSpan _forget;
    private readonly TimeSpan _silence;
    private readonly TimeSpan _portIdle;
    private readonly Lock _gate = new();

    private DateTimeOffset _lastTraffic;

    private DateTimeOffset _suppressUntil;
    private IPAddress? _current;
    private DateTimeOffset? _endedAt;

    /// <summary>Creates a watcher.</summary>
    /// <param name="forget">
    /// How long a server may go unheard from before talking to it again counts as new. Long enough
    /// that a brief stall is not a change, short enough that returning to a previous instance is.
    /// </param>
    /// <param name="silence">
    /// How long the group may carry nothing at all before the instance is taken to have ended.
    /// Defaults to 30 s, which is ten of the 3.02 s heartbeats measured in finding 50.
    /// </param>
    /// <param name="portIdle">
    /// How long one connection may carry nothing before it is taken to be gone. Three of the
    /// 3.02 s heartbeats by default, which is what distinguishes a connection that was reset from
    /// one that is merely between packets.
    /// </param>
    public InstanceWatcher(
        TimeSpan? forget = null, TimeSpan? silence = null, TimeSpan? portIdle = null)
    {
        _forget = forget ?? TimeSpan.FromSeconds(20);
        _silence = silence ?? TimeSpan.FromSeconds(30);
        _portIdle = portIdle ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>When the current instance started, or null if nothing has been seen yet.</summary>
    public DateTimeOffset? Since { get; private set; }

    /// <summary>The server currently being talked to.</summary>
    public IPAddress? Server
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>How many changes have been seen.</summary>
    public int Changes { get; private set; }

    /// <summary>How many were ignored because a reset had just been injected.</summary>
    public int Suppressed { get; private set; }

    /// <summary>How many instances ended, by either route.</summary>
    public int Ends { get; private set; }

    /// <summary>How many of those ended because the group went quiet rather than closed.</summary>
    public int QuietEnds { get; private set; }

    /// <summary>How long the group has carried nothing, for a front end to show.</summary>
    public TimeSpan Quiet(DateTimeOffset now) =>
        _lastTraffic == default ? TimeSpan.Zero : now - _lastTraffic;

    /// <summary>
    /// True while the game is in an instance, as far as the traffic shows.
    /// </summary>
    /// <remarks>
    /// False from the moment the instance server closes its connection until a new one opens. The
    /// timer stops rather than carrying on counting through orbit, which is the difference between
    /// "how long in here" and "how long since the last load screen".
    /// </remarks>
    public bool InInstance => Since is not null;

    /// <summary>How long the last instance lasted, or zero if none has ended.</summary>
    public TimeSpan LastDuration { get; private set; }

    /// <summary>Raised when the instance changes.</summary>
    public event Action<InstanceChange>? Changed;

    /// <summary>Raised when the instance ends, with how long it lasted and why.</summary>
    public event Action<TimeSpan, InstanceEndReason>? Ended;

    /// <summary>How long the current instance has lasted.</summary>
    public TimeSpan Elapsed(DateTimeOffset now) =>
        Since is { } since ? now - since : TimeSpan.Zero;

    /// <summary>
    /// Ignores changes for a moment, because this tool just caused a reconnect.
    /// </summary>
    /// <remarks>
    /// Measured: the game reopened 330 ms after a reset. Several seconds covers that with room for
    /// a slower machine, and is short enough that a genuine change immediately afterwards is still
    /// caught.
    /// </remarks>
    public void Suppress(DateTimeOffset now, TimeSpan? grace = null)
    {
        lock (_gate)
        {
            _suppressUntil = now + (grace ?? TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Feeds in a packet. Call for every packet on the group, held or not.</summary>
    public void Observe(PacketInfo packet)
    {
        // Only the opening of a connection matters. Ordinary traffic to a server already in the
        // set says nothing about having moved.
        var opening = packet.TcpFlags?.HasFlag(TcpControlFlags.Syn) == true;

        // A graceful close from the instance server is the game leaving - once it is the last one.
        // Deliberately not RST: that is what this tool's own reconnect injects, and treating the
        // two alike would make every reconnect look like leaving the activity. A reset also leaves
        // its connection in the live set on purpose, so resetting every socket does not empty it
        // and read as a close.
        var closing = packet.IsInbound
            && packet.TcpFlags?.HasFlag(TcpControlFlags.Fin) == true
            && packet.TcpFlags?.HasFlag(TcpControlFlags.Reset) != true;

        lock (_gate)
        {
            _lastTraffic = packet.Timestamp;

            if (!_live.TryGetValue(packet.RemoteAddress, out var ports))
            {
                _live[packet.RemoteAddress] = ports = [];
            }

            ports[packet.LocalPort] = packet.Timestamp;
            Prune(ports, packet.Timestamp);

            var known = _lastSeen.TryGetValue(packet.RemoteAddress, out var seen)
                && packet.Timestamp - seen <= _forget;

            _lastSeen[packet.RemoteAddress] = packet.Timestamp;
            Forget(packet.Timestamp);

            if (closing)
            {
                ports.Remove(packet.LocalPort);

                // Only when the last one goes. Closing one socket of several is the game tidying
                // up, not leaving.
                if (ports.Count == 0
                    && _current is not null
                    && packet.RemoteAddress.Equals(_current))
                {
                    End(packet.Timestamp, InstanceEndReason.ServerClosed);
                }

                return;
            }

            // Traffic resuming after the instance ended starts a new one, whether or not it is a
            // connection opening. HarryWatch's whole rule is this and nothing else - any packet
            // after a gap - and without it there is a hole here: end on silence, have traffic come
            // back on a connection that was never re-opened, and the timer would stay stopped with
            // nothing able to restart it.
            //
            // Safe only because it is limited to not being in one. During an instance, a packet to
            // a server already in use says nothing about having moved.
            if (Since is null && _current is null)
            {
                // Resumed only if there was something to resume from. The very first packet of a
                // session has not come back from anywhere.
                Start(
                    packet,
                    _endedAt is null
                        ? InstanceChangeReason.First
                        : InstanceChangeReason.ResumedAfterQuiet);

                return;
            }

            if (!opening || known)
            {
                return;
            }

            if (packet.Timestamp < _suppressUntil)
            {
                Suppressed++;
                return;
            }

            Start(packet, InstanceChangeReason.NewServer);
        }
    }

    /// <summary>
    /// Ends the instance if the group has gone quiet. Call on a timer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Silence cannot be noticed from a packet, there being none, so something has to ask.
    /// </para>
    /// <para>
    /// <b>Only sound where the watcher sees traffic the modules withhold.</b> A WinDivert handle
    /// that drops a packet removes it before lower-priority handles see it, so a watcher sniffing
    /// behind a 30k hold would see the silence that hold is creating and call it an ending. Fed
    /// from the module's own observation point - as <c>ConnectionSampler</c> is, before the module
    /// rules on the packet - that cannot happen.
    /// </para>
    /// </remarks>
    /// <returns>True if this call ended the instance.</returns>
    public bool Tick(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (Since is null || _lastTraffic == default || now - _lastTraffic < _silence)
            {
                return false;
            }

            // Dated from the last packet rather than from now: the instance stopped when the
            // traffic did, not when this happened to notice.
            QuietEnds++;
            End(_lastTraffic, InstanceEndReason.WentQuiet);
            return true;
        }
    }

    /// <summary>Drops connections that have gone quiet for long enough to be gone.</summary>
    private void Prune(Dictionary<int, DateTimeOffset> ports, DateTimeOffset now)
    {
        if (ports.Count < 2)
        {
            return;
        }

        foreach (var (port, seen) in ports.ToArray())
        {
            if (now - seen > _portIdle)
            {
                ports.Remove(port);
            }
        }
    }

    /// <summary>Begins an instance on this packet's server.</summary>
    private void Start(PacketInfo packet, InstanceChangeReason reason)
    {
        var previous = _current;

        _current = packet.RemoteAddress;
        Since = packet.Timestamp;
        _endedAt = null;
        Changes++;

        // A server being talked to again starts with a clean slate; anything left over belongs to
        // the instance that has just been replaced.
        _live.Remove(packet.RemoteAddress);
        _live[packet.RemoteAddress] = new Dictionary<int, DateTimeOffset>
        {
            [packet.LocalPort] = packet.Timestamp
        };

        Raise(new InstanceChange(packet.Timestamp, packet.RemoteAddress, previous, reason));
    }

    private void Raise(InstanceChange change) => Changed?.Invoke(change);

    /// <summary>Stops the timer.</summary>
    private void End(DateTimeOffset at, InstanceEndReason reason)
    {
        if (Since is not { } since)
        {
            return;
        }

        LastDuration = at - since;
        Since = null;
        _endedAt = at;
        _current = null;
        _live.Clear();
        Ends++;

        Ended?.Invoke(LastDuration, reason);
    }

    /// <summary>When the last instance ended, for anything wanting to show how long ago.</summary>
    public DateTimeOffset? EndedAt
    {
        get
        {
            lock (_gate)
            {
                return _endedAt;
            }
        }
    }

    private void Forget(DateTimeOffset now)
    {
        if (_lastSeen.Count < 64)
        {
            return;
        }

        foreach (var (address, seen) in _lastSeen.ToArray())
        {
            if (now - seen > _forget)
            {
                _lastSeen.Remove(address);
            }
        }
    }
}
