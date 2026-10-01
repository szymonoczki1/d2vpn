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
    private readonly List<IControllable> _controls = [];

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

        // Reconnect is its own entry rather than a button hidden inside a hold, because it is a
        // different kind of thing: one shot, no state, and useful whether or not anything is
        // holding. It still shares this handle - a sampler has to see the packets a hold withheld -
        // so it is a second control on one runner rather than a runner of its own.
        if (sampler is not null)
        {
            _reconnect = new ReconnectControl($"{label}-rc", this);
            _controls.Add(_reconnect);
        }
    }

    private readonly ReconnectControl? _reconnect;

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

        public ModuleKind Kind => ModuleKind.Hold;

        public bool IsEnabled { get; set; } = true;

        public bool IsActive
        {
            get => slot.Module.IsActive;
            set => slot.Module.IsActive = value;
        }

        // False: reconnect is its own control now, so a hold does not also pretend to offer it.
        public bool SupportsReset => false;

        public IModuleOptions? Options => options;

        public ModuleStatus Status => new(
            name,
            slot.Module.IsActive,
            IsEnabled,
            owner._controller.PacketsSeen,
            slot.Dropped,
            slot.Buffered,
            slot.Replayed,
            owner._controller.ResetsInjected);

        public IReadOnlyList<ModuleMetric> Metrics =>
        [
            new("seen", $"{owner._controller.PacketsSeen}"),
            new("withheld", $"{slot.Dropped}"),
            new("buffered", $"{slot.Buffered}"),
            new("replayed", $"{slot.Replayed}")
        ];

        public void RequestReset() =>
            throw new InvalidOperationException(
                $"'{name}' is a hold - use '{owner.Name}-rc' to reconnect");
    }

    /// <summary>
    /// The reconnect for a port group, as its own addressable entry.
    /// </summary>
    /// <remarks>
    /// A trigger: it has no on state, so <see cref="IsActive"/> is always false and activating it
    /// fires a reset instead of latching. <see cref="ModuleHost.Toggle"/> routes a key press here
    /// rather than treating it as a switch.
    /// </remarks>
    private sealed class ReconnectControl(string name, WinDivertModuleRunner owner) : IControllable
    {
        public string Name => name;

        public ModuleKind Kind => ModuleKind.Trigger;

        public bool IsEnabled { get; set; } = true;

        /// <summary>Always false: there is nothing here to leave switched on.</summary>
        public bool IsActive
        {
            get => false;
            set
            {
                if (value)
                {
                    RequestReset();
                }
            }
        }

        public bool SupportsReset => true;

        public IModuleOptions? Options => null;

        public ModuleStatus Status => new(
            name,
            false,
            IsEnabled,
            owner._controller.PacketsSeen,
            0,
            0,
            0,
            owner._controller.ResetsInjected);

        // A reconnect withholds nothing, so the packet counters a hold reports would all be zero.
        // What it does is inject resets, and how much traffic the handle has seen says whether
        // there is a connection live enough to reset at all.
        public IReadOnlyList<ModuleMetric> Metrics =>
        [
            new("resets sent", $"{owner._controller.ResetsInjected}"),
            new("packets seen", $"{owner._controller.PacketsSeen}")
        ];

        public void RequestReset()
        {
            if (!IsEnabled)
            {
                throw new InvalidOperationException($"'{name}' is disabled");
            }

            owner._controller.RequestReset();
        }
    }
}
