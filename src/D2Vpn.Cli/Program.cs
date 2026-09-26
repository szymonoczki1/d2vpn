using D2Vpn.Cli;
using D2Vpn.Core.Calibration;
using D2Vpn.Core.Interception;
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
    Console.WriteLine("  d2vpn watch <group> [--seconds N]");
    Console.WriteLine("      Live packet counts and payload sizes for a group.");
    Console.WriteLine("      Observes only: packets are copied, never diverted.");
    Console.WriteLine();
    Console.WriteLine("  d2vpn calibrate <group> [--seconds N]");
    Console.WriteLine("      Derive this machine's packet-length signature for a group.");
    Console.WriteLine("      Hold an uplink module partway through the run; the hold is found");
    Console.WriteLine("      from the traffic itself, so the timing is up to you. Reports the");
    Console.WriteLine("      payload sizes that crossed while uplink was collapsed - the packets");
    Console.WriteLine("      a module must pass for the session to survive a hold.");
    Console.WriteLine();
    Console.WriteLine("groups:  3074  27k  7500  30k");
    Console.WriteLine();
    Console.WriteLine("Both watch and calibrate need administrator rights.");
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

    if (!Elevation.RequireElevated())
    {
        return 1;
    }

    using var source = new WinDivertPacketSource(group);
    Console.WriteLine($"watching {group} for {seconds}s  (sniff mode, traffic is not modified)");
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

    if (!Elevation.RequireElevated())
    {
        return 1;
    }

    using var source = new WinDivertPacketSource(group);
    var calibrator = new SignatureCalibrator(group);

    Console.WriteLine($"calibrating {group}  ({seconds}s, sniff mode - traffic is not modified)");
    Console.WriteLine($"filter: {source.FilterExpression}");
    Console.WriteLine();
    Console.WriteLine("What to do, in the game window:");
    Console.WriteLine("  1. leave it alone for the first ~10s");
    Console.WriteLine("  2. hold your uplink module for 10-15s");
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

        if (packet.IsOutbound)
        {
            recentOutbound++;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - lastPrint >= TimeSpan.FromSeconds(1))
        {
            // A live uplink rate makes a failed run obvious while it is still running: if this
            // never drops near zero, the module is not engaging.
            var elapsed = (now - started).TotalSeconds;
            Console.Write($"\r{elapsed,5:N0}s / {seconds}s   uplink {recentOutbound,4}/s   ");
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
            Console.WriteLine($"  {peer.Address,-18} {peer.BaselineOutboundRate,5:N1}/s normal   no hold");
            continue;
        }

        var spans = string.Join(" + ", peer.Windows.Select(w =>
            $"{w.Start.TotalSeconds:N0}-{(w.Start + w.Duration).TotalSeconds:N0}s"));

        Console.WriteLine($"  {peer.Address,-18} {peer.BaselineOutboundRate,5:N1}/s normal   "
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
        Console.WriteLine("No uplink collapse on any peer - the module never engaged.");
        Console.WriteLine();
        Console.WriteLine("During a real hold a peer's uplink falls to near zero for several");
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
        + $"{window.OutboundPackets} uplink packets crossed");

    var maxHolds = result.HeldPeers.Max(p => p.Windows.Count);
    if (maxHolds > 1)
    {
        // Worth stating: one keypress does not produce one block.
        Console.WriteLine();
        Console.WriteLine($"This was {maxHolds} separate collapses, not one. Between them uplink");
        Console.WriteLine("returned to full rate - the limiter letting the game re-establish with");
        Console.WriteLine("its peer before blocking again. A hold is block/resync/block, not a");
        Console.WriteLine("single continuous cut.");
    }

    Console.WriteLine();

    Console.WriteLine("YOUR SIGNATURE - uplink payload sizes concentrated in the hold:");
    Console.WriteLine($"  {string.Join(", ", result.OutboundSignature)}");
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

    if (result.PooledBlockInbound.Total > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Downlink kept flowing throughout ({result.PooledBlockInbound.Total} packets), "
            + "as expected for an uplink-only hold.");
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
        Console.WriteLine($"uplink per second - {peer.Address}  ( * = counted as held )");

        var peak = Math.Max(1, peer.Timeline.Max(b => b.Outbound));
        foreach (var bucket in peer.Timeline)
        {
            var bar = new string('#', (int)Math.Round(bucket.Outbound * 40.0 / peak));
            var mark = bucket.InWindow ? '*' : ' ';
            Console.WriteLine($"  {bucket.Second,3}s {mark} {bucket.Outbound,4} {bar}");
        }
    }
}
