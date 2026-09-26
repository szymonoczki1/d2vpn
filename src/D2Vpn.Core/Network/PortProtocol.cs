namespace D2Vpn.Core.Network;

/// <summary>Transport protocol a <see cref="PortGroup"/> applies to.</summary>
public enum PortProtocol
{
    /// <summary>UDP. Used by the peer traffic on 3074 and the 27k range.</summary>
    Udp,

    /// <summary>TCP. Used by the service channel on 7500 and instance control on 30k.</summary>
    Tcp
}
