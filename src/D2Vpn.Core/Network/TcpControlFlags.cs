namespace D2Vpn.Core.Network;

/// <summary>
/// The TCP control bits, as far as a module here needs them.
/// </summary>
/// <remarks>
/// Only the flags this project acts on are named. <see cref="Push"/> marks a segment carrying data
/// the peer wants delivered, which is the thing worth blocking; <see cref="Reset"/> is the peer
/// abandoning the connection, which on this game surfaces as an error code and so has to force a
/// module off rather than be treated as ordinary traffic.
/// </remarks>
[Flags]
public enum TcpControlFlags
{
    /// <summary>No flags set.</summary>
    None = 0,

    /// <summary>FIN - the sender has finished sending.</summary>
    Fin = 1 << 0,

    /// <summary>SYN - connection setup.</summary>
    Syn = 1 << 1,

    /// <summary>RST - the connection is being torn down abruptly.</summary>
    Reset = 1 << 2,

    /// <summary>PSH - deliver this segment's data to the application now.</summary>
    Push = 1 << 3,

    /// <summary>ACK - the acknowledgement number is meaningful.</summary>
    Ack = 1 << 4,

    /// <summary>URG - the urgent pointer is meaningful.</summary>
    Urgent = 1 << 5,
}
