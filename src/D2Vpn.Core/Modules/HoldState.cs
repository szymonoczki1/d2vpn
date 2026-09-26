namespace D2Vpn.Core.Modules;

/// <summary>
/// What a module is doing to one peer.
/// </summary>
/// <remarks>
/// Measured rather than designed: a real limiter cycles between <see cref="Blocking"/> and
/// <see cref="Resyncing"/> for the whole time a key is held. See <c>docs/protocol.md</c>,
/// findings 10 and 13.
/// </remarks>
public enum HoldState
{
    /// <summary>Not holding this peer. Everything passes.</summary>
    Idle,

    /// <summary>Holding. Uplink is dropped except the signature sizes.</summary>
    Blocking,

    /// <summary>
    /// The peer asked to reconnect and the exchange is being allowed to complete. Everything
    /// passes until it finishes, then blocking resumes.
    /// </summary>
    Resyncing
}
