using System.Runtime.Versioning;

using D2Vpn.Core.Interception;
using D2Vpn.Core.Modules;

namespace D2Vpn.Core.Hosting;

/// <summary>
/// One WinDivert handle, shared by every module on a port group.
/// </summary>
/// <remarks>
/// <para>
/// A filter is built from the port group alone - protocol and port range, never direction - so
/// every module on one group matches the same packets and they cannot each have a handle. This
/// runner owns the handle and exposes one <see cref="IControllable"/> per module behind it, which is
/// what lets <c>3074-ul</c>, <c>3074-dl</c>, <c>3074-ul-slow</c> and <c>3074-dl-slow</c> be four
/// independent toggles.
/// </para>
/// <para>
/// Each control carries its own buffer and its own release, so turning off the uplink hold replays
/// what it withheld and leaves the downlink hold's queue alone. See <see cref="ModuleSlot"/>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinDivertModuleRunner : IModuleRunner
{
    private readonly WinDivertTrafficController _controller;
    private readonly ConnectionSampler? _sampler;
    private readonly List<SlotControl> _controls = [];

    /// <summary>Opens one handle for a set of slots on the same port group.</summary>
    /// <param name="label">A name for this handle, used in errors and on the status line.</param>
    /// <param name="slots">
    /// The modules sharing the handle, each paired with the options it exposes at runtime.
    /// Consulted in declared order; the first non-passing verdict wins.
    /// </param>
    /// <param name="priority">The WinDivert handle priority.</param>
    /// <param name="rewriter">A shared ack rewriter, for replayed TCP segments.</param>
    /// <param name="sampler">A shared sampler, if these modules can inject resets.</param>
    public WinDivertModuleRunner(
        string label,
        IReadOnlyList<(string Name, ModuleSlot Slot, IModuleOptions? Options)> slots,
        short priority = 0,
        TcpAckRewriter? rewriter = null,
        ConnectionSampler? sampler = null)
    {
        Name = label;
        _sampler = sampler;
        _controller = new WinDivertTrafficController(
            [.. slots.Select(s => s.Slot)], priority, rewriter, sampler);

        foreach (var (name, slot, options) in slots)
        {
            _controls.Add(new SlotControl(name, slot, options, this));
        }
    }

    /// <summary>Convenience for a handle carrying exactly one module.</summary>
    public WinDivertModuleRunner(
        string name,
        ITrafficModule module,
        short priority = 0,
        PacketBuffer? buffer = null,
        TcpAckRewriter? rewriter = null,
        ConnectionSampler? sampler = null,
        IModuleOptions? options = null)
        : this(name, [(name, new ModuleSlot(module, buffer), options)], priority, rewriter, sampler)
    {
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public IReadOnlyList<IControllable> Controls => _controls;

    /// <summary>The filter this handle was opened with.</summary>
    public string FilterExpression => _controller.FilterExpression;

    /// <inheritdoc/>
    public string? HandleKey => _controller.FilterExpression;

    /// <inheritdoc/>
    public Task RunAsync(CancellationToken cancellationToken) =>
        _controller.RunAsync(cancellationToken);

    /// <inheritdoc/>
    public void Dispose()
    {
        _controller.Dispose();
        _sampler?.Dispose();
    }

    /// <summary>One module behind the shared handle, as the control surface sees it.</summary>
    private sealed class SlotControl(
        string name,
        ModuleSlot slot,
        IModuleOptions? options,
        WinDivertModuleRunner owner) : IControllable
    {
        public string Name => name;

        public bool IsActive
        {
            get => slot.Module.IsActive;
            set => slot.Module.IsActive = value;
        }

        // A sampler is shared across the handle, so every module on a group that can reset can.
        public bool SupportsReset => owner._sampler is not null;

        public IModuleOptions? Options => options;

        public ModuleStatus Status => new(
            name,
            slot.Module.IsActive,
            owner._controller.PacketsSeen,
            slot.Dropped,
            slot.Buffered,
            slot.Replayed,
            owner._controller.ResetsInjected);

        public void RequestReset() => owner._controller.RequestReset();
    }
}
