using System.Runtime.Versioning;

using D2Vpn.Core.Interception;
using D2Vpn.Core.Modules;

namespace D2Vpn.Core.Hosting;

/// <summary>
/// Runs one module over a real WinDivert handle, for a <see cref="ModuleHost"/>.
/// </summary>
/// <remarks>
/// The production <see cref="IModuleRunner"/>, and deliberately thin: it owns a module, a
/// controller and whatever buffering that module needs, and forwards the host's calls. Everything
/// worth testing is either in the module, the controller or the host, all of which were tested
/// before this existed.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinDivertModuleRunner : IModuleRunner
{
    private readonly WinDivertTrafficController _controller;
    private readonly ITrafficModule _module;
    private readonly ConnectionSampler? _sampler;

    /// <summary>Wraps a module and the pieces it needs.</summary>
    /// <param name="name">The control-surface name, such as <c>27k-ul</c>.</param>
    /// <param name="module">The module to run.</param>
    /// <param name="priority">The WinDivert handle priority.</param>
    /// <param name="buffer">A buffer, if the module withholds packets for replay.</param>
    /// <param name="rewriter">An ack rewriter, for replayed TCP segments.</param>
    /// <param name="sampler">A sampler, if this module injects resets.</param>
    public WinDivertModuleRunner(
        string name,
        ITrafficModule module,
        short priority = 0,
        PacketBuffer? buffer = null,
        TcpAckRewriter? rewriter = null,
        ConnectionSampler? sampler = null)
    {
        Name = name;
        _module = module;
        _sampler = sampler;
        _controller = new WinDivertTrafficController(module, priority, buffer, rewriter, sampler);
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public bool IsActive
    {
        get => _module.IsActive;
        set => _module.IsActive = value;
    }

    /// <inheritdoc/>
    public bool SupportsReset => _sampler is not null;

    /// <summary>The filter this module's handle was opened with.</summary>
    public string FilterExpression => _controller.FilterExpression;

    /// <inheritdoc/>
    public string? HandleKey => _controller.FilterExpression;

    /// <inheritdoc/>
    public Task RunAsync(CancellationToken cancellationToken) =>
        _controller.RunAsync(cancellationToken);

    /// <inheritdoc/>
    public void RequestReset() => _controller.RequestReset();

    /// <inheritdoc/>
    public ModuleStatus Status => new(
        Name,
        _module.IsActive,
        _controller.PacketsSeen,
        _controller.PacketsDropped,
        _controller.PacketsBuffered,
        _controller.PacketsReplayed,
        _controller.ResetsInjected);

    /// <inheritdoc/>
    public void Dispose()
    {
        _controller.Dispose();
        _sampler?.Dispose();
    }
}
