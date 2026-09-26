namespace D2Vpn.Core.Modules;

/// <summary>
/// Which direction of traffic a module holds.
/// </summary>
/// <remarks>
/// <para>
/// This replaced a boolean named <c>InboundAlwaysPasses</c>, which read like a direction switch
/// and was not one: setting it false did not produce a downlink hold, it blocked <em>both</em>
/// directions, because inbound packets simply fell through to the same filter. Naming the three
/// cases makes the choice explicit and makes <see cref="Both"/> reachable on purpose rather than
/// by accident.
/// </para>
/// <para>
/// Uplink and downlink need <em>different</em> signatures. The pass list for an uplink hold is the
/// set of packets this machine must still be able to send; for a downlink hold it is what the peer
/// must still be able to send to us. They are not the same packets, and one cannot be derived from
/// the other - each has to be calibrated in its own direction.
/// </para>
/// </remarks>
public enum HoldDirection
{
    /// <summary>Hold what this machine sends. Written "UL" in module names and macros.</summary>
    Uplink,

    /// <summary>Hold what the peer sends. Written "DL".</summary>
    Downlink,

    /// <summary>Hold both directions. The "full" variant of a module.</summary>
    Both
}
