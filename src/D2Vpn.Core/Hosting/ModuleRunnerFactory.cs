using System.Runtime.Versioning;

using D2Vpn.Core.Interception;
using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

namespace D2Vpn.Core.Hosting;

/// <summary>
/// Builds a running module from a <see cref="ModuleSpec"/>.
/// </summary>
/// <remarks>
/// <para>
/// The one place that knows a UDP group means <see cref="HoldModule"/> and a TCP group means
/// <see cref="ApiBlockModule"/>, which direction gets which handle priority, and what buffering
/// each needs. The single-module commands worked this out inline; the host would have repeated it.
/// </para>
/// <para>
/// <b>Presets only.</b> Everything here comes from <see cref="MeasuredDefaults"/> or
/// <see cref="ApiBlockOptions"/> with buffering on, matching the <c>run</c> command's defaults. The
/// per-module flags that command exposes - buffer size, resync cooldown, slow depth - are not
/// reachable through a spec string, so a session that needs them still uses <c>run</c>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ModuleRunnerFactory
{
    /// <summary>Creates a runner for a spec.</summary>
    public static WinDivertModuleRunner Create(ModuleSpec spec) =>
        spec.Group.Protocol == PortProtocol.Tcp
            ? CreateTcp(spec)
            : CreateUdp(spec);

    private static WinDivertModuleRunner CreateUdp(ModuleSpec spec)
    {
        var options = MeasuredDefaults.For(spec.Group, spec.Direction)
            ?? throw new ArgumentException(
                $"no measured defaults for {spec.Name}");

        options = options with
        {
            Buffering = true,
            SlowMode = spec.Slow,

            // The throttle, not the fixed offset. A fixed delay on uplink starves the peer when the
            // game sends faster than the release rate; measurement settled on a rate cap, and that
            // is what "slow" means on a UDP hold here.
            SlowThrottle = spec.Slow
        };

        var module = new HoldModule(spec.Group.Name, spec.Group, options);

        var schedule = options.SlowMode
            ? new PacketBuffer.SlowSchedule(
                options.SlowDelay, options.SlowThrottle, options.SlowBurstWindow)
            : null;

        var buffer = new PacketBuffer(options.BufferCapacity, options.BufferMaxAge, schedule);

        // Inbound observability needs to sit ahead of the WFP filter, which is what the single
        // module command uses -1000 for on a downlink hold.
        var priority = spec.Direction == HoldDirection.Uplink ? (short)0 : (short)-1000;

        return new WinDivertModuleRunner(spec.Name, module, priority, buffer);
    }

    private static WinDivertModuleRunner CreateTcp(ModuleSpec spec)
    {
        var options = Defaults(spec);
        var module = new ApiBlockModule(spec.Group.Name, spec.Group, options);

        var schedule = options.ReleaseAfter is { } after
            ? new PacketBuffer.SlowSchedule(after)
            : null;

        // An ordered stream does not want an age limit: a segment dropped for being stale blocks
        // everything behind it for good. See PacketBuffer.NeverExpires.
        var buffer = new PacketBuffer(2048, PacketBuffer.NeverExpires, schedule);

        // A replayed segment acknowledges a position the connection has long since passed, which
        // would be read as a duplicate acknowledgement.
        var rewriter = new TcpAckRewriter();

        // A sampler on the same handle is what makes 'reset' work on this module. It has to be the
        // same handle: a second one on an overlapping filter would never see a packet this module
        // had already withheld, and a reset is built from packets seen in both directions.
        var sampler = new ConnectionSampler(TimeSpan.FromSeconds(10));

        return new WinDivertModuleRunner(spec.Name, module, 0, buffer, rewriter, sampler);
    }

    private static ApiBlockOptions Defaults(ModuleSpec spec)
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
