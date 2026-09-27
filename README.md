# d2vpn

Network traffic control and macro management for Destiny 2, on Windows.

Packet interception through the WinDivert kernel driver, with per-module control over specific
port groups. Built on measurements taken from live traffic rather than on inherited constants -
see [docs/protocol.md](docs/protocol.md).

> **Status: both 3074 modules work.** Uplink and downlink are each validated end to end against a
> live game - 96% of traffic blocked over a 60 second hold, twice the ~30 s ceiling a hold without
> a working resync dies at, with the in-game effect confirmed both ways: a downlink hold defers an
> interaction until release, and an uplink hold stops the server learning about a kill. The other
> three port groups are unmeasured. What follows describes what exists today, not what is
> planned.

## Why calibration is the centrepiece

Every limiter in this space works the same way: during a block, drop the game-state payload but
let the connection-keepalive handshake through. Block everything instead and the peer sees a dead
connection, so the game disconnects you.

Which packets count as "handshake" is identified by **payload size**. The traffic is encrypted, so
size and timing are the only signal available.

The existing tools hardcode those sizes. Measurement on one machine found that of five constants
baked into one reference implementation, **two matched exactly and three did not** - packet sizes
shift with game version, loadout, ornament and catalyst. A constant that is correct for its author
is wrong for everyone else, and it fails silently.

So d2vpn derives them - and it finds the hold in the traffic rather than asking you to match a
countdown:

```
> d2vpn calibrate 3074 --seconds 45

   45s / 45s   uplink    0/s

=== 3074 (UDP 3074) ===
normal uplink rate: 10.0 packets/s (79 packets before the hold)
hold detected     : 9s -> 19s (10s)
uplink while held : 0.7 packets/s (7% of normal, 7 packets total)

YOUR SIGNATURE - uplink payload sizes that crossed during the hold:
  51, 128, 136, 190, 198, 1166

These are the packets a module must let through for the session to survive
a hold. Blocking them too would make the game disconnect.

None of these sizes occur in normal traffic, so a length match cannot
confuse them with game state. Selective pass-through confirmed.

Downlink kept flowing throughout (150 packets), as expected for an uplink-only hold.
```

**The hold is detected from the uplink rate collapsing, not from a timer.** An earlier design
asked the operator to hold during a fixed window and then differenced the two halves. It does not
work: reaction time smears the boundary, and payload sizes step in small increments because
encrypted payloads pad, so a short baseline misses many ordinary sizes and reports them as
signature. One real run produced a 105-entry "signature" reading 1193, 1194, 1195, 1196 - every
one an ordinary data packet. Uplink collapsing from 10/s to 0.7/s is unambiguous by comparison,
and inside a genuine hold only a handful of packets cross at all, so every one of them is signal.

Calibration runs in WinDivert's **sniff** mode: packets are copied and continue up the stack
untouched, so it cannot stall or break a live game.

## Commands

| Command | What it does |
|---|---|
| `d2vpn list` | Show the known port groups |
| `d2vpn watch <group> [--seconds N]` | Live packet counts and a payload-size histogram |
| `d2vpn calibrate <group> [--seconds N] [--downlink]` | Derive this machine's signature for a group, in either direction |
| `d2vpn hold <group> [--seconds N] [--dry-run] [--downlink]` | Hold a group in one direction for a fixed time. `--dry-run` reports what it would drop without touching traffic |
| `d2vpn run <group> [--key K] [--downlink] [--full] [--slow] [--no-buffer]` | Hold a group on a hotkey, until Ctrl+C. This is the one to actually play with |

Groups: `3074`, `27k`, `7500`, `30k`. Both `watch` and `calibrate` need an elevated terminal,
because loading a kernel driver does.

## Holding uplink is a cycle, not a switch

The obvious design - drop uplink while a key is held, pass a list of handshake sizes so the peer
does not give up - **caps out at about 30 seconds**, and it took an experiment to find out why.

Running the same 20-second hold with a reference limiter's `AutoResync` turned off, one reconnect
request repeated at 3-4 packets/second for the hold's entire length: the game asking to
re-establish, over and over, because nothing ever answered. Turn `AutoResync` back on and those
retries nearly vanish. It is not a filter. It **answers** the exchange and drives it to
completion.

So a module is a per-peer state machine:

```
Blocking   ── saw an outbound reconnect request ──▶  Resyncing
Resyncing  ── the exchange has had time to finish ──▶  Blocking
```

While `Blocking`, uplink is dropped except the signature sizes. While `Resyncing`, everything
passes so the game can re-establish. A 20-second hold measured across four peers at once ran
block / resync / block, and that is the shape this reproduces.

The acceptance test needs no instrumentation: **hold for more than 30 seconds.** An error code
means the resync is not working. No error code means it is.

Measured: **451 s held, 35 completed resync cycles, 92.5% of uplink blocked, no error code**, and
nothing crossed outside the two configured pass lists. The 35 is the result rather than the 451 -
one long stretch is weak evidence, the renewal exchange completing thirty-five consecutive times is
the mechanism being demonstrated.

**So do not disarm it.** `--no-pass` is the right setting for a short sequence - it removes the
standing pass list, the only thing that can leak in a hold of a second or two. `--no-resync` is a
trap: a trigger size appears in about 0.4% of ordinary traffic, so an armed resync almost never
fires inside a hold that short, while disarming it guarantees an error code in any hold that runs
long. Running with neither a trigger nor a safety timeout now prints a warning saying so.

State is per remote address because the game talks to several peers at once, each on its own tick,
joining and leaving mid-session. Time comes from packet timestamps rather than a clock, so a whole
hold can be driven through in a unit test with no driver and no game running.

**Uplink and downlink are separate holds needing separate signatures.** An uplink pass list is
what this machine must still be able to send; a downlink one is what the peer must still be able
to send to us. Neither can be derived from the other, and the calibrator has to be told which
direction to watch - a downlink hold leaves uplink flowing perfectly normally, so watching the
wrong stream reports that nothing happened. Only uplink on 3074 has been measured so far.

## Dropping a packet and deferring it are different tricks

`run` holds blocked uplink and replays it the moment the hold is released; `hold` destroys it.
Which behaviour is wanted depends entirely on the direction, and the asymmetry is not obvious:

- **Downlink.** The server keeps sending current state, so dropping only delays what the client
  learns; the state arrives anyway. Buffering changes little.
- **Uplink.** Nothing re-sends the player's own actions. A dropped action is destroyed, not
  deferred - so the shots taken during a hold simply never happened.

That matters for short sequences. Some effects need the actions to *arrive late*, after the desync,
so the peer registers them against stale context - and for those, dropping is the wrong tool no
matter how well the block itself works. A reference limiter measured on this machine blocks uplink
just as completely as this project does and then replays 46-65 packets in millisecond-spaced bursts
on release; that replay was the entire difference in outcome.

Rebuilding it from that observation alone took the measured success rate from roughly 1 in 6 to 12
in 15 - past the reference limiter's own 6 in 9. So `run` does it by default, and `--no-buffer`
turns it off. See Finding 36 in [docs/protocol.md](docs/protocol.md).

Only *outbound* packets are held, whatever direction the hold itself covers - a `--full` hold
still destroys the inbound it blocks. The asymmetry is between directions, not between modules,
and replaying inbound is unproven: the server re-sends state regardless, so there is nothing to
recover, and delivering a backlog of stale state on release is the one direction where replay
could do harm rather than nothing. `--buffer-downlink` exists so that can be measured.

Limits are 2048 packets and a 30 s age, settable with `--buffer-size` and `--buffer-age-s`. They
are not peers: **the age limit is the operative one**. In a measured 295 s hold it discarded 87.7%
of the buffer, and the capacity could hold 7.4x more than the age could ever admit - so capacity
bounds memory against a rate spike, while age decides what is actually delivered. Eviction is
oldest first, because what matters on release is the most recent actions.

That release is measured: 252 packets reinjected in 13.6 ms (54 us each), no error code and no
in-game symptom. A full 2048-packet drain would take ~111 ms, queuing about 2 packets against
WinDivert's 4096-deep queue - so the drain cannot stall the receive loop at these rates.

## Hold, buffer, slow - three different things

| Mode | Blocked traffic | Peer sees |
|---|---|---|
| `--no-buffer` | destroyed | a gap, and the actions never happened |
| default | held, replayed on release | a gap, then the actions arrive late |
| `--slow` | held, released after `--slow-ms` | no gap - a stream running behind |

Slow is not a stronger hold, it is a different mode. Nothing is withheld permanently and nothing is
dropped, so there is no gap for the peer to react to: the whole conversation just runs late. It
delays whichever direction the hold covers - so a plain `run 3074 --slow` delays only uplink -
where buffering covers outbound only regardless of the hold.

The delay defaults to 2000 ms and is the one number here with no measurement behind it. What was
measured was the reference limiter's 30k variant - inbound TCP on a different port, under a
reconnect - which pins the order of magnitude and nothing finer. Expect to calibrate it.

## Safety

`hold` is the first thing here that can break a connection - in divert mode a received packet
reaches its destination only if it is reinjected.

- **`--dry-run` first.** It runs the module against sniffed traffic and reports what it *would*
  have dropped, changing nothing.
- **A packet that cannot be decided is passed, never dropped.** A module bug, an unparseable
  packet, an exception mid-decision - all of them let traffic through.
- **Closing the handle restores normal traffic**, so disposal is the kill switch, and it runs on
  cancellation, on error, and on every exit path. Ctrl+C releases.
- **The buffer cannot outlive the handle.** Blocked uplink is queued and replayed on release, but
  the queue is capped, aged out, and *discarded* rather than flushed if the handle closes - so the
  kill switch stays instant and no backlog is ever pushed into a session already in trouble.
  `--no-buffer` goes back to deciding every packet immediately.
- **Exit any other limiter completely** - not just idle. Two WinDivert handles on overlapping
  filters both receive every packet and can each drop or reinject it independently.

## Port groups

| Group | Protocol | Ports | Role |
|---|---|---|---|
| `3074` | UDP | 3074 | Primary peer traffic, ~10 Hz each way |
| `27k` | UDP | 27015-27200 | Additional peer traffic |
| `7500` | TCP | 7500-7509 | Service channel |
| `30k` | TCP | 30000-30009 | Instance and session control |

## Design rules

These are constraints, not preferences. Each exists because measurement showed the alternative
breaks.

**Filters match protocol and remote port only.** Never a local address, adapter, or interface.
WinDivert hooks the network stack rather than an adapter, so a port-keyed filter matches whether
traffic leaves through a VPN tunnel or a physical NIC. A connection behind CGNAT needs a VPN for a
workable NAT type, but the app must never assume one is present. Enforced by a test.

**Signatures are payload bytes, never frame bytes.** Frame length includes a link-layer header
that a Raw IP VPN tunnel does not have and Ethernet does. A frame-based signature would stop
matching the instant the VPN is toggled.

**Timeouts belong to individual modules.** A 3074 uplink hold has survived over 20 minutes,
because the handshake pass-through keeps renewing peer liveness. A held 30k reconnect errors out
around 25-30 seconds, because nothing equivalent protects it. Neither number generalises.

**The core has no UI dependency.** `D2Vpn.Core` targets `net9.0` and knows nothing about WPF, so
every module and the calibrator can be tested against synthetic packets with no driver present.

## Layout

```
src/D2Vpn.Core/        domain, interception, calibration  (net9.0, no UI)
src/D2Vpn.Cli/         command-line front end             (net9.0-windows)
tests/D2Vpn.Core.Tests/
tools/capture/         PowerShell capture and analysis scripts
docs/                  measured findings and prior-art analysis
```

## Building and running

Needs the .NET 9 SDK.

```
dotnet build
dotnet test
```

`d2vpn.cmd` in the repo root forwards to the built executable, so from an elevated terminal in
this directory:

```
.\d2vpn list
.\d2vpn calibrate 3074 --seconds 60
```

The leading `.\` is required in PowerShell for a script in the current directory. To drop it,
put the output directory on PATH for the session:

```
$env:PATH += ";$PWD\src\D2Vpn.Cliin\Debug
et9.0-windows"
```

Commands that touch the driver - `watch`, `calibrate`, `hold` - need an elevated terminal.

## Documentation

- [docs/protocol.md](docs/protocol.md) - measured network behaviour, with each claim's evidence
- [docs/prior-art.md](docs/prior-art.md) - how existing limiters work and what was learned from them
