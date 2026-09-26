namespace D2Vpn.Core.Network;

/// <summary>
/// Travel direction of a packet, from this machine's point of view.
/// </summary>
/// <remarks>
/// The limiter community writes these as "UL" and "DL", and the AutoHotkey scripts use the same
/// shorthand, so the mapping is spelled out here once: <see cref="Outbound"/> is UL,
/// <see cref="Inbound"/> is DL.
/// </remarks>
public enum PacketDirection
{
    /// <summary>Received from the remote peer. Written "DL" in module names and macros.</summary>
    Inbound,

    /// <summary>Sent to the remote peer. Written "UL" in module names and macros.</summary>
    Outbound
}
