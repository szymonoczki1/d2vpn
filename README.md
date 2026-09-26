# d2vpn

Network traffic control and macro management for Destiny 2, on Windows.

Packet interception through the WinDivert kernel driver, with per-module control over specific
port groups. Built on measurements taken from live traffic rather than on inherited constants -
see [docs/protocol.md](docs/protocol.md).

> **Status: early.** The measurement and calibration layer works. Traffic *control* is not built
> yet. What follows describes what exists today, not what is planned.

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
| `d2vpn calibrate <group> [--baseline N] [--hold N]` | Derive this machine's signature for a group |

Groups: `3074`, `27k`, `7500`, `30k`. Both `watch` and `calibrate` need an elevated terminal,
because loading a kernel driver does.

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

## Building

Needs the .NET 9 SDK.

```
dotnet build
dotnet test
```

## Documentation

- [docs/protocol.md](docs/protocol.md) - measured network behaviour, with each claim's evidence
- [docs/prior-art.md](docs/prior-art.md) - how existing limiters work and what was learned from them
