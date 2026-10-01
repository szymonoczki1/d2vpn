namespace D2Vpn.Core.Hosting;

/// <summary>
/// What one module is doing, as the control surface reports it.
/// </summary>
/// <param name="Name">The module's address, such as <c>27k-ul</c>.</param>
/// <param name="IsActive">Whether it is holding.</param>
/// <param name="PacketsSeen">Packets the module has decided on.</param>
/// <param name="PacketsDropped">Packets withheld and destroyed.</param>
/// <param name="PacketsBuffered">Packets withheld and kept for replay.</param>
/// <param name="PacketsReplayed">Packets released from the buffer.</param>
/// <param name="ResetsInjected">Resets injected, for the reconnect module.</param>
/// <remarks>
/// A record rather than live property reads, so a status line and a pipe reply describe the same
/// instant. Counters are cumulative for the life of the host.
/// </remarks>
public readonly record struct ModuleStatus(
    string Name,
    bool IsActive,
    long PacketsSeen,
    long PacketsDropped,
    long PacketsBuffered,
    long PacketsReplayed,
    long ResetsInjected)
{
    /// <summary>
    /// The status as one line of the control protocol.
    /// </summary>
    /// <remarks>
    /// Space-separated <c>key=value</c>, which is what a consumer that is not .NET has to parse.
    /// AutoHotkey has no JSON in the box, and a line a script can split on spaces costs nothing to
    /// produce and nothing to read.
    /// </remarks>
    public string ToLine() =>
        $"{Name} active={(IsActive ? 1 : 0)} seen={PacketsSeen} dropped={PacketsDropped} "
        + $"buffered={PacketsBuffered} replayed={PacketsReplayed} resets={ResetsInjected}";
}
