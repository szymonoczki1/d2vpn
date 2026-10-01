using System.Runtime.Versioning;

using D2Vpn.Core.Interception;
using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Hosting;

/// <summary>
/// Builds running modules from <see cref="ModuleSpec"/>s.
/// </summary>
/// <remarks>
/// <para>
/// The one place that knows a UDP group means <see cref="HoldModule"/> and a TCP group means
/// <see cref="ApiBlockModule"/>, which direction gets which handle priority, and what buffering each
/// needs. The single-module commands worked this out inline; the host would have repeated it.
/// </para>
/// <para>
/// <b>Specs are grouped by port group, one handle each.</b> A filter is keyed on the group alone, so
/// four toggles on 3074 become four modules behind one handle rather than four handles. That is what
/// <see cref="CreateAll"/> does, and why it is the entry point rather than a per-spec create.
/// </para>
/// <para>
/// <b>Presets only.</b> Everything comes from <see cref="MeasuredDefaults"/> or
/// <see cref="ApiBlockOptions"/>. The numeric flags <c>run</c> exposes - buffer size, resync
/// cooldown, slow depth - are not reachable through a spec string, so a session that needs them
/// still uses <c>run</c>. Autoresync and buffering are reachable, and change while running.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ModuleRunnerFactory
{
    /// <summary>
    /// Builds one runner per port group, with every spec on that group as a slot behind it.
    /// </summary>
    /// <remarks>
    /// A block is ordered before its slow variant, so turning both on blocks rather than delays -
    /// the first non-passing verdict wins. See <see cref="ModuleSlot"/>.
    /// </remarks>
    public static IReadOnlyList<WinDivertModuleRunner> CreateAll(IReadOnlyList<ModuleSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(specs);

        List<WinDivertModuleRunner> runners = [];

        try
        {
            foreach (var group in specs.GroupBy(s => s.Group))
            {
                runners.Add(CreateGroup(group.Key, [.. group.OrderBy(s => s.Slow)]));
            }
        }
        catch
        {
            // A later handle failing must not leak the ones already open.
            foreach (var runner in runners)
            {
                runner.Dispose();
            }

            throw;
        }

        return runners;
    }

    private static WinDivertModuleRunner CreateGroup(
        PortGroup group, IReadOnlyList<ModuleSpec> specs) =>
        group.Protocol == PortProtocol.Tcp
            ? CreateTcpGroup(group, specs)
            : CreateUdpGroup(group, specs);

    private static WinDivertModuleRunner CreateUdpGroup(
        PortGroup group, IReadOnlyList<ModuleSpec> specs)
    {
        List<(string, ModuleSlot, IModuleOptions?)> slots = [];

        foreach (var spec in specs)
        {
            var options = MeasuredDefaults.For(group, spec.Direction)
                ?? throw new ArgumentException($"no measured defaults for {spec.Name}");

            options = options with
            {
                Buffering = true,
                SlowMode = spec.Slow,

                // The throttle, not the fixed offset. A fixed delay starves the peer when the game
                // sends faster than the release rate; measurement settled on a rate cap, and that
                // is what "slow" means on a UDP hold here.
                SlowThrottle = spec.Slow
            };

            var module = new HoldModule(spec.Name, group, options);

            var schedule = options.SlowMode
                ? new PacketBuffer.SlowSchedule(
                    options.SlowDelay, options.SlowThrottle, options.SlowBurstWindow)
                : null;

            var buffer = new PacketBuffer(options.BufferCapacity, options.BufferMaxAge, schedule);

            slots.Add((spec.Name, new ModuleSlot(module, buffer), new HoldModuleToggles(module)));
        }

        // Inbound observability has to sit ahead of the WFP filter, which is what the single-module
        // command uses -1000 for. One handle now serves both directions, so it takes the lower
        // priority whenever any slot on it holds inbound.
        var priority = specs.Any(s => s.Direction != HoldDirection.Uplink)
            ? (short)-1000
            : (short)0;

        return new WinDivertModuleRunner(group.Name, slots, priority);
    }

    private static WinDivertModuleRunner CreateTcpGroup(
        PortGroup group, IReadOnlyList<ModuleSpec> specs)
    {
        List<(string, ModuleSlot, IModuleOptions?)> slots = [];

        foreach (var spec in specs)
        {
            var options = TcpDefaults(spec);
            var module = new ApiBlockModule(spec.Name, group, options);

            var schedule = options.ReleaseAfter is { } after
                ? new PacketBuffer.SlowSchedule(after)
                : null;

            // An ordered stream wants no age limit: a segment dropped for being stale blocks
            // everything behind it for good. See PacketBuffer.NeverExpires.
            var buffer = new PacketBuffer(2048, PacketBuffer.NeverExpires, schedule);

            slots.Add((spec.Name, new ModuleSlot(module, buffer), new ApiBlockToggles(module)));
        }

        // A replayed segment acknowledges a position the connection has long since passed, which
        // reads as a duplicate acknowledgement. Shared across the handle, as the observation is.
        var rewriter = new TcpAckRewriter();

        // The sampler is what makes 'reset' work, and it has to be on this handle: a second one on
        // an overlapping filter would never see a packet a hold had already withheld, and a reset is
        // built from packets seen in both directions.
        var sampler = new ConnectionSampler(TimeSpan.FromSeconds(10));

        return new WinDivertModuleRunner(group.Name, slots, 0, rewriter, sampler);
    }

    private static ApiBlockOptions TcpDefaults(ModuleSpec spec)
    {
        if (spec.Group == DestinyPorts.Instance)
        {
            return spec.Slow ? ApiBlockOptions.Instance30kSlow : ApiBlockOptions.Instance30k;
        }

        if (spec.Group == DestinyPorts.Api)
        {
            return spec.Slow
                ? ApiBlockOptions.Api7500 with { ReleaseAfter = TimeSpan.FromSeconds(6) }
                : ApiBlockOptions.Api7500;
        }

        throw new ArgumentException($"no TCP defaults for {spec.Group.Name}");
    }
}
