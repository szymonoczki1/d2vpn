using System.Net;

using D2Vpn.Core.Interception;

namespace D2Vpn.Core.Network;

/// <summary>Why the instance timer restarted.</summary>
public enum InstanceChangeReason
{
    /// <summary>The first instance seen since the watcher started.</summary>
    First,

    /// <summary>A connection opened to a server this session had not been talking to.</summary>
    NewServer
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
    private readonly TimeSpan _forget;
    private readonly Lock _gate = new();

    private DateTimeOffset _suppressUntil;
    private IPAddress? _current;

    /// <summary>Creates a watcher.</summary>
    /// <param name="forget">
    /// How long a server may go unheard from before talking to it again counts as new. Long enough
    /// that a brief stall is not a change, short enough that returning to a previous instance is.
    /// </param>
    public InstanceWatcher(TimeSpan? forget = null) =>
        _forget = forget ?? TimeSpan.FromSeconds(20);

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

    /// <summary>Raised when the instance changes.</summary>
    public event Action<InstanceChange>? Changed;

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

        lock (_gate)
        {
            var known = _lastSeen.TryGetValue(packet.RemoteAddress, out var seen)
                && packet.Timestamp - seen <= _forget;

            _lastSeen[packet.RemoteAddress] = packet.Timestamp;
            Forget(packet.Timestamp);

            if (!opening || known)
            {
                return;
            }

            if (packet.Timestamp < _suppressUntil)
            {
                Suppressed++;
                return;
            }

            var reason = _current is null
                ? InstanceChangeReason.First
                : InstanceChangeReason.NewServer;

            var previous = _current;

            _current = packet.RemoteAddress;
            Since = packet.Timestamp;
            Changes++;

            Raise(new InstanceChange(packet.Timestamp, packet.RemoteAddress, previous, reason));
        }
    }

    private void Raise(InstanceChange change) => Changed?.Invoke(change);

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
