using System.Globalization;
using D2Vpn.Cli;
using D2Vpn.Cli.Hotkeys;
using D2Vpn.Core.Input;
using D2Vpn.Core.Calibration;
using D2Vpn.Core.Interception;
using D2Vpn.Core.Modules;
using D2Vpn.Core.Network;

// Every number this prints is a measurement meant to be pasted into a finding, diffed against
// another run, or read back out of a session log. On this machine the ambient culture is pl-PL,
// whose digit group separator is a non-breaking space - so "2048" was printing as "2 048" with a
// byte the console renders as a replacement character, and logs were formatted per-locale. Parsing
// has already been bitten by this once. Formatting is fixed here rather than at 18 call sites.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

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
        "run" => await RunAsync(args),
        "reconnect" or "rc" => await ReconnectAsync(args),
        "pause" => await PauseAsync(args),
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
    Console.WriteLine("                     [--downlink | --full] [--priority N] [--no-resync]");
    Console.WriteLine("                     [--resync-ms N] [--cooldown-ms N] [--timeout-s N]");
    Console.WriteLine("                     [--resync-pass A,B,C] [--no-pass]");
    Console.WriteLine("      --no-resync stops resync windows opening; --no-pass clears the");
    Console.WriteLine("      standing pass list. They are separate: uplink passes its handshake");
    Console.WriteLine("      sizes even with resync off, so both are needed for a total block.");
    Console.WriteLine("      --timeout-s overrides the safety timeout; 0 removes it. Needed to");
    Console.WriteLine("      hold past the game's ~30s ceiling, which is the only way to tell a");
    Console.WriteLine("      working resync from a hold that was simply too short to fail.");
    Console.WriteLine("      Hold one direction on a group: drop it except the signature");
    Console.WriteLine("      sizes, and stop blocking when the peer asks to reconnect, so");
    Console.WriteLine("      the session survives. --dry-run reports what it WOULD drop");
    Console.WriteLine("      without touching traffic - the safe way to try it first.");
    Console.WriteLine();
    Console.WriteLine("  d2vpn run <group> [--downlink | --full] [--key NAME] [--log-passed]");
    Console.WriteLine("                    [--untick-ms N] [--log FILE]");
    Console.WriteLine("                    [--no-buffer] [--buffer-size N] [--buffer-age-s N]");
    Console.WriteLine("                    [--buffer-downlink] [--slow] [--slow-ms N]");
    Console.WriteLine("                    [--slow-fixed] [--slow-burst-ms N]");
    Console.WriteLine("      Run a module until interrupted, toggled by a hotkey. This is the one");
    Console.WriteLine("      to use in play: hold runs on a timer, which is right for measuring");
    Console.WriteLine("      and useless when a desync has to start on the frame you want.");
    Console.WriteLine("      Default key is capslock for uplink, mouse4 for downlink - matching");
    Console.WriteLine("      what the existing AHK scripts already send. Accepts the same");
    Console.WriteLine("      --resync-pass, --resync-ms, --cooldown-ms, --timeout-s, --no-pass");
    Console.WriteLine("      and --no-resync as hold.");
    Console.WriteLine();
    Console.WriteLine("      Blocked uplink is held and replayed on release rather than being");
    Console.WriteLine("      destroyed, because nothing re-sends what the player did: a dropped");
    Console.WriteLine("      shot never happened, while a held one arrives late. Measured at 12");
    Console.WriteLine("      detached shots in 15, against roughly 1 in 6 when they were thrown");
    Console.WriteLine("      away. --no-buffer restores destroying them.");
    Console.WriteLine("      Uplink is ~20 packets/s, so the 2048 default holds ~100s; packets");
    Console.WriteLine("      older than 30s are discarded rather than replayed, since stale");
    Console.WriteLine("      actions are worse than none. --buffer-size and --buffer-age-s change");
    Console.WriteLine("      those two limits.");
    Console.WriteLine("      Only outbound is held, whatever direction the hold covers, so a");
    Console.WriteLine("      downlink run is unaffected. Replaying inbound is unproven: the");
    Console.WriteLine("      server re-sends state regardless, so there is nothing to recover,");
    Console.WriteLine("      and a backlog of stale inbound state is the one direction where");
    Console.WriteLine("      replay could do harm. --buffer-downlink enables it to be measured.");
    Console.WriteLine();
    Console.WriteLine("      --slow is a different mode, not a stronger hold. Nothing is blocked");
    Console.WriteLine("      and nothing is dropped: packets are queued and released late, so the");
    Console.WriteLine("      peer sees a stream that is behind rather than a gap in one. It covers");
    Console.WriteLine("      whichever direction the hold does - a plain 3074 run slows only");
    Console.WriteLine("      uplink - where buffering covers outbound only regardless. The pass");
    Console.WriteLine("      list still passes immediately, so the sizes that keep a session alive");
    Console.WriteLine("      are never held. On untick the whole queue is dumped.");
    Console.WriteLine("");
    Console.WriteLine("      On 3074 it is a RATE CAP: one packet every --slow-ms per peer,");
    Console.WriteLine("      whatever arrives. Input outruns output, the backlog grows, and the");
    Console.WriteLine("      desync deepens the longer you hold - so a larger --slow-ms is more");
    Console.WriteLine("      aggressive, not less. Default 1000 ms, which is what both reference");
    Console.WriteLine("      limiters use and what was measured on the wire.");
    Console.WriteLine("      A fixed offset was tried first and is indistinguishable from latency,");
    Console.WriteLine("      which the game compensates for. --slow-fixed selects it anyway: it is");
    Console.WriteLine("      the right mechanism for 30k, whose slow really is a fixed delay.");
    Console.WriteLine("");
    Console.WriteLine("      --slow-burst-ms (default 10) keeps a burst together under a rate cap:");
    Console.WriteLine("      packets arriving that close are spaced by it rather than by the full");
    Console.WriteLine("      period, so four packets 2 ms apart do not leave four seconds apart.");
    Console.WriteLine("      A rate cap is the one mode that CAN drop: the backlog grows until its");
    Console.WriteLine("      tail reaches --buffer-age-s and then expires, so a held throttle turns");
    Console.WriteLine("      into a partial block after about that long. The summary says how many");
    Console.WriteLine("      expired.");
    Console.WriteLine("");
    Console.WriteLine("      --log records holds, releases and outcomes with timestamps, so a");
    Console.WriteLine("      run can be lined up against a capture. Mark each attempt with f7");
    Console.WriteLine("      (worked) or f8 (failed) - labelled trials are the only way to find");
    Console.WriteLine("      a pattern that is not visible in play. --log-passed prints every");
    Console.WriteLine("      packet that crosses while holding, for working out what a leak is");
    Console.WriteLine("      carrying.");
    Console.WriteLine();
    Console.WriteLine("      --untick-ms keeps blocking after the key is released. Off by");
    Console.WriteLine("      default, and measured to make bullet detach strictly worse - every");
    Console.WriteLine("      attempt failed with it on. Kept because the result is informative,");
    Console.WriteLine("      not because it is worth using.");
    Console.WriteLine();
    Console.WriteLine("  d2vpn run 7500|30k [--key NAME] [--log FILE] [--slow] [--slow-s N]");
    Console.WriteLine("                 [--rc | --rc-key NAME] [--no-buffer] [--capture-slice]");
    Console.WriteLine("                 [--safe | --max-hold-s N] [--quiet-hold-s N]");
    Console.WriteLine("                 [--capture-quiet-ms N] [--capture-bytes N] [--no-resync]");
    Console.WriteLine("      A different mechanism from the UDP groups, because these are TCP.");
    Console.WriteLine("      Dropping a TCP segment does not destroy it - the sender re-sends the");
    Console.WriteLine("      same data under the same sequence number - so a block that only drops");
    Console.WriteLine("      leaks about 200 ms later. This withholds inbound data segments and");
    Console.WriteLine("      keeps suppressing their retransmissions for as long as the hold runs.");
    Console.WriteLine("");
    Console.WriteLine("      On 30k a 73-byte heartbeat arrives every ~3s and is passed through,");
    Console.WriteLine("      which is what lets a hold run indefinitely - blocking it freezes the");
    Console.WriteLine("      acknowledgement and the game gives up after about twenty seconds,");
    Console.WriteLine("      sending FIN and reconnecting. --no-resync stops passing it, to see");
    Console.WriteLine("      that happen; --pass A,B,C overrides the size if this machine's");
    Console.WriteLine("      heartbeat differs. Watch the keepalive count: zero while holding 30k");
    Console.WriteLine("      means the list is wrong and the connection has ~20s left.");
    Console.WriteLine("      Under --slow the pass is likely counterproductive: a passed packet");
    Console.WriteLine("      cannot move ahead of a hole already in front of it, so it waits for");
    Console.WriteLine("      the withheld content anyway and turns a smooth delay into a sawtooth");
    Console.WriteLine("      of gaps. Try --slow --no-keepalive to delay everything uniformly.");
    Console.WriteLine("      --no-keepalive and --no-resync are the same flag; the second matches");
    Console.WriteLine("      what it is called on 3074 and has nothing to do with --rc.");
    Console.WriteLine("");
    Console.WriteLine("      --slow delays inbound instead of stopping it: every segment is still");
    Console.WriteLine("      withheld and its retransmissions still suppressed, but each goes out");
    Console.WriteLine("      once it has waited --slow-s, so the stream runs permanently behind");
    Console.WriteLine("      rather than halting. 6s on 30k, which is what the reference limiter");
    Console.WriteLine("      states for its own 30k slow and is consistent with gaps of 3.91s and");
    Console.WriteLine("      5.94s measured while it ran.");
    Console.WriteLine("");
    Console.WriteLine("      30k without --slow blocks inbound until you untick, matching that");
    Console.WriteLine("      tool's 30k. It used to route to the UDP-style hold, which suppresses");
    Console.WriteLine("      nothing and so leaked at the first retransmission about 200 ms in,");
    Console.WriteLine("      and it blocked the wrong direction. Neither was ever tested.");
    Console.WriteLine("");
    Console.WriteLine("      Inbound only. --downlink and --full are refused: blocking outbound");
    Console.WriteLine("      stops the acknowledgements this machine owes, which makes the peer");
    Console.WriteLine("      retransmit harder and reset sooner for no gain.");
    Console.WriteLine("");
    Console.WriteLine("      The failure mode here is a RESET and an error code, not a slow");
    Console.WriteLine("      timeout - there is no resync keeping the connection alive the way");
    Console.WriteLine("      there is on 3074. Watch the retransmits-suppressed count: across a");
    Console.WriteLine("      hold longer than about five seconds, withholding segments while");
    Console.WriteLine("      suppressing none means the hold leaked, and the totals will not say");
    Console.WriteLine("      so. Under five seconds, zero is normal - the peer has not tried again");
    Console.WriteLine("      yet.");
    Console.WriteLine("");
    Console.WriteLine("      Every inbound data segment is withheld for the whole hold.");
    Console.WriteLine("      --capture-slice switches to the reference implementation's two-phase");
    Console.WriteLine("      behaviour instead: withhold until the sender goes quiet for");
    Console.WriteLine("      --capture-quiet-ms, then hold only that slice and pass later data.");
    Console.WriteLine("      That assumes a sender which bursts then stalls, and measured here");
    Console.WriteLine("      7500 carries ~2.3 packets/s - so gaps over 100 ms are just the");
    Console.WriteLine("      spacing between updates, the capture fires after the first segment,");
    Console.WriteLine("      and seven consecutive holds each withheld exactly one.");
    Console.WriteLine("");
    Console.WriteLine("      Withheld segments are buffered and replayed - on untick for a block,");
    Console.WriteLine("      on schedule for --slow. --no-buffer destroys them instead, in which");
    Console.WriteLine("      case the data only arrives when the peer next retransmits, and every");
    Console.WriteLine("      suppression doubles that wait - so after a few seconds of holding the");
    Console.WriteLine("      peer is backed off to around a second, felt as a delay before anything");
    Console.WriteLine("      changes in game. It is refused with --slow, where dropping the queue");
    Console.WriteLine("      would leave a block rather than a delay.");
    Console.WriteLine("      Replayed segments have their ack numbers brought forward and checksums");
    Console.WriteLine("      recalculated, since one held for a minute acknowledges a position long");
    Console.WriteLine("      since passed; measured, all three replayed segments in a 61s hold");
    Console.WriteLine("      needed it.");
    Console.WriteLine("");
    Console.WriteLine("      The hold is UNCAPPED by default and ends only when you untick or the");
    Console.WriteLine("      peer resets. The effect lasts as long as the hold does - the game is");
    Console.WriteLine("      deprived of API data for every second it runs - so how long to hold");
    Console.WriteLine("      is your call rather than a number this tool should pick.");
    Console.WriteLine("");
    Console.WriteLine("      --safe caps it at 60s. Measured on this machine a 75s hold released");
    Console.WriteLine("      cleanly and an 86s hold was reset, so 60 leaves margin. --max-hold-s");
    Console.WriteLine("      sets any other length. --quiet-hold-s additionally ends a hold once");
    Console.WriteLine("      the peer has stopped retransmitting for --quiet-resent-s (24); that");
    Console.WriteLine("      happens about 25s in, and it stops the counters moving rather than");
    Console.WriteLine("      stopping the effect, so it is off unless you ask for it.");
    Console.WriteLine("");
    Console.WriteLine("      --rc adds a second keybind, f9 by default, that injects a reset into");
    Console.WriteLine("      every live connection on this group - the same thing 'd2vpn rc' does,");
    Console.WriteLine("      on the same handle. They stay independent: the hold is still on");
    Console.WriteLine("      capslock and either can be used without the other. 30k and a");
    Console.WriteLine("      reconnect go together, since resetting and then holding the new");
    Console.WriteLine("      connection delays the world state coming back, and running them as");
    Console.WriteLine("      two processes would mean two diverting handles on one filter.");
    Console.WriteLine("      --rc-key sets the key, --idle-s (10) how long a silent connection");
    Console.WriteLine("      still counts as resettable.");
    Console.WriteLine("");
    Console.WriteLine("      An inbound RST always disables the module. That is not a precaution,");
    Console.WriteLine("      it is the connection already being gone.");
    Console.WriteLine("");
    Console.WriteLine("      The capture values are inherited from a reference implementation and");
    Console.WriteLine("      neither has been reachable here: the channel is too quiet to trigger");
    Console.WriteLine("      --capture-quiet-ms, and --capture-bytes (1460) is never reached");
    Console.WriteLine("      because segments are 21-278 bytes. They are exposed to be calibrated,");
    Console.WriteLine("      not because the defaults are known to be right.");
    Console.WriteLine("");
    Console.WriteLine("  d2vpn reconnect [group] [--key NAME] [--idle-s N] [--log FILE]");
    Console.WriteLine("      Also 'rc'. The one command here that is not a hold: nothing is");
    Console.WriteLine("      blocked, traffic passes untouched, and one keypress injects a TCP");
    Console.WriteLine("      reset into every live connection on the group. The game notices at");
    Console.WriteLine("      once, error codes, and reconnects by itself.");
    Console.WriteLine("");
    Console.WriteLine("      Two resets go out per connection, built from packets it recently");
    Console.WriteLine("      sent. One goes to the peer, saying this machine is finished; one is");
    Console.WriteLine("      delivered up this machine's own stack, saying the peer is. Killing");
    Console.WriteLine("      one end leaves the other believing the connection is alive.");
    Console.WriteLine("");
    Console.WriteLine("      Defaults to 30k, where a capture here showed the game opening a new");
    Console.WriteLine("      connection 330 ms later, and a reference limiter reports the effect");
    Console.WriteLine("      lasting ~6s solo and ~40s in a fireteam. Its uses per that tool:");
    Console.WriteLine("      change public instance, reload world state, pull to fireteam leader.");
    Console.WriteLine("");
    Console.WriteLine("      A connection can only be reset once a packet carrying payload has");
    Console.WriteLine("      been seen in BOTH directions, so give it a few seconds of traffic");
    Console.WriteLine("      first. The live count on the status line says when a press will do");
    Console.WriteLine("      something. --idle-s (10) is how long a silent connection still");
    Console.WriteLine("      counts as live. Trigger defaults to f9.");
    Console.WriteLine("");
    Console.WriteLine("  d2vpn pause [--process NAME] [--key NAME] [--timeout-s N] [--log FILE]");
    Console.WriteLine("      Freezes the game itself rather than its traffic. Suspends every");
    Console.WriteLine("      thread in the process, so nothing is sent, received, drawn or");
    Console.WriteLine("      simulated until you press the key again. To the peers it looks");
    Console.WriteLine("      like an uplink and downlink hold at once.");
    Console.WriteLine("");
    Console.WriteLine("      This is the one command with a safety limit ON by default (30s).");
    Console.WriteLine("      A traffic hold that goes wrong ends in an error code and the game");
    Console.WriteLine("      reconnects itself; a suspend that goes wrong ends in a process");
    Console.WriteLine("      that will never run again. Ctrl+C resumes on the way out, but a");
    Console.WriteLine("      kill from Task Manager has no cleanup path - which is what the");
    Console.WriteLine("      limit is for. --timeout-s 0 removes it, with a warning.");
    Console.WriteLine("");
    Console.WriteLine("      --process defaults to destiny2. Needs no WinDivert handle, so it");
    Console.WriteLine("      does not conflict with another limiter, but the game's anti-cheat");
    Console.WriteLine("      may refuse the handle outright - if it does, this cannot work.");
    Console.WriteLine("");
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

/// <summary>
/// Reads a comma-separated set of payload sizes, for experimenting with a resync pass list.
/// </summary>
/// <remarks>
/// Exposed because the handshake for a direction is found by trying candidates against a live
/// game and seeing whether the session survives. Uplink's is not established: the sizes seen
/// crossing its resync windows crossed because the window opens fully, which says what flowed
/// through an open gate rather than what the exchange requires.
/// </remarks>
static IReadOnlySet<int>? LengthSetOption(string[] args, string name)
{
    var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    if (i < 0 || i + 1 >= args.Length)
    {
        return null;
    }

    var parts = args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var set = new HashSet<int>();

    foreach (var part in parts)
    {
        if (!int.TryParse(part, out var length) || length < 0)
        {
            throw new ArgumentException($"{name} takes payload sizes, e.g. --resync-pass 51,233");
        }

        set.Add(length);
    }

    return set.Count > 0
        ? set
        : throw new ArgumentException($"{name} needs at least one size.");
}

static bool HasFlag(string[] args, string name) =>
    args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

static int IntOption(string[] args, string name, int fallback, bool allowZero = false)
{
    var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    if (i < 0 || i + 1 >= args.Length)
    {
        return fallback;
    }

    if (!int.TryParse(args[i + 1], out var value) || value < 0 || (value == 0 && !allowZero))
    {
        throw new ArgumentException(
            $"{name} needs a {(allowZero ? "non-negative" : "positive")} whole number.");
    }

    return value;
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
    var full = HasFlag(args, "--full");
    var noResync = HasFlag(args, "--no-resync");
    var noPass = HasFlag(args, "--no-pass");

    if (full && downlink)
    {
        throw new ArgumentException("--full already covers both directions; drop --downlink.");
    }
    var timeoutSeconds = IntOption(args, "--timeout-s", -1, allowZero: true);
    var resyncPass = LengthSetOption(args, "--resync-pass");
    var resyncMs = IntOption(args, "--resync-ms", 0);
    var cooldownMs = IntOption(args, "--cooldown-ms", 0);
    var priority = PriorityOption(args);

    var heldDirection = full
        ? HoldDirection.Both
        : downlink ? HoldDirection.Downlink : HoldDirection.Uplink;
    var options = MeasuredDefaults.For(group, heldDirection);
    if (options is null)
    {
        var what = full ? "full" : downlink ? "downlink" : "uplink";
        throw new ArgumentException(
            $"No {what} signature has been measured for {group}. "
            + $"Run 'd2vpn calibrate {group.Name}{(downlink ? " --downlink" : string.Empty)}' first.");
    }

    // The window and the cooldown are the two knobs that decide whether a hold leaks. They are
    // exposed rather than guessed at: a window long enough to complete a handshake but short
    // enough not to pass game state is a property of this game on this connection, and the only
    // way to find it is to try values and watch what reaches the character.
    if (timeoutSeconds >= 0)
    {
        // The safety timeout exists because a direction's resync is unproven - which makes it the
        // thing standing between here and proving it. Holding past the game's ~30 s ceiling is the
        // only test that distinguishes a working resync from a hold that was simply too short.
        options = options with
        {
            SafetyTimeout = timeoutSeconds == 0 ? null : TimeSpan.FromSeconds(timeoutSeconds)
        };

        Console.WriteLine(timeoutSeconds == 0
            ? "SAFETY TIMEOUT DISABLED - the hold runs for the full duration."
            : $"Safety timeout overridden to {timeoutSeconds}s.");
        Console.WriteLine("If the resync is not working the session dies at about 30s. Ctrl+C");
        Console.WriteLine("releases immediately.");
        Console.WriteLine();
    }

    if (resyncPass is not null)
    {
        options = options with { ResyncPassLengths = resyncPass };
        Console.WriteLine($"Resync will pass only: {string.Join(", ", resyncPass.Order())}");
        Console.WriteLine("Everything else stays blocked even while a window is open. If the");
        Console.WriteLine("session dies at ~30s, these are not the whole handshake.");
        Console.WriteLine();
    }

    if (resyncMs > 0)
    {
        options = options with { ResyncDuration = TimeSpan.FromMilliseconds(resyncMs) };
    }

    if (cooldownMs > 0)
    {
        options = options with { ResyncCooldown = TimeSpan.FromMilliseconds(cooldownMs) };
    }

    if (noPass)
    {
        // The standing pass list is separate from the resync. Uplink keeps passing its six
        // handshake sizes even with resync disabled, so --no-resync alone is not a total block.
        options = options with { PassLengths = new HashSet<int>() };
        Console.WriteLine("PASS LIST CLEARED - nothing crosses outside a resync window.");
        Console.WriteLine();
    }

    if (noResync)
    {
        // Isolates the block from the resync. A total block should defer everything, at the cost
        // of dying once the game's ~30 s timeout runs out.
        options = options with { ResyncTriggerLengths = new HashSet<int>() };
        Console.WriteLine("RESYNC DISABLED - this blocks outright with nothing let through.");
        Console.WriteLine("Expect the session to die around 30s if the safety timeout does not");
        Console.WriteLine("end the hold first. Use it to see what the block alone does.");
        Console.WriteLine();
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
        Console.WriteLine("NOTE: nothing is passed outside a resync window, so this blocks outright.");
        Console.WriteLine(full
            ? "For a full hold that is deliberate: one pass list cannot serve both directions,"
            : "For 3074 downlink that is deliberate - measurement showed nothing needs to cross,");
        Console.WriteLine(full
            ? "since each needs a different one. Elsewhere it means no calibration has been done."
            : "and the handshake crosses during a resync instead.");
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
        + $"  (open {options.ResyncDuration.TotalSeconds:N2}s, then blocked "
        + $"{options.ResyncCooldown.TotalSeconds:N1}s minimum)");
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

    // The corroborating signal lives in the direction that is NOT held. Blocking uplink makes the
    // peer probe harder on downlink; blocking downlink makes the game retry harder on uplink.
    // Watching the held direction measures the one stream guaranteed not to move - this tool sees
    // every packet before dropping it, so its own counters never fall.
    long warmupOpposite = 0;
    long warmupHeld = 0;
    var engaged = false;

    while (!run.IsCompleted)
    {
        await Task.WhenAny(run, Task.Delay(1000, CancellationToken.None));
        var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;

        if (!engaged && elapsed >= warmup)
        {
            engaged = true;
            warmupOpposite = downlink ? controller.OutboundSeen : controller.InboundSeen;
            warmupHeld = full
                ? controller.InboundSeen + controller.OutboundSeen
                : downlink ? controller.InboundSeen : controller.OutboundSeen;
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

    var heldDropped = full
        ? controller.InboundDropped + controller.OutboundDropped
        : downlink ? controller.InboundDropped : controller.OutboundDropped;
    var heldSeen = full
        ? controller.InboundSeen + controller.OutboundSeen
        : downlink ? controller.InboundSeen : controller.OutboundSeen;
    var heldDuringHold = heldSeen - warmupHeld;
    if (heldDuringHold > 0)
    {
        // Warmup passes everything, so a whole-run percentage understates how tight the hold was.
        var tightness = heldDropped / (double)heldDuringHold;
        Console.WriteLine($"blocked while holding   : {tightness,5:P0} of {heldDuringHold} packets");
        if (tightness < 0.9)
        {
            Console.WriteLine();
            Console.WriteLine("Anything short of near-total leaks: a single packet is enough to carry");
            Console.WriteLine("a pickup or a buff through. Try --resync-ms lower or --cooldown-ms");
            Console.WriteLine("higher, and --no-resync to see what a perfect block looks like.");
        }
    }

    // Two independent checks that the block reached the game, neither needing anything observed
    // in play. The game asking to reconnect means it noticed a peer went quiet; the peer sending
    // harder than usual means it noticed us. Either one alone is weak; together they settle it.
    var holdSeconds = Math.Max(1, seconds - warmup);
    var oppositeSeen = downlink ? controller.OutboundSeen : controller.InboundSeen;
    var oppositeLabel = downlink ? "uplink" : "downlink";
    var baselineRate = warmup > 0 ? warmupOpposite / (double)warmup : 0;
    var holdRate = (oppositeSeen - warmupOpposite) / (double)holdSeconds;

    if (!full)
    {
        Console.WriteLine();
        Console.WriteLine($"{oppositeLabel} while idle     : {baselineRate,5:N1}/s");
        Console.WriteLine($"{oppositeLabel} while holding  : {holdRate,5:N1}/s");
    }
    else
    {
        // Both directions are held, so there is no unheld one to read a reaction from. Reconnect
        // requests are the only signal left, and they are the reliable one anyway.
        baselineRate = 0;
    }

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
            Console.WriteLine(downlink
                ? "  - the game sent harder than usual, retrying to reach its peers"
                : "  - the peer sent harder than usual, probing for us");
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

/// <summary>
/// Resolves a module's options from the command line, shared by hold and run.
/// </summary>
static async Task<int> ReconnectAsync(string[] args)
{
    // 30k by default, which is where the reference implementation does this and where a reset was
    // measured recovering in 330 ms. The group is still selectable: the mechanism is not specific
    // to a port, only to TCP.
    var group = args.Length > 1 && !args[1].StartsWith('-')
        ? ResolveGroup(args)
        : DestinyPorts.Instance;

    if (group.Protocol != PortProtocol.Tcp)
    {
        throw new ArgumentException(
            $"{group.Name} is UDP, and a reset is a TCP mechanism. There is no connection to tear "
            + "down, so there would be nothing for the peer to act on. Use 30k or 7500.");
    }

    var triggerKey = KeyName.Parse(StringOption(args, "--key") ?? "f9");
    var priority = PriorityOption(args, (short)0);

    if (!Elevation.RequireElevated())
    {
        return 1;
    }

    Console.WriteLine("This injects packets. Nothing is blocked, but a reset really does kill");
    Console.WriteLine("the connection it is aimed at.");
    Console.WriteLine();

    using var cts = new CancellationTokenSource();
    using var sigint = SetupInterrupt(cts);

    return await ReconnectCommand.ExecuteAsync(
        group, triggerKey, priority,
        IntOption(args, "--poll-ms", 1),
        IntOption(args, "--idle-s", 10),
        StringOption(args, "--log"),
        cts.Token).ConfigureAwait(false);
}

static async Task<int> RunApiAsync(string[] args, PortGroup group)
{
    if (HasFlag(args, "--downlink") || HasFlag(args, "--full"))
    {
        throw new ArgumentException(
            $"{group.Name} blocks inbound only, so --downlink and --full do not apply. Blocking "
            + "outbound "
            + "would stop the acknowledgements this machine owes, which makes the peer retransmit "
            + "harder and reset sooner.");
    }

    // Per group, because the two differ in what they are for rather than in tuning. 7500 holds
    // until told otherwise; 30k has a stated delay-line variant at six seconds.
    var slowSeconds = IntOption(args, "--slow-s", 0);
    var slow = HasFlag(args, "--slow") || slowSeconds > 0;

    var defaults = group == DestinyPorts.Instance
        ? slow ? ApiBlockOptions.Instance30kSlow : ApiBlockOptions.Instance30k
        : ApiBlockOptions.Api7500;

    var options = defaults with
    {
        // Both measured and both on. --buffer and --hold-all are still accepted, and now say
        // what is already happening rather than changing it.
        Buffering = !HasFlag(args, "--no-buffer"),
        HoldEverything = !HasFlag(args, "--capture-slice"),
        CaptureQuiet = TimeSpan.FromMilliseconds(
            IntOption(args, "--capture-quiet-ms", (int)ApiBlockOptions.Api7500.CaptureQuiet.TotalMilliseconds)),
        CaptureBytes = IntOption(args, "--capture-bytes", defaults.CaptureBytes),

        // The keepalive. --no-resync clears it, which on 30k means the hold dies in about twenty
        // seconds; --pass overrides it for a machine whose heartbeat is a different size.
        // --no-keepalive is the clearer name, since --rc on this command is a reconnect and
        // "resync" reads like it would disable that. --no-resync stays because it means the same
        // thing on 3074, where it clears what keeps a hold alive.
        PassLengths = HasFlag(args, "--no-keepalive") || HasFlag(args, "--no-resync")
            ? new HashSet<int>()
            : LengthSetOption(args, "--pass") ?? defaults.PassLengths,
        ReleaseAfter = slow
            ? TimeSpan.FromSeconds(
                slowSeconds > 0 ? slowSeconds : (defaults.ReleaseAfter ?? TimeSpan.FromSeconds(6)).TotalSeconds)
            : null,
        // Uncapped unless asked otherwise. The effect lasts as long as the hold does, so how
        // long to hold is the operator's call - the only automatic exit is a reset, which is the
        // connection already being gone rather than a precaution.
        MaxHold = TimeSpan.FromSeconds(IntOption(args, "--max-hold-s", 0, allowZero: true)),
        QuietHold = TimeSpan.FromSeconds(IntOption(args, "--quiet-hold-s", 0, allowZero: true)),
        QuietRetransmit = TimeSpan.FromSeconds(
            IntOption(args, "--quiet-resent-s",
                (int)ApiBlockOptions.Api7500.QuietRetransmit.TotalSeconds)),
    };

    // Without the queue there is nothing to release late, so this would quietly be a block rather
    // than a delay - the opposite of what --slow was asked for.
    if (slow && !options.Buffering)
    {
        throw new ArgumentException(
            "--slow needs the buffer: a segment cannot be delivered late unless it is kept. "
            + "Without it this blocks rather than delays. Drop --no-buffer.");
    }

    // A ceiling below where the error code was measured, for anyone who would rather not find
    // out. Per group, because the two are nowhere near each other: 7500 survived 75 s and was
    // reset at 86.5 s, while 30k error codes at around 18 s.
    if (HasFlag(args, "--safe") && options.MaxHold <= TimeSpan.Zero)
    {
        options = options with
        {
            MaxHold = group == DestinyPorts.Instance
                ? TimeSpan.FromSeconds(14)
                : TimeSpan.FromSeconds(60)
        };
    }

    var toggleKey = KeyName.Parse(StringOption(args, "--key") ?? "capslock");

    // Optional second keybind on the same handle. 30k and a reconnect are used together - reset
    // the connection, then hold the new one to delay the world state coming back - and running
    // them as two processes would mean two diverting handles on the same filter, which is exactly
    // what this tool warns against.
    var wantsReconnect = HasFlag(args, "--rc") || StringOption(args, "--rc-key") is not null;
    int? reconnectKey = wantsReconnect
        ? KeyName.Parse(StringOption(args, "--rc-key") ?? "f9")
        : null;

    // Inbound, so the same priority a 3074 downlink hold needs to see its traffic at all.
    var priority = PriorityOption(args, (short)-1000);

    if (!Elevation.RequireElevated())
    {
        return 1;
    }

    Console.WriteLine("DIVERT MODE - packets really are dropped while holding.");
    Console.WriteLine("Any other limiter must be fully EXITED, not just idle.");
    Console.WriteLine();

    using var cts = new CancellationTokenSource();
    using var sigint = SetupInterrupt(cts);

    return await ApiRunCommand.ExecuteAsync(
        group, options, toggleKey, priority,
        IntOption(args, "--poll-ms", 1),
        StringOption(args, "--log"),
        reconnectKey,
        IntOption(args, "--idle-s", 10),
        cts.Token).ConfigureAwait(false);
}

static HoldModuleOptions ResolveOptions(string[] args, PortGroup group, bool downlink, bool full)
{
    var heldDirection = full
        ? HoldDirection.Both
        : downlink ? HoldDirection.Downlink : HoldDirection.Uplink;

    var options = MeasuredDefaults.For(group, heldDirection);
    if (options is null)
    {
        var what = full ? "full" : downlink ? "downlink" : "uplink";
        throw new ArgumentException(
            $"No {what} signature has been measured for {group}. "
            + $"Run 'd2vpn calibrate {group.Name}{(downlink ? " --downlink" : string.Empty)}' first.");
    }

    if (LengthSetOption(args, "--resync-pass") is { } resyncPass)
    {
        options = options with { ResyncPassLengths = resyncPass };
    }

    var resyncMs = IntOption(args, "--resync-ms", 0);
    if (resyncMs > 0)
    {
        options = options with { ResyncDuration = TimeSpan.FromMilliseconds(resyncMs) };
    }

    var cooldownMs = IntOption(args, "--cooldown-ms", 0);
    if (cooldownMs > 0)
    {
        options = options with { ResyncCooldown = TimeSpan.FromMilliseconds(cooldownMs) };
    }

    var timeoutSeconds = IntOption(args, "--timeout-s", -1, allowZero: true);
    if (timeoutSeconds >= 0)
    {
        options = options with
        {
            SafetyTimeout = timeoutSeconds == 0 ? null : TimeSpan.FromSeconds(timeoutSeconds)
        };
    }

    // On by default, because that is where it was measured: 12 detached shots in 15 against
    // roughly 1 in 6 when blocked packets were destroyed (Finding 36). This covers outbound only,
    // whatever direction the hold itself covers, so a downlink-only run is unaffected by it -
    // replaying inbound is unproven, the server re-sends state regardless, and a backlog of stale
    // inbound state is the one direction where replay could do harm rather than nothing.
    var slowMs = IntOption(args, "--slow-ms", 0);
    var slow = HasFlag(args, "--slow") || HasFlag(args, "--slow-throttle")
        || HasFlag(args, "--slow-fixed") || slowMs > 0;

    // Each group's measured default decides which kind of slow it means - a rate cap on 3074, a
    // fixed offset on 30k - because they are different mechanisms rather than settings of one.
    // The flags override it either way.
    var throttle = HasFlag(args, "--slow-fixed") ? false
        : HasFlag(args, "--slow-throttle") || options.SlowThrottle;

    if (HasFlag(args, "--slow-fixed") && HasFlag(args, "--slow-throttle"))
    {
        throw new ArgumentException("--slow-fixed and --slow-throttle are opposites; pick one.");
    }

    options = options with
    {
        Buffering = !HasFlag(args, "--no-buffer"),
        BufferInbound = HasFlag(args, "--buffer-downlink"),
        BufferCapacity = IntOption(args, "--buffer-size", options.BufferCapacity),
        BufferMaxAge = TimeSpan.FromSeconds(
            IntOption(args, "--buffer-age-s", (int)options.BufferMaxAge.TotalSeconds)),
        SlowMode = slow,
        SlowDelay = slowMs > 0 ? TimeSpan.FromMilliseconds(slowMs) : options.SlowDelay,
        SlowThrottle = throttle,
        SlowBurstWindow = TimeSpan.FromMilliseconds(
            IntOption(args, "--slow-burst-ms", (int)options.SlowBurstWindow.TotalMilliseconds)),
    };

    if (slow && HasFlag(args, "--no-buffer"))
    {
        throw new ArgumentException(
            "--slow needs the buffer: a packet cannot be delivered late unless it is kept. "
            + "Drop --no-buffer.");
    }

    if (options.SlowMode && options.SlowDelay > options.BufferMaxAge)
    {
        throw new ArgumentException(
            $"--slow-ms {options.SlowDelay.TotalMilliseconds:N0} is longer than the "
            + $"{options.BufferMaxAge.TotalSeconds:N0}s buffer age limit, so every delayed packet "
            + "would expire before it was due. Raise --buffer-age-s or lower --slow-ms.");
    }

    if (HasFlag(args, "--no-pass"))
    {
        options = options with { PassLengths = new HashSet<int>() };
    }

    if (HasFlag(args, "--no-resync"))
    {
        options = options with { ResyncTriggerLengths = new HashSet<int>() };
    }

    return options;
}

static async Task<int> RunAsync(string[] args)
{
    var group = ResolveGroup(args);

    // Every TCP group, not just 7500. A TCP hold is a different mechanism rather than a
    // differently configured one - it works by suppressing retransmissions, has no resync, and
    // ends in a reset - so it gets its own module and its own command. 30k used to route to the
    // UDP-style hold, which leaks at the first retransmission, roughly 200 ms in.
    if (group.Protocol == PortProtocol.Tcp)
    {
        return await RunApiAsync(args, group).ConfigureAwait(false);
    }

    var downlink = HasFlag(args, "--downlink");
    var full = HasFlag(args, "--full");

    if (full && downlink)
    {
        throw new ArgumentException("--full already covers both directions; drop --downlink.");
    }

    var keyName = StringOption(args, "--key") ?? (downlink ? "mouse4" : "capslock");
    var toggleKey = KeyName.Parse(keyName);
    var priority = PriorityOption(args, downlink ? (short)-1000 : (short)0);
    var options = ResolveOptions(args, group, downlink, full);

    if (!Elevation.RequireElevated())
    {
        return 1;
    }

    Console.WriteLine("DIVERT MODE - packets really are dropped while holding.");
    Console.WriteLine("Any other limiter must be fully EXITED, not just idle.");
    Console.WriteLine();

    using var cts = new CancellationTokenSource();
    using var sigint = SetupInterrupt(cts);

    return await RunCommand.ExecuteAsync(
        group, options, toggleKey, priority, HasFlag(args, "--log-passed"),
        IntOption(args, "--poll-ms", 1),
        IntOption(args, "--untick-ms", 0, allowZero: true),
        StringOption(args, "--log"),
        KeyName.Parse(StringOption(args, "--good-key") ?? "f7"),
        KeyName.Parse(StringOption(args, "--bad-key") ?? "f8"),
        cts.Token);
}

static async Task<int> PauseAsync(string[] args)
{
    var processName = StringOption(args, "--process") ?? "destiny2";
    var toggleKey = KeyName.Parse(StringOption(args, "--key") ?? "f10");

    // Zero means no limit, so it has to be distinguishable from "not supplied".
    var timeoutSeconds = IntOption(args, "--timeout-s", 30, allowZero: true);
    var safetyTimeout = timeoutSeconds > 0
        ? TimeSpan.FromSeconds(timeoutSeconds)
        : (TimeSpan?)null;

    // No WinDivert handle is opened, so no elevation is required for our sake. Opening the game
    // for suspending may still need it, and NtProcessControl.Open says so if it fails.
    using var cts = new CancellationTokenSource();
    using var sigint = SetupInterrupt(cts);

    return await PauseCommand.ExecuteAsync(
        processName,
        toggleKey,
        IntOption(args, "--poll-ms", 1),
        safetyTimeout,
        StringOption(args, "--log"),
        cts.Token);
}

static string? StringOption(string[] args, string name)
{
    var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
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
