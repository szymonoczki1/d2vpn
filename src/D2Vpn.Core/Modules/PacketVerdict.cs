namespace D2Vpn.Core.Modules;

/// <summary>What should happen to a packet.</summary>
public enum PacketVerdict
{
    /// <summary>Send it on. In divert mode this means reinjecting it into the stack.</summary>
    Pass,

    /// <summary>Do not send it. In divert mode a packet that is not reinjected is gone.</summary>
    Drop,

    /// <summary>
    /// Hold it and send it when the hold is released.
    /// </summary>
    /// <remarks>
    /// The difference between deferring an action and destroying it. Nothing re-sends what the
    /// player did, so a dropped uplink packet is gone - whereas a held one arrives late, after
    /// the desync, which is what the trick depends on. Measured: the reference limiter replays
    /// roughly as many packets as it withheld, in millisecond-spaced bursts.
    /// </remarks>
    Buffer
}
