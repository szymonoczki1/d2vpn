using D2Vpn.Cli;
using D2Vpn.Core.Calibration;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "list" => ListGroups(),
        "watch" => await WatchAsync(args),
        "calibrate" => await CalibrateAsync(args),
        "hold" => await HoldAsync(args),
        _ => Unknown(args[0])
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    if (ex.InnerException is not null)
    {
        Console.Error.WriteLine($"  cause: {ex.InnerException.Message}");
    }
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("d2vpn - Destiny 2 traffic tooling");
    Console.WriteLine();
    Console.WriteLine("usage:");
    Console.WriteLine("  d2vpn list");
    Console.WriteLine("      Show the known port groups.");
    Console.WriteLine();
    Console.WriteLine("  d2vpn watch <group> [--seconds N] [--priority N]");
    Console.WriteLine("      Live packet counts and payload sizes for a group.");
    Console.WriteLine("      Observes only: packets are copied, never diverted.");
    Console.WriteLine();
    Console.WriteLine("  d2vpn calibrate <group> [--seconds N] [--downlink] [--priority N]");
    Console.WriteLine("      Derive this machine's packet-length signature for a group.");
    Console.WriteLine("      Hold the module partway through the run; the hold is found");
    Console.WriteLine("      from the traffic itself, so the timing is up to you. Reports the");
    Console.WriteLine("      payload sizes that crossed while that direction was collapsed -");
    Console.WriteLine("      the packets a module must pass for the session to survive.");
    Console.WriteLine("      --downlink calibrates a DL hold instead. The two need separate");
    Console.WriteLine("      signatures: neither can be derived from the other.");
    Console.WriteLine();
    Console.WriteLine("  d2vpn hold <group> [--seconds N] [--warmup N] [--dry-run]");
    Console.WriteLine("                     [--downlink] [--priority N]");
    Console.WriteLine("      Hold one direction on a group: drop it except the signature");
    Console.WriteLine("      sizes, and stop blocking when the peer asks to reconnect, so");
    Console.WriteLine("      the session survives. --dry-run reports what it WOULD drop");
    Console.WriteLine("      without touching traffic - the safe way to try it first.");
    Console.WriteLine();
    Console.WriteLine("groups:  3074  27k  7500  30k");
    Console.WriteLine();
    Console.WriteLine("--priority sets the WinDivert handle priority (-30000..30000, default 0).");
    Console.WriteLine("Handles are ordered by it, and a diverting handle that drops a packet");
    Console.WriteLine("removes it before lower-ranked handles see it. If another limiter sits");
    Console.WriteLine("behind this one, its blocking is invisible here - the packet was already");
    Console.WriteLine("counted. Change this if a hold you know happened does not show up.");
    Console.WriteLine();
    Console.WriteLine("watch, calibrate and hold all need administrator rights.");
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"unknown command '{command}'. Try: d2vpn --help");
    return 1;
}

static int ListGroups()
{
    Console.WriteLine($"{"name",-6} {"proto",-5} {"ports",-14} {"count",6}");
    Console.WriteLine(new string('-', 34));
    foreach (var g in DestinyPorts.All)
    {
        var ports = g.FromPort == g.ToPort ? $"{g.FromPort}" : $"{g.FromPort}-{g.ToPort}";
        Console.WriteLine($"{g.Name,-6} {g.Protocol.ToString().ToUpperInvariant(),-5} {ports,-14} {g.PortCount,6}");
    }

    return 0;
}

static PortGroup ResolveGroup(string[] args)
{
    if (args.Length < 2)
    {
        throw new ArgumentException("missing group name. Try: d2vpn list");
    }

    return DestinyPorts.ByName(args[1])
        ?? throw new ArgumentException($"unknown group '{args[1]}'. Try: d2vpn list");
}

/// <summary>
/// Reads a WinDivert handle priority.
/// </summary>
/// <remarks>
/// Worth exposing because it changes what can be observed at all. Handles are ordered by priority,
/// and a diverting handle that drops a packet removes it before lower-ranked handles see it. A
/// limiter sitting ahead of us makes its blocking visible; one sitting behind us makes its
/// blocking invisible, because we counted the packet before it was dropped. That is the leading
/// explanation for why a downlink hold barely registered while an uplink hold was obvious.
/// </remarks>
static short PriorityOption(string[] args, short fallback = 0)
{
    var i = Array.FindIndex(args, a => string.Equals(a, "--priority", StringComparison.OrdinalIgnoreCase));
    if (i < 0 || i + 1 >= args.Length)
    {
        return fallback;
    }

    // Parsed as int so an out-of-range value reports the range rather than failing to parse.
    if (!int.TryParse(args[i + 1], out var value))
    {
        throw new ArgumentException("--priority needs a whole number.");
    }

    // WinDivert's documented range.
    if (value is < -30000 or > 30000)
    {
        throw new ArgumentException(
            $"--priority must be between -30000 and 30000, got {value}.");
    }

    return (short)value;
}

static bool HasFlag(string[] args, string name) =>
    args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

static int IntOption(string[] args, string name, int fallback)
{
    var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    if (i < 0 || i + 1 >= args.Length)
    {
        return fallback;
    }

    return int.TryParse(args[i + 1], out var value) && value > 0
        ? value
        : throw new ArgumentException($"{name} needs a positive whole number.");
}

static async Task<int> WatchAsync(string[] args)
{
    var group = ResolveGroup(args);
    var seconds = IntOption(args, "--seconds", 30);
    var priority = PriorityOption(args);

    if (!Elevation.RequireElevated())
    {
        return 1;
    }

    using var source = new WinDivertPacketSource(group, priority);
    Console.WriteLine($"watching {group} for {seconds}s  (sniff mode, traffic is not modified)");
    Console.WriteLine($"handle priority: {priority}");
    Console.WriteLine($"filter: {source.FilterExpression}");
    Console.WriteLine();

    var outbound = new LengthHistogram();
    var inbound = new LengthHistogram();
    var peers = new HashSet<string>();
    var byInterface = new Dictionary<int, int>();

    // Duplicate detection, kept as a standing check. A rate here once looked like double what
    // Wireshark measured, which suggested the driver was reporting packets twice - plausible over
    // a VPN, where an outbound packet could cross the stack on its way to the tunnel and again as
    // it is handed over. Measurement refuted it: 0 duplicates in 875 packets on one interface, and
    // the rate gap was simply a second peer that the capture had not seen. Worth keeping, because
    // a repeat of the same direction, peer and payload size inside a few milliseconds is not
    // something real traffic does at a 100 ms tick, so it would catch the problem if it ever
    // did arise.
    var duplicates = 0;
    var duplicateAcrossInterfaces = 0;
    (DateTimeOffset At, PacketDirection Dir, string Peer, int Len, int If)? previous = null;

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    var started = DateTimeOffset.UtcNow;
    var lastPrint = started;

    await foreach (var packet in source.ReadAllAsync(cts.Token))
    {
        (packet.IsOutbound ? outbound : inbound).Add(packet.PayloadLength);
        peers.Add(packet.RemoteAddress.ToString());
        byInterface[packet.InterfaceIndex] = byInterface.GetValueOrDefault(packet.InterfaceIndex) + 1;

        var peerKey = packet.RemoteAddress.ToString();
        if (previous is { } p
            && p.Dir == packet.Direction
            && p.Peer == peerKey
            && p.Len == packet.PayloadLength
            && (packet.Timestamp - p.At) < TimeSpan.FromMilliseconds(5))
        {
            duplicates++;
            if (p.If != packet.InterfaceIndex)
            {
                duplicateAcrossInterfaces++;
            }
        }

        previous = (packet.Timestamp, packet.Direction, peerKey, packet.PayloadLength, packet.InterfaceIndex);

        var now = DateTimeOffset.UtcNow;
        if (now - lastPrint >= TimeSpan.FromSeconds(1))
        {
            lastPrint = now;
            var elapsed = (now - started).TotalSeconds;
            Console.Write($"\r{elapsed,5:N0}s  UL {outbound.Total,6} ({outbound.Total / elapsed,5:N1}/s)"
                + $"  DL {inbound.Total,6} ({inbound.Total / elapsed,5:N1}/s)  peers {peers.Count}   ");
        }
    }

    var total = outbound.Total + inbound.Total;
    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine($"--- sightings by network interface ---");
    foreach (var entry in byInterface.OrderByDescending(e => e.Value))
    {
        Console.WriteLine($"  ifIdx {entry.Key,-6} {entry.Value,7} packets");
    }

    Console.WriteLine();
    Console.WriteLine($"--- repeated sightings ---");
    Console.WriteLine($"  {duplicates} of {total} packets repeated the previous packet exactly");
    Console.WriteLine($"  (same direction, peer and payload size, within 5 ms)");
    if (duplicateAcrossInterfaces > 0)
    {
        Console.WriteLine($"  {duplicateAcrossInterfaces} of those arrived on a DIFFERENT interface");
    }

    if (total > 0 && duplicates * 4 > total)
    {
        Console.WriteLine();
        Console.WriteLine("  That is far too many to be real traffic: the driver is reporting the");
        Console.WriteLine("  same packet more than once, and every rate this tool prints is inflated.");
    }

    Console.WriteLine();
    PrintHistogram("outbound (UL)", outbound);
    PrintHistogram("inbound (DL)", inbound);
    return 0;
}

static async Task<int> CalibrateAsync(string[] args)
{
    var group = ResolveGroup(args);
    var seconds = IntOption(args, "--seconds", 45);
    var downlink = HasFlag(args, "--downlink");
    var direction = downlink ? PacketDirection.Inbound : PacketDirection.Outbound;
    var label = downlink ? "downlink" : "uplink";

    // Measured: at +1000 a full-duration downlink hold was completely invisible - flat traffic
    // throughout - while at -1000 the same hold showed downlink at 3% of normal for 41 seconds.
    // Handles sit at a fixed position in the stack and packets traverse it in opposite directions,
    // so the priority that puts this process downstream of another limiter differs by direction.
    // Uplink already observes correctly at 0.
    var priority = PriorityOption(args, downlink ? (short)-1000 : (short)0);

    if (!Elevation.RequireElevated())
    {
        return 1;
    }

    using var source = new WinDivertPacketSource(group, priority);
    var calibrator = new SignatureCalibrator(group, new CalibrationOptions { Direction = direction });

    Console.WriteLine($"calibrating {group} {label}  ({seconds}s, sniff mode - traffic is not modified)");
    Console.WriteLine($"filter: {source.FilterExpression}");
    Console.WriteLine($"handle priority: {priority}");
    Console.WriteLine();
    Console.WriteLine("What to do, in the game window:");
    Console.WriteLine("  1. leave it alone for the first ~10s");
    Console.WriteLine($"  2. hold your {label} module for 10-15s");
    Console.WriteLine("  3. release, and leave it alone until this finishes");
    Console.WriteLine();
    Console.WriteLine("The exact timing does not matter. The hold is found from the traffic,");
    Console.WriteLine("so you do not need to match a countdown. Hotkeys only register while the");
    Console.WriteLine("game has focus, so click into it now.");
    Console.WriteLine();

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    var started = DateTimeOffset.UtcNow;
    var lastPrint = started;
    var recentOutbound = 0;

    await foreach (var packet in source.ReadAllAsync(cts.Token))
    {
        calibrator.Observe(packet);

        if (packet.Direction == direction)
        {
            recentOutbound++;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - lastPrint >= TimeSpan.FromSeconds(1))
        {
            // A live uplink rate makes a failed run obvious while it is still running: if this
            // never drops near zero, the module is not engaging.
            var elapsed = (now - started).TotalSeconds;
            Console.Write($"\r{elapsed,5:N0}s / {seconds}s   {label} {recentOutbound,4}/s   ");
            lastPrint = now;
            recentOutbound = 0;
        }
    }

    Console.WriteLine();
    Console.WriteLine();

    var result = calibrator.Build();
    PrintCalibration(result);
    return result.BlockDetected ? 0 : 2;
}

static async Task<int> HoldAsync(string[] args)
{
    var group = ResolveGroup(args);
    var seconds = IntOption(args, "--seconds", 45);
    var warmup = IntOption(args, "--warmup", 10);
    var dryRun = HasFlag(args, "--dry-run");
    var downlink = HasFlag(args, "--downlink");
    var priority = PriorityOption(args);

    var heldDirection = downlink ? HoldDirection.Downlink : HoldDirection.Uplink;
    var options = MeasuredDefaults.For(group, heldDirection);
    if (options is null)
    {
        throw new ArgumentException(
            $"No {(downlink ? "downlink" : "uplink")} signature has been measured for {group}. "
            + $"Run 'd2vpn calibrate {group.Name}{(downlink ? " --downlink" : string.Empty)}' first.");
    }

    if (options.ResyncTriggerLengths.Count == 0 && options.PassLengths.Count > 0)
    {
        Console.WriteLine("NOTE: no resync trigger is known for this direction, so the module will");
        Console.WriteLine("block without ever letting the peer re-establish. A safety timeout ends");
        Console.WriteLine("the hold before that becomes a problem. See docs/protocol.md.");
        Console.WriteLine();
    }
    if (options is null)
    {
        throw new ArgumentException(
            $"No signature is known for {group}. Run 'd2vpn calibrate {group.Name}' first.");
    }

    if (options.PassLengths.Count == 0)
    {
        Console.WriteLine("NOTE: this direction has no pass list, so it blocks outright.");
        Console.WriteLine("For 3074 downlink that is deliberate - measurement showed nothing needs");
        Console.WriteLine("to cross. Elsewhere it means the group has not been calibrated.");
        Console.WriteLine();
    }

    if (!Elevation.RequireElevated())
    {
        return 1;
    }

    var module = new HoldModule(group.Name, group, options);
    module.SafetyTimeoutElapsed += m =>
        Console.WriteLine($"\n{m.Name}: safety timeout reached, module disabled.");

    Console.WriteLine($"{(dryRun ? "DRY RUN" : "HOLD")}  {group}   {seconds}s");
    Console.WriteLine($"  pass sizes   : {string.Join(", ", options.PassLengths.Order())}");
    Console.WriteLine($"  resync starts: {string.Join(", ", options.ResyncTriggerLengths.Order())}"
        + $"  (for {options.ResyncDuration.TotalSeconds:N1}s)");
    Console.WriteLine($"  safety limit : {(options.SafetyTimeout is { } t ? $"{t.TotalSeconds:N0}s" : "none")}");
    Console.WriteLine();

    if (dryRun)
    {
        Console.WriteLine("Sniff mode - packets are copied and continue untouched. Nothing is");
        Console.WriteLine("dropped; this only reports what a real hold would have done.");
    }
    else
    {
        Console.WriteLine("DIVERT MODE - packets really are dropped.");
        Console.WriteLine("Any other limiter must be fully EXITED, not just idle: two handles on");
        Console.WriteLine("overlapping filters both get every packet and both can drop it.");
        Console.WriteLine("Ctrl+C releases immediately; closing the handle restores traffic.");
    }

    Console.WriteLine();
    Console.WriteLine($"Running {warmup}s idle, then holding for {seconds - warmup}s.");
    Console.WriteLine("The idle stretch is a baseline: it is what makes the run prove itself,");
    Console.WriteLine("without needing to see anything in-game.");
    Console.WriteLine();
    Console.WriteLine("Starting in 5s - click into the game now.");
    await Task.Delay(TimeSpan.FromSeconds(5));

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    using var sigint = SetupInterrupt(cts);

    var started = DateTimeOffset.UtcNow;

    if (dryRun)
    {
        await RunDryAsync(group, module, started, seconds, warmup, priority, cts.Token);
        return 0;
    }

    using var controller = new WinDivertTrafficController(module, priority);
    var run = controller.RunAsync(cts.Token);

    long warmupInbound = 0;
    var engaged = false;

    while (!run.IsCompleted)
    {
        await Task.WhenAny(run, Task.Delay(1000, CancellationToken.None));
        var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;

        if (!engaged && elapsed >= warmup)
        {
            engaged = true;
            warmupInbound = controller.InboundSeen;
            module.IsActive = true;
        }
        Console.Write($"\r{elapsed,5:N0}s/{seconds}s  "
            + $"UL {controller.OutboundDropped}/{controller.OutboundSeen} dropped  "
            + $"DL {controller.InboundDropped}/{controller.InboundSeen} dropped  "
            + $"resyncs {module.ResyncsStarted}  "
            + $"{(module.IsActive ? "HOLDING" : engaged ? "released" : "baseline")}   ");
    }

    await run;

    var triggersSeen = module.TriggersSeen;
    var resyncsStarted = module.ResyncsStarted;
    module.IsActive = false;

    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine($"uplink   : {controller.OutboundDropped} dropped of {controller.OutboundSeen} seen");
    Console.WriteLine($"downlink : {controller.InboundDropped} dropped of {controller.InboundSeen} seen");
    Console.WriteLine();
    Console.WriteLine($"reconnect requests seen : {triggersSeen}");
    Console.WriteLine($"resync windows started  : {resyncsStarted}");

    // Two independent checks that the block reached the game, neither needing anything observed
    // in play. The game asking to reconnect means it noticed a peer went quiet; the peer sending
    // harder than usual means it noticed us. Either one alone is weak; together they settle it.
    var holdSeconds = Math.Max(1, seconds - warmup);
    var baselineRate = warmup > 0 ? warmupInbound / (double)warmup : 0;
    var holdRate = (controller.InboundSeen - warmupInbound) / (double)holdSeconds;

    Console.WriteLine();
    Console.WriteLine($"downlink while idle     : {baselineRate,5:N1}/s");
    Console.WriteLine($"downlink while holding  : {holdRate,5:N1}/s");

    // A fired resync is decisive: the game only asks to reconnect when it has noticed a peer go
    // quiet. The downlink-rate check is a weaker corroborator - it did not move in a run where the
    // resync demonstrably worked, so its absence proves nothing on its own.
    var landed = triggersSeen > 0 || (baselineRate > 0 && holdRate > baselineRate * 1.25);
    Console.WriteLine();

    if (landed)
    {
        Console.WriteLine("The block reached the game:");
        if (triggersSeen > 0)
        {
            Console.WriteLine($"  - it asked to reconnect {triggersSeen} times");
            Console.WriteLine($"  - the resync opened {resyncsStarted} times, letting it recover");
        }

        if (baselineRate > 0 && holdRate > baselineRate * 1.25)
        {
            Console.WriteLine("  - the peer sent harder than usual, probing for us");
        }
    }
    else
    {
        Console.WriteLine("No sign the block reached the game. It never asked to reconnect and the");
        Console.WriteLine("peer did not react, so surviving this run proves nothing. Most likely the");
        Console.WriteLine("packets are being dropped somewhere that does not matter - try a");
        Console.WriteLine("different --priority, the way downlink measurement needed one.");
    }

    if (triggersSeen == 0)
    {
        // Without this the run is uninterpretable: a hold that survived might have resynced, or
        // might simply not have been blocking hard enough for the game to notice.
        Console.WriteLine();
        Console.WriteLine("No reconnect request was ever seen, so the resync never ran. Either the");
        Console.WriteLine("game did not notice the block, or it asks in a size not in the trigger");
        Console.WriteLine($"list ({string.Join(", ", options.ResyncTriggerLengths.Order())}).");
        Console.WriteLine("Run 'd2vpn calibrate' while this is holding to see what it actually sends.");
    }

    if (controller.PacketsPassedOnError > 0)
    {
        Console.WriteLine($"{controller.PacketsPassedOnError} packets were passed because they could");
        Console.WriteLine("not be decided. Traffic was never at risk, but the module did not see them.");
    }

    Console.WriteLine();
    Console.WriteLine("Released. Did the game survive past 30s without an error code?");
    Console.WriteLine("That is the whole test: a pass-list alone dies there, resync is what does not.");
    return 0;
}

/// <summary>
/// Runs the module against sniffed traffic, reporting decisions without acting on them.
/// </summary>
static async Task RunDryAsync(
    PortGroup group, HoldModule module, DateTimeOffset started, int seconds, int warmup,
    short priority, CancellationToken token)
{
    using var source = new WinDivertPacketSource(group, priority);
    long seen = 0;
    long wouldDrop = 0;
    var engaged = false;
    var lastPrint = started;

    await foreach (var packet in source.ReadAllAsync(token))
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = (now - started).TotalSeconds;

        // Engage on the same schedule as a real hold, or the two runs are not comparable.
        if (!engaged && elapsed >= warmup)
        {
            engaged = true;
            module.IsActive = true;
        }

        seen++;
        if (module.Decide(packet) == PacketVerdict.Drop)
        {
            wouldDrop++;
        }

        if (now - lastPrint >= TimeSpan.FromSeconds(1))
        {
            lastPrint = now;
            Console.Write($"\r{elapsed,5:N0}s / {seconds}s   seen {seen,6}"
                + $"   would drop {wouldDrop,6}   resyncs {module.ResyncsStarted}"
                + $"   {(engaged ? "HOLDING" : "baseline")}   ");
        }
    }

    var triggers = module.TriggersSeen;
    module.IsActive = false;

    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine($"seen {seen}, would have dropped {wouldDrop}, would have passed {seen - wouldDrop}");
    Console.WriteLine();
    Console.WriteLine("Nothing was actually dropped, so the game never noticed and never asked to");
    Console.WriteLine($"reconnect - {triggers} requests seen, and a dry run cannot produce them.");
    Console.WriteLine("This checks the filter and the drop ratio only. Whether the resync works can");
    Console.WriteLine("only be answered by a real hold.");
}

/// <summary>Wires Ctrl+C to release the hold rather than kill the process mid-flight.</summary>
static IDisposable SetupInterrupt(CancellationTokenSource cts)
{
    ConsoleCancelEventHandler handler = (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine();
        Console.WriteLine("releasing...");
        cts.Cancel();
    };

    Console.CancelKeyPress += handler;
    return new D2Vpn.Cli.Disposer(() => Console.CancelKeyPress -= handler);
}

static void PrintHistogram(string title, LengthHistogram histogram)
{
    Console.WriteLine($"--- {title}: {histogram.Total} packets, {histogram.DistinctLengths.Count} distinct sizes ---");
    if (histogram.Total == 0)
    {
        Console.WriteLine("  (nothing seen)");
        Console.WriteLine();
        return;
    }

    foreach (var entry in histogram.MostCommon(12))
    {
        var share = (double)entry.Count / histogram.Total;
        Console.WriteLine($"  {entry.PayloadLength,6} bytes  x{entry.Count,-6} {new string('#', (int)(share * 40))}");
    }

    Console.WriteLine($"  largest: {histogram.Largest} bytes");
    Console.WriteLine();
}

static void PrintCalibration(CalibrationResult result)
{
    Console.WriteLine($"=== {result.Group} - {result.Peers.Count} peer(s) ===");
    Console.WriteLine();

    foreach (var peer in result.Peers)
    {
        if (!peer.HoldDetected)
        {
            Console.WriteLine($"  {peer.Address,-18} {peer.BaselineRate,5:N1}/s normal   no hold");
            continue;
        }

        var spans = string.Join(" + ", peer.Windows.Select(w =>
            $"{w.Start.TotalSeconds:N0}-{(w.Start + w.Duration).TotalSeconds:N0}s"));

        Console.WriteLine($"  {peer.Address,-18} {peer.BaselineRate,5:N1}/s normal   "
            + $"HELD {spans}  ({peer.TotalHeld.TotalSeconds:N0}s total, {peer.CollapseRatio:P0} of normal)");
    }

    Console.WriteLine();

    if (!result.HasUsableBaseline)
    {
        Console.WriteLine("Too little normal traffic on any peer to judge against.");
        Console.WriteLine("Run it again while the game is actually sending on this group.");
        PrintTimelines(result);
        return;
    }

    if (!result.BlockDetected)
    {
        Console.WriteLine($"No {result.HeldLabel} collapse on any peer - the module never engaged.");
        Console.WriteLine();
        Console.WriteLine($"During a real hold a peer's {result.HeldLabel} falls to near zero for several");
        Console.WriteLine("seconds. Check the timelines below before blaming the module:");
        Console.WriteLine();
        Console.WriteLine("  - no sustained dip anywhere -> the keypress did not reach the limiter,");
        Console.WriteLine("    usually because the game did not have focus");
        Console.WriteLine("  - a clear dip that was not picked up -> the normal rate above is wrong");
        Console.WriteLine("    for this run, which happens when traffic is very uneven");
        PrintTimelines(result);
        return;
    }

    if (result.HoldWasPartial)
    {
        // Worth stating plainly: it changes what a module has to do, not just how we measure.
        Console.WriteLine($"NOTE: {result.HeldPeers.Count} of {result.Peers.Count} peers were held.");
        Console.WriteLine("Either the module holds connections individually, or a peer joined or");
        Console.WriteLine("left during the run. Analysing peers together would have hidden this.");
        Console.WriteLine();
    }

    var window = result.CombinedWindow!;
    Console.WriteLine($"hold spans {window.Start.TotalSeconds:N0}s -> "
        + $"{(window.Start + window.Duration).TotalSeconds:N0}s, "
        + $"{window.OutboundPackets} {result.HeldLabel} packets crossed");

    var maxHolds = result.HeldPeers.Max(p => p.Windows.Count);
    if (maxHolds > 1)
    {
        // Worth stating: one keypress does not produce one block.
        Console.WriteLine();
        Console.WriteLine($"This was {maxHolds} separate collapses, not one. Between them {result.HeldLabel}");
        Console.WriteLine("returned to full rate - the limiter letting the game re-establish with");
        Console.WriteLine("its peer before blocking again. A hold is block/resync/block, not a");
        Console.WriteLine("single continuous cut.");
    }

    Console.WriteLine();

    Console.WriteLine($"YOUR SIGNATURE - {result.HeldLabel} payload sizes concentrated in the hold:");
    Console.WriteLine($"  {string.Join(", ", result.Signature)}");
    Console.WriteLine();
    Console.WriteLine("These are the packets a module must let through for the session to survive");
    Console.WriteLine("a hold. Blocking them too would make the game disconnect.");
    Console.WriteLine();

    // One table, in enrichment order, so the line between signature and stray is visible rather
    // than asserted. "Concentrated" is how much more often a size occurs during a hold than
    // normally; membership in normal traffic on its own means very little, because payload sizes
    // spread across a hundred-odd values and most pick up a count of one or two by chance.
    Console.WriteLine("every size that crossed during the hold:");
    Console.WriteLine();
    Console.WriteLine($"  {"size",6}  {"in hold",14}  {"in normal",14}  {"concentrated",12}   verdict");

    foreach (var a in result.AllLengthsWithStats)
    {
        var hold = $"{a.BlockCount} ({a.BlockShare:P0})";
        var normal = a.BaselineCount == 0 ? "never" : $"{a.BaselineCount} ({a.BaselineShare:P1})";
        var factor = double.IsInfinity(a.Enrichment) ? "only in hold" : $"{a.Enrichment,10:N1}x";
        var verdict = a.Enrichment >= CalibrationResult.MinimumEnrichment ? "SIGNATURE" : "ordinary";
        Console.WriteLine($"  {a.PayloadLength,6}  {hold,14}  {normal,14}  {factor,12}   {verdict}");
    }

    if (result.ExcludedAsOrdinary.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("The 'ordinary' ones occur about as often normally as during the hold, so");
        Console.WriteLine("they are traffic that slipped through rather than handshake packets.");
        Console.WriteLine("Passing them on length alone would pass game state too.");
    }

    if (result.PooledBlockOpposite.Total > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"most common {result.OppositeLabel} sizes during the hold "
            + "(where a resync trigger lives):");
        foreach (var entry in result.OppositeDuringHold)
        {
            var share = (double)entry.Count / result.PooledBlockOpposite.Total;
            Console.WriteLine($"  {entry.PayloadLength,6} bytes  x{entry.Count,-5} {share,6:P0}");
        }

        Console.WriteLine();
        Console.WriteLine($"{char.ToUpperInvariant(result.OppositeLabel[0])}{result.OppositeLabel[1..]} kept flowing throughout "
            + $"({result.PooledBlockOpposite.Total} packets), as expected for a {result.HeldLabel}-only hold.");
    }

    PrintTimelines(result);
}

/// <summary>Prints one uplink timeline per peer, so a bad window is visible.</summary>
static void PrintTimelines(CalibrationResult result)
{
    foreach (var peer in result.Peers)
    {
        if (peer.Timeline.Count == 0)
        {
            continue;
        }

        Console.WriteLine();
        Console.WriteLine($"{result.HeldLabel} per second - {peer.Address}  ( * = counted as held )");

        var peak = Math.Max(1, peer.Timeline.Max(b => b.Outbound));
        foreach (var bucket in peer.Timeline)
        {
            var bar = new string('#', (int)Math.Round(bucket.Outbound * 40.0 / peak));
            var mark = bucket.InWindow ? '*' : ' ';
            Console.WriteLine($"  {bucket.Second,3}s {mark} {bucket.Outbound,4} {bar}");
        }
    }
}
