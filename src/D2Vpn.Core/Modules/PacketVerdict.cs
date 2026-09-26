namespace D2Vpn.Core.Modules;

/// <summary>What should happen to a packet.</summary>
public enum PacketVerdict
{
    /// <summary>Send it on. In divert mode this means reinjecting it into the stack.</summary>
    Pass,

    /// <summary>Do not send it. In divert mode a packet that is not reinjected is gone.</summary>
    Drop
}
