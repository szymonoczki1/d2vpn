using System.Globalization;

namespace D2Vpn.Core.Hosting;

/// <summary>
/// What one module is doing, as the control surface reports it.
/// </summary>
/// <param name="Name">The module's address, such as <c>27k-ul</c>.</param>
/// <param name="IsActive">Whether it is holding.</param>
/// <param name="IsEnabled">Whether it is allowed to hold at all.</param>
/// <param name="PacketsSeen">Packets the module has decided on.</param>
/// <param name="PacketsDropped">Packets withheld and destroyed.</param>
/// <param name="PacketsBuffered">Packets withheld and kept for replay.</param>
/// <param name="PacketsReplayed">Packets released from the buffer.</param>
/// <param name="ResetsInjected">Resets injected, for the reconnect module.</param>
/// <param name="HeldSeconds">How long it has been active, or 0 if it is not.</param>
/// <param name="TotalHeldSeconds">How long it has been active in total this session.</param>
/// <remarks>
/// A record rather than live property reads, so a status line and a pipe reply describe the same
/// instant. Counters are cumulative for the life of the host.
/// </remarks>
public readonly record struct ModuleStatus(
    string Name,
    bool IsActive,
    bool IsEnabled,
    long PacketsSeen,
    long PacketsDropped,
    long PacketsBuffered,
    long PacketsReplayed,
    long ResetsInjected,
    double HeldSeconds = 0,
    double TotalHeldSeconds = 0)
{
    /// <summary>
    /// The status as one line of the control protocol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Space-separated <c>key=value</c>, which is what a consumer that is not .NET has to parse.
    /// AutoHotkey has no JSON in the box, and a line a script can split on spaces costs nothing to
    /// produce and nothing to read.
    /// </para>
    /// <para>
    /// <b>Invariant, explicitly.</b> This is a wire format, and on a machine whose locale uses a
    /// decimal comma it was emitting <c>held=0,0</c> - which a script splitting on the separator
    /// would misread, and which differed between machines running the same build. A library
    /// producing a protocol cannot rely on whoever hosts it having set the thread culture.
    /// </para>
    /// </remarks>
    public string ToLine() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Name} active={(IsActive ? 1 : 0)} enabled={(IsEnabled ? 1 : 0)} "
        + $"seen={PacketsSeen} dropped={PacketsDropped} "
        + $"buffered={PacketsBuffered} replayed={PacketsReplayed} resets={ResetsInjected} "
        + $"held={HeldSeconds:F1} totalHeld={TotalHeldSeconds:F1}");
}
