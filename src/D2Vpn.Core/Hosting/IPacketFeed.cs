using D2Vpn.Core.Interception;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Hosting;

/// <summary>
/// A runner that reports what its handle saw, for something that only watches.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not part of <see cref="IModuleRunner"/>. Most runners have nothing to report -
/// game pause opens no handle at all - and a watcher wants one named group rather than whatever
/// happens to be hosted. A separate interface lets a front end ask for the feed it needs and get
/// nothing when there is none, instead of every runner carrying an event it never raises.
/// </para>
/// <para>
/// This exists because the instance timer cannot have a handle of its own. A filter is built from
/// the port group alone, so a second handle on 30k would sit behind the first by priority and see
/// only what it chose to reinject - which, during a hold, is nothing. The timer would go blind for
/// exactly as long as the hold lasted and then declare the instance over, on a tool whose whole
/// purpose is holding 30k. Reporting from inside the handle that is already open is the only
/// vantage point from which a withheld packet is visible.
/// </para>
/// </remarks>
public interface IPacketFeed
{
    /// <summary>The port group this feed carries.</summary>
    PortGroup Group { get; }

    /// <summary>
    /// Raised for every packet on the group, before any module has had a verdict on it.
    /// </summary>
    /// <remarks>
    /// Raised on the receive loop, so a handler must be quick and must not touch a UI directly.
    /// </remarks>
    event Action<PacketInfo>? PacketObserved;

    /// <summary>
    /// Raised after this tool injected resets, with how many connections and packets.
    /// </summary>
    /// <remarks>
    /// A watcher needs this to tell its own reconnect apart from the game leaving. The reset kills
    /// the connection and the game opens a new one within a third of a second, which by every
    /// other measure is indistinguishable from arriving somewhere new.
    /// </remarks>
    event Action<int, int>? ResetsSent;
}
