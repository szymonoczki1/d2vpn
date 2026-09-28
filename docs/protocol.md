# Destiny 2 network behaviour — observed

Everything here was measured on this machine, not taken from documentation. Each claim says how
it was established so it can be re-checked when the game patches.

Reproduce with:

```powershell
cd tools\capture
.\Start-Capture.ps1 -Label <scenario> -Seconds 60
.\Get-PortBreakdown.ps1 -Label <scenario> -Lengths
```

---

## Measurement setup

| | |
|---|---|
| Date | 2026-09-25 |
| Game PID at capture | 38400 |
| Capture interface | the VPN's WireGuard tunnel adapter |
| Physical path | consumer uplink → VPN tunnel → internet |
| Tool | `dumpcap` / `tshark` 4.x via npcap (capture works without elevation) |

**The capture interface is the tunnel, not the physical NIC.** All Destiny sockets are bound to
the tunnel adapter's address. Capturing on the physical adapter would show
only encrypted WireGuard UDP and nothing usable. This affects *observation only* - see the design
constraint below.

### Why the VPN is present

The connection is behind CGNAT, which forces strict NAT and blocks the peer-to-peer matchmaking
Destiny depends on. Routing through a VPN with a nearby exit gets NAT type to moderate. The
tunnel is therefore permanent infrastructure here, not a temporary condition - but see the
constraint: it must never be *assumed*.

---

## Design constraint: d2vpn must be VPN-agnostic

**Requirement.** d2vpn works identically with the VPN up or down. HarryWatch and the user's
current limiter both behave this way and it must not regress.

**How that is achieved.** Match on protocol + remote port only. Never bind a filter to a local
address, an adapter, or an interface index. HarryWatch's filters are already written this way:

```csharp
new WinDivert(Filter.True.And(x => x.IsUdp && x.Network.RemotePort == 3074), WinDivertLayer.Network)
```

WinDivert hooks the network stack rather than an adapter, so a port-keyed filter matches whether
the packet is bound for a tunnel adapter or a physical NIC. The local address (the tunnel's here,
something else without the VPN) never enters the predicate.

The capture scripts in `tools\capture` *do* select an interface, because a capture necessarily
happens somewhere. That is a property of the measurement tooling and must not leak into the app.

---

## Port map

From `survey-01` — a 10 s capture while sitting in-game, 2793 packets, 0 dropped. Every port
below was attributed to `destiny2.exe` by joining the capture against the OS socket table, so
none of it is Steam or background traffic.

| Proto | Remote port(s) | Observed | Role |
|---|---|---|---|
| UDP | **3074** | 284 pkts, 1 peer, UL 184 / DL 100 | Primary game/peer traffic |
| UDP | **27017–27127** (seen: 27017, 27024, 27030, 27046, 27056, 27060, 27127) | 1333 pkts across 7 ports | Peer traffic. Busiest single conversation was 27056 (791 pkts) |
| TCP | **30000** | 65 pkts, balanced UL/DL | Instance/session control. Long-lived connection |
| TCP | **7500** | 39 pkts, balanced UL/DL | Service/API channel. Long-lived connection |
| UDP | 31431 | 4 pkts | Outside the ranges we planned for — see below |

Established TCP connections read directly from the OS socket table:

```
<tunnel>:60059 -> <server>:7500    ESTABLISHED
<tunnel>:64968 -> <server>:30000  ESTABLISHED
```

### Notes and corrections to the reference implementation

- **27k is wider than a Steam range.** 27015–27200 is conventionally Steam's range, but here
  every port in it belonged to `destiny2`. HarryWatch calls this provider `Players`, which
  matches: it is peer traffic, not Steam.
- **UDP 31431 is unexplained.** It falls in Bungie's published 20950–38997 range but outside the
  27k window HarryWatch watches, so its `PlayersProvider` filter would miss it. Needs a longer
  capture to know whether it is transient or load-bearing.
- **TCP port ranges are unverified at the edges.** HarryWatch filters 7500-7509 and
  30000-30009. We have only ever observed exactly 7500 and exactly 30000. Keeping the wider
  ranges is the safe default: HarryWatch ran reliably on this machine, so its ranges are treated
  as trustworthy and our single short sample is not evidence against them.

---

## Measured: what a 3074 uplink block actually does

Capture `pve-toggle-01` - 60 s, 28926 packets, 0 dropped, BitcoinMiner's `PVE` module with
`Buffer: true` and `AutoResync: true`, toggled via CapsLock (its `OutboundKeybind`).

### Timeline

| Window | UL/s | DL/s | What is happening |
|---|---|---|---|
| s0-8.7 | 10 | 10 | Steady state. Exactly 10 packets/s each way - a **10 Hz tick** |
| s8.7 | 3 | 10 | Block engages mid-second |
| s9-11 | **0** | 10 | Uplink fully stopped. **Downlink completely unaffected** |
| s12-15 | 1,1,4,1 | 7,2,3,17 | A handful of packets escape - see below |
| s15-18 | 0 | 17,14,15 | Peer sends *more* than normal: it is probing for the missing uplink |
| s19.641 | **62** | 15 | Release. Buffer drains |
| s20-23 | 18,18,16,13 | ~10 | Elevated, tapering |
| s24-59 | 10 | 10 | Full recovery. No error code, no disconnect |

Block duration ~10.9 s. **There is no known time limit on a 3074 uplink hold.** One has been
held for over 20 minutes with no disconnect. See "How long a hold can last" below.

### Finding 1 - the block is directional and downlink is untouched

DL held at 10/s through the entire block. This is a true uplink-only cut, confirming that
`3074UL` in the AHK scripts means exactly what it says.

### Finding 2 - selective pass-through, confirmed

Only **7** uplink packets crossed during 10.9 s of "blocking". Their lengths share **nothing**
with steady-state traffic:

```
steady state UL : 474, 482, 490, 498, 506, 514, 522, 530, 538, 546, 554, 562,
                  570, 578, 586, 594, 602, 610, 626, 642, 650, 666, 698, 1170

escaped         : t=12.629  frame= 164  payload= 136
                  t=13.049  frame= 156  payload= 128
                  t=14.327  frame=  79  payload=  51
                  t=14.775  frame=1194  payload=1166   <-- one large packet
                  t=14.829  frame= 218  payload= 190
                  t=14.927  frame= 226  payload= 198
                  t=15.020  frame= 226  payload= 198
```

(Frame lengths shown for traceability to the capture; **payload is the unit that matters** - see
Finding 3b.)

Disjoint sets. These are not leaked game-state packets - they are a different message class the
game only emits once the peer stops acknowledging: small control packets (79/156/164) followed by
a handshake (1194, then 218/226/226). The limiter recognises them and lets them through, which is
what keeps the session alive while game state stalls.

**This is the whole technique.** Drop the payload, pass the handshake.

### How long a hold can last

Indefinitely, as far as we can tell. A 3074 UL hold has run **over 20 minutes** without a
disconnect, and the mechanism explains why: the escape sequence at s12-15 is not a
one-off grace period, it is the game re-establishing liveness with its peer. As long as those
handshake packets keep passing, the session keeps renewing. The 10.9 s in this capture was chosen
by the test procedure, not by any limit.

**Do not confuse this with the ~25 s figure in `KickModule`.** That module runs on the *Players*
provider (27k) and targets *another player*: ~25 s is when the victim drops, and its force-disable
at 26-27 s exists to stop the operator from eating a BEAVER error afterwards. It says nothing
about how long you may hold your own 3074 uplink.

**Design consequence.** A 3074 module needs no safety auto-disable. A 27k kick-style module does.
Timeouts belong to specific modules, never to the app as a policy.

### Finding 3 - CORRECTED: signatures must be measured as payload length

**An earlier version of this document compared Wireshark's `frame.len` against HarryWatch's
constants and concluded they were all wrong. That comparison was invalid** - HarryWatch's
`Packet.Length` is `Payload.Length`, the transport payload, not the frame.

The VPN's tunnel adapter captures as **Raw IP** (no Ethernet header), so here `frame.len == ip.len`
and UDP payload is `frame.len - 28` (20 IP + 8 UDP). Recomputed:

| `frame.len` | Payload | HarryWatch constant | Match |
|---|---|---|---|
| 164 | **136** | 136 | **exact** |
| 156 | **128** | 128 | **exact** |
| 79 | 51 | 42 | no |
| 226 | 198 | 233 | no |
| 1194 | 1166 | 1258 | no |

So the real picture is a split, not a wholesale mismatch:

- **128 and 136 are stable protocol constants.** They appear identically on this machine and in
  code written years earlier against a different connection. Safe as shipped defaults.
- **51, 198 and 1166 vary.** These are the ones that shift with version, loadout, or session -
  exactly the class BitcoinMiner exposes as `TriggerLength` for the user to calibrate.

**Revised design rule.** Ship the stable values as defaults, expose *every* value as
configuration, and provide calibration to derive the variable ones. Not "never hardcode" -
"never hardcode without an override and a way to re-derive."

### Finding 3b - payload length is the only VPN-agnostic unit

This is a correctness requirement, not a preference.

Frame length depends on the link layer. On a Raw IP VPN tunnel a packet frames at
`ip.len`; over Ethernet the same packet frames at `ip.len + 14`. **A signature expressed in frame
bytes would therefore break the moment the VPN is toggled** - a direct violation of the
VPN-agnostic requirement.

Payload length is invariant across both paths, and it is also what WinDivert hands a module via
`WinDivertParseResult.DataSpan`. So:

- **d2vpn stores and matches signatures in payload bytes, everywhere.**
- Capture tooling must report payload length (`data.len`) alongside `frame.len`, or the two halves
  of the project will silently disagree about what "length 226" means.

### Finding 4 - the buffer drain is NOT paced

At release, **59 packets were injected inside a single millisecond**:

```
19.641  len=218  gap=0.0 ms
19.641  len=210  gap=0.0 ms
...  (59 packets, all at 19.641, every gap 0.0 ms)
19.933  len=1194  gap=291.1 ms
19.987  len=226   gap= 54.2 ms
20.040  len=210   gap= 52.9 ms     <-- ~54 ms cadence, i.e. ~2x normal rate
```

A hard dump, then a 291 ms pause, then a large packet, then catch-up at roughly double tick rate
until it settles back to 10 Hz by s24.

This corrects an earlier guess: BitcoinMiner's `RateLimiter` dependency is **not** pacing the
drain. HarryWatch does the same thing - `ClearDelayQueue(null, Buffer, 0)` with a zero delay - and
its `// 1000 / 20 / 3` comment shows someone tried paced values and settled on dumping. Both
tools dump, and the game tolerates it. **Drain pacing is not where the quality difference lies.**

### Finding 5 - buffered traffic differs from steady-state traffic

The 59 replayed packets were 202-259 bytes, not the 474-698 of steady state. Once the stall
begins the game stops producing large state updates and switches to small ones, so what the
buffer holds is mostly post-stall traffic. Buffer sizing should be reasoned about in packets and
seconds, not extrapolated from steady-state byte rate.

---

## Measured: `З0k Slow` and `Reconnect` (TCP 30000)

Capture `z0kslow-reconnect-01` - 90 s, 28468 packets, 0 dropped. Sequence: F9 (`З0k Slow` on)
~s10, F10 (`Reconnect`) ~s11, hold 25 s, release. A 60 s hold was requested and refused as
unrealistic - see the ceiling below.

### Finding 6 - the Reconnect module is two outbound RSTs

```
t=11.219  <tunnel>:57229 -> :30000  len=40   RST
t=11.219  <tunnel>:57248 -> :30000  len=40   RST
```

Both active 30k connections killed in the same millisecond, one RST each, 40 bytes (bare TCP
header). This is HarryWatch's `ReconnectModule` behaviour confirmed: iterate every live peer,
clone a recent packet, set the RST flag, recalculate, inject.

The game recovered immediately:

| Time | Event |
|---|---|
| 11.219 | Two RSTs injected; streams to `<server-a>` and `<server-b>` both end |
| 11.548 | New SYN to `<server-a>` - **330 ms later**, same server |
| 53.540 | New SYN to `<server-c>` - a *different* server. The actual instance change |

**Open question - settled, see finding 46.** Two resets are injected per connection and only the
outbound one is ever visible, because an injected inbound packet is delivered up the local stack and
never reaches the adapter. **Adapter-level capture cannot verify inbound injection**, so a tool
reporting twice as many resets as a capture shows is correct rather than broken.

### Finding 7 - `З0k Slow` holds inbound traffic and releases it in batches

Inbound gap distribution by phase:

| Phase | Packets | Mean gap | **Max gap** |
|---|---|---|---|
| Before hold (s0-11) | 29 | 0.36 s | 1.32 s |
| **During hold (s11-45)** | 97 | 0.35 s | **5.94 s** |
| After release (s45+) | 201 | 0.22 s | 2.01 s |

Two gaps during the hold - 3.91 s and 5.94 s - sit far outside both the pre-hold maximum (1.32 s)
and the post-release maximum (2.01 s). Delivery is batched:

```
t=24.630  len= 40     <- bare ACK
t=24.881  len=976
t=24.881  len=552  x13   <- fifteen packets inside ~1 ms
t=24.886  len=552
t=30.935  len=552  x3    <- next batch, again same millisecond
```

Hold for several seconds, dump the batch instantly, hold again. Same instantaneous-release
behaviour as the 3074 buffer drain in Finding 4 - **neither tool paces a release**.

Dominant inbound payload on 30k during a reconnect is **552 bytes**, with a 976 leading the batch.

### Built - `--slow`

Implemented as a delay line: while slow is on, blocked packets go to the same `PacketBuffer` the
hold uses, and `TakeDue` releases each one once it has been held for `SlowDelay`. So nothing is
dropped and the peer never sees a gap - only a stream running behind.

The mechanism comes from the reference limiter's own description of this module, "delays all inbound
packets from 30k by a fixed number of seconds", which is a plain user-facing README and not its
code. The batching above is consistent with that: a delay line drained on packet arrival delivers in
bursts, not smoothly.

What this does **not** establish is the right delay on UDP 3074, which is what the built module is
for. Finding 7 is inbound TCP on a different port under a reconnect, and TCP reacts to delay by
retransmitting where UDP does not - so it constrains the order of magnitude and nothing finer. The
default is 2 s and is a starting point to calibrate from, not a result.

Decisions worth recording:

- **Scoped to the hold's direction, unlike buffering.** Buffering covers outbound only whatever
  the hold covers, because the server re-sends inbound state anyway. That argument does not apply
  to a delay - delaying inbound is the whole of what the reference module does - so slow follows
  the hold instead: `run 3074 --slow` delays only uplink, `--full --slow` delays both.
- **The pass list still passes immediately.** Delaying a handshake size would put the machinery
  that keeps a session alive behind the same delay as everything else, which is how a hold turns
  into an error code.
- **Age still beats due-ness.** Delivery is driven by packet arrivals rather than a timer, because
  a timer would mean a second thread writing the handle. On a port with traffic in both directions
  that is a fine clock, since slow never stops the stream that drives it - but if a port goes quiet
  entirely, anything that passes `BufferMaxAge` while waiting is discarded rather than sent stale.
- **`--slow-ms` longer than `--buffer-age-s` is refused.** Every packet would expire before coming
  due, making slow an elaborate way of dropping everything.

**Not yet confirmed in game.**

### Finding 8 - why the first 30k attempt showed nothing

Capture `z0kslow-01` recorded a maximum inbound gap of 1.80 s against a 1.82 s baseline in an
unrelated capture - i.e. no effect at all. Against Finding 7's 5.94 s the contrast is decisive:
**the module did not fire that time.** The ambiguity in that earlier analysis is resolved. It was
not a delay too small to see; nothing happened.

Useful procedural lesson: `AltTabSupressKeybinds` is on, so a keypress only registers while
Destiny has focus. Any capture run needs the game focused before the toggle.

### Finding 9 - hold ceilings are per-module, and 30k has one

| Module | Practical ceiling | Evidence |
|---|---|---|
| 3074 UL (`PVE` outbound) | **None known** | 451 s across 35 resync cycles, instrumented - see finding 38. (An earlier, uncounted "over 20 minutes" from this session is superseded by it.) |
| `З0k Slow` | **~25-30 s** | "60 is a bit unrealistic since its gonna error code me" |

The 3074 hold survives because the handshake pass-through keeps renewing peer liveness
(Finding 2). Nothing equivalent protects a held 30k reconnect, so the error code arrives.

**Design consequence, reinforcing the earlier one.** A 30k module needs a configurable safety
auto-disable with a default around 20-25 s. A 3074 module needs none. Ceilings belong to
individual modules and must be discovered per module, never assumed from another.

---

## Measured: the 3074 signature has a stable core

Four independent measurements of a 3074 uplink hold - one via Wireshark, three via `d2vpn
calibrate` - agree on four payload sizes and disagree on everything else.

| Source | Signature (payload bytes) |
|---|---|
| Wireshark `pve-toggle-01` | **51, 128, 136**, 190, 198, **1166** |
| `calibrate` run A | **51, 128, 136**, 174, 182, 505, **1166** |
| `calibrate` run B | **51, 128, 136**, 487, 503, **1166** |
| `calibrate` run C | **51, 128, 136**, 515, 558, 576, **1166** |

**51, 128, 136 and 1166 appear in all four.** Two of them, 128 and 136, are also the two constants
HarryWatch hardcodes that match this machine exactly (Finding 3). That is a fixed handshake
skeleton, stable across sessions and across years of game versions.

### The variable extras are two different things

- **Small sizes (174, 182, 190, 198)** sit well below the ordinary game-state range, which on this
  machine starts around 446. They appear right after the 1166 packet and look like handshake
  replies - genuine signature that only shows up when the exchange gets that far.
- **Large sizes (487, 503, 505, 515, 558, 576)** sit *inside* the ordinary range. These are very
  likely boundary contamination, not signature: a hold begins partway through a second, so the
  opening bucket carries normal traffic before the keypress as well as the hold after it. In run C
  the opening bucket held exactly three packets and the signature carried exactly three
  large sizes.

The calibrator now refines the window start onto the first uplink gap of 0.5 s or more rather than
trusting the bucket edge, which should remove the large sizes while leaving the core and the
handshake replies. **Pending confirmation by a further run.**

### Design consequence

A 3074 module should ship `51, 128, 136, 1166` as its default allowlist - they have held across
every measurement taken - and use calibration to add whatever else a given session needs. This is
the concrete form of the rule from Finding 3: hardcode the stable values as defaults, expose all
of them, and always be able to re-derive.

---

## Measured: a hold is block / resync / block

Capture: `d2vpn calibrate 3074 --seconds 60`, CapsLock held from s10 to s30. **Four** peers were
active, and all four show the same shape:

```
   12s     20 ################################   normal
   13s     12 ###################                engaging
   14s      1 ##                                 collapse 1
   16s      1 ##
   17s      4 ######                             recovering
   18s     18 #############################      FULL RATE, mid-hold
   20s     18 #############################
   21s      1 ##                                 collapse 2
   28s      1 ##
   29s     18 #############################      released
```

### Finding 10 - one keypress produces several collapses, not one

A 20-second hold was **block (s13-16), full-rate resync (s17-20), block (s21-28)**. Uplink between
the collapses is not a leak: it is ~18/s, indistinguishable from normal, and it happens on every
peer at once.

This matches the reference implementation. HarryWatch's `XboxProvider.AllowPacket` returns
`true` for most packets while a resync is in flight - `if (id < 5) return true`,
`if (p.IsPveService()) return true`, and an explicit "have to accept 1 data packet". The limiter
deliberately stops blocking so the game can re-establish with its peer, then resumes.

**Design consequence.** A module is not a switch that stays off. It is a cycle: block, detect that
the peer needs re-establishing, pass traffic until it has, block again. Anything that models a
hold as one continuous cut will not behave like the tools that work.

### Finding 11 - peer count varies and analysis must be per peer

Peers observed on 3074 across sessions: **1**, then **2**, then **4**. Uplink scaled with it -
10/s, 23/s, 80/s - which is roughly 10-20/s per peer.

Aggregating them is fatal to detection. If one peer of four is held, total uplink falls by a
quarter, nowhere near a collapse. Every reference limiter keys its state on remote address, and
so does d2vpn now.

A cross-check settled a suspicion here: a rate that looked like double what Wireshark measured was
**not** the driver reporting packets twice. `watch` found 0 duplicates in 875 packets on a single
interface; the capture simply had one peer and the later run had two.

### Finding 12 - the signature needs an enrichment filter, and it recovers a third constant

A hold is not perfectly clean - ordinary packets slip through at boundaries and during resyncs.
Membership alone cannot tell them from signature. Frequency can:

| payload | in hold | in normal | enrichment | verdict |
|---|---|---|---|---|
| 1166 | 19 (53%) | 8 (0.6%) | **88x** | signature |
| 233 | 3 (8%) | 1 (0.1%) | **112x** | signature |
| 51 | 3 (8%) | 4 (0.3%) | **28x** | signature |
| 128 | 2 (6%) | 6 (0.4%) | **12x** | signature |
| 136 | 1 (3%) | 3 (0.2%) | **12x** | signature |
| 174 | 1 (3%) | 81 (6.0%) | 0x | ordinary |
| 182 | 1 (3%) | 75 (5.6%) | 0x | ordinary |
| 190 | 1 (3%) | 68 (5.1%) | 1x | ordinary |
| 206 | 1 (3%) | 49 (3.7%) | 1x | ordinary |
| 214 | 1 (3%) | 19 (1.4%) | 2x | ordinary |

Filtered signature: **36, 51, 128, 136, 233, 1166**.

**Reproduced.** A second run on a different day with two peers instead of four returned the
identical signature, the identical five excluded sizes (174, 182, 190, 206, 214, all enriched
0.6-2.0x), and the same block/resync/block shape on both peers - 14-19s + 22-29s on one,
14-17s + 21-28s on the other. Two independent runs agreeing to the byte is what promotes this
from a measurement to a finding.

Note **233**. Earlier runs never surfaced it, and it is a third constant from HarryWatch's
hardcoded set - so **128, 136 and 233 all match this machine exactly**, against 42 and 1258 which
do not. The skeleton is more stable than the first comparison suggested; what varies is the large
data packet (1258 there, 1166 here).

The filter is applied to the signature itself rather than offered as advice: passing a stray means
passing ordinary game state, which is precisely what a selective block must never do.

---

## Measured: what AutoResync actually does

A controlled comparison - the same 20-second uplink hold, run twice, with BitcoinMiner's PVE
`AutoResync` the only setting changed. `Buffer` stayed on for both.

| | AutoResync ON | AutoResync OFF |
|---|---|---|
| Shape | block / resync / block | **one continuous collapse** |
| Uplink while held | ~1/s (5-9% of normal) | ~3-4/s (**22%** of normal) |
| `128` | 2 packets (6%) | **64 packets (47%)** |
| `136` | 1 packet (3%) | **59 packets (43%)** |
| `1166` | 19 packets (53%) | 2 packets (1%) |

### Finding 13 - the resync stretches are the limiter's doing, not the game's

With `AutoResync` off, both live peers held continuously from s14/s15 to s32. The mid-hold return
to full rate seen in Finding 10 does not happen. That confirms the cycling is a deliberate choice
by the limiter rather than the game re-establishing on its own.

### Finding 14 - 128 and 136 are a reconnect request, retried until answered

With resync off, `128` and `136` make up **90% of everything that crosses a hold**, arriving
steadily at 3-4/s for its entire length. Nothing else changes; the game simply keeps asking.

With resync on, those two nearly vanish (6% and 3%) and `1166` becomes dominant instead. So
`AutoResync` is not passive filtering - it **answers** the game's reconnect exchange and drives
it to completion, which stops the retry storm and produces the single large data packet that
finishes it.

This also explains why the signature looked different between the two modes: with resync on you
are mostly seeing a *completed* handshake, with it off you are seeing an *unanswered* one.

### Finding 14b - without resync, a 3074 hold dies at ~30 seconds

| AutoResync | Longest safe 3074 uplink hold |
|---|---|
| ON | **no known limit** - held over 20 minutes |
| OFF | **~30 seconds**, then an error code |

The measured run held 20 seconds and felt completely normal, which is why nothing seemed wrong:
it stopped just short of the ceiling. The retry storm in Finding 14 is what runs out - the peer
asks, never gets an answer, and eventually gives up on the session.

This also refines Finding 9. A hold ceiling is not a property of the port, it is a property of
whether the reconnect exchange is being completed. Same port, same module, same key: 30 seconds
or unlimited depending on one setting.

**Design consequence, and it is the big one for Phase 2.** A module cannot be only a filter, and
resync cannot be deferred to a later version. The pass-list alone buys 30 seconds. Something has
to recognise a reconnect request and carry the exchange through, and that single behaviour is the
difference between a toy and a limiter worth using.

### A resync design that follows from the measurements alone

Finding 10 measured what a resync looks like from outside: roughly 3-4 seconds during which
uplink returns to full rate, triggered while the peer is asking to reconnect. Finding 14 supplies
the trigger: an outbound `128` or `136`.

That is enough to build without inspecting payloads at all:

```
Blocking   --- saw outbound 128 or 136 --->   Resyncing
Resyncing  --- ~3-4 s elapsed            --->   Blocking
```

While `Resyncing`, pass everything; while `Blocking`, drop uplink except the signature sizes. The
reference implementation goes further and reads a counter out of the payload to detect precisely
when an exchange completes, which is more accurate and also the part most worth deriving
ourselves rather than copying. A timed resync is the honest first version: it reproduces the
measured shape, and where it is wrong it will be wrong visibly.

### Finding 15 - a peer's silence is not a hold

The same run exposed a detector bug worth recording. A peer whose first packet arrived at s44 was
reported as `HELD 5-44s, 0% of normal`, and the leading zeros dragged its baseline to 2/s against
a real rate near 18/s. Peers join and leave mid-session; offsets are global so every peer shares
one timeline, which leaves late joiners with leading zeros and early leavers with trailing ones.

Analysis is now restricted to the span between a peer's first and last packet.

---

## Measured: what a sniffing handle can and cannot see

Three runs of the same downlink hold, changing only the WinDivert handle priority.

| Priority | Downlink during a full-duration hold | Reading |
|---|---|---|
| **+1000** | flat 10/s for 57 s - **nothing at all** | this process sits *ahead* of the other limiter |
| **0** | brief 2 s dips | in between |
| **-1000** | **0/s for 41 s, 3% of normal** | this process sits *behind* it |

### Finding 16 - the priority that observes a block depends on direction

WinDivert handles occupy a fixed position in the network stack, and packets traverse that stack in
opposite directions depending on whether they are inbound or outbound. A handle that is downstream
for outbound is therefore upstream for inbound.

A diverting handle that drops a packet removes it before anything downstream sees it. So to
*observe* another tool's blocking, this process must sit downstream of it - and which priority
achieves that flips with direction:

| Direction | Priority that observes correctly |
|---|---|
| Uplink | **0** (already downstream) |
| Downlink | **-1000** |

This was not a subtle effect. At +1000 a twenty-second downlink hold produced a perfectly flat
timeline, indistinguishable from a keypress that never registered. Anyone measuring downlink at
the uplink's priority would conclude the module does not work.

**Consequence for measurement.** `calibrate --downlink` now defaults to -1000, and `--priority`
exists so the assumption can be re-tested when anything about the environment changes.

**Consequence for d2vpn itself.** When d2vpn does the blocking rather than watching someone else
do it, this stops being about observation and becomes about precedence: two limiters diverting the
same ports will fight, and which one wins depends on this number. Another limiter must be fully
exited, not merely idle.

### Finding 17 - a downlink hold also cycles, and its signature differs from uplink's

With a usable priority, downlink shows the same block / resync / block shape as uplink: five
separate collapses across one continuous hold, full-rate traffic between them.

Downlink signature, **confirmed** by a second run at the correct priority: **51, 233**.

The earlier run also showed 98, on the strength of a single packet. It did not reappear, so it was
an artefact and is not part of the signature. 36 was rejected in both runs, at 2.4x enrichment.

In the confirming run, 233 crossed 4 times and **never appeared in normal traffic at all**, while
51 crossed twice at 13.2x. Both peers produced identical timelines - the same three collapses at
11-16 s, 17-26 s and 27-30 s - which is worth noting: the hold looked global rather than per-peer
on that occasion.

Against the uplink signature of 36, 51, 128, 136, 233, 1166:

- **51 and 233 appear in both.**
- 98 is downlink-only.
- 36, 128, 136 and 1166 are uplink-only - and 36 was explicitly *rejected* for downlink.

So the two directions share part of a handshake and differ in the rest, which is what the design
already assumed: neither signature can be derived from the other.

**Superseded by Finding 19.** `51, 233` reproduced across two runs and was briefly shipped as the
downlink pass list, but both runs had resync enabled. With resync disabled, downlink passes
nothing at all - so these are resync-window traffic rather than packets the session requires. What is *not* known is which of them - if either - is the reconnect
request that should start a resync. Uplink's 128 and 136 were identified by holding with a
reference limiter's own resync disabled and watching which packet the game repeated at 3-4/s; the
equivalent downlink run has not been done. Until it is, the downlink defaults carry no resync
trigger and a 25 s safety timeout instead.

### Finding 17b - downlink is buffered on release too

The confirming run released at s30 and produced **44 packets in one second against a 10/s
baseline** - the same instantaneous dump measured on uplink in Finding 4, so buffering is not an
uplink-only behaviour.

Only about 4 seconds' worth came back against roughly 19 seconds held, so most held traffic was
discarded rather than replayed. Whether that is a buffer cap, an age limit, or the game itself
refusing stale packets is unknown.

### Finding 18 - a peer sending one packet a second cannot be judged

One run carried a peer at 1 packet/second that reported three separate holds. At that rate a quiet
second and a held second are the same thing, so the "collapses" are noise and its packet sizes are
not signature.

Peers without a usable baseline are now excluded from the signature while still being listed, so
the difference between a peer that was ignored and one that was never there stays visible.

---

## Measured: downlink has no pass list, and cannot be probed the way uplink was

Same experiment that identified uplink's reconnect request - hold with a reference limiter's
`AutoResync` disabled - run against downlink.

### Finding 19 - with resync off, a downlink hold passes nothing at all

Nineteen seconds, both peers, **zero packets**. Not a reduced rate: an empty timeline. Then a
release dump of 99 and 97 packets in a single second.

With resync on, the same hold cycled through three collapses and 51 and 233 crossed. With it off,
nothing crosses. Packets genuinely required to keep the session alive would have crossed either
way, so **51 and 233 are resync-window traffic, not a pass list**.

An earlier revision shipped them as the downlink signature. That was wrong, and this run is what
caught it. The downlink defaults now carry an empty pass list.

### Finding 20 - WITHDRAWN: the downlink trigger is 128, travelling outbound

**This finding was wrong.** It claimed a downlink trigger could not be observed from this machine,
reasoning that the peer's retries are dropped upstream of any local observer.

The premise was wrong about who retries. Whichever direction is held, **it is this machine that
notices a peer has gone quiet and asks to reconnect** - so the request is outbound either way.
A packet capture during a downlink hold with resync disabled showed exactly that:

```
<tunnel> -> <peer-a>   12118 -> 3074  Len=128
<tunnel> -> <peer-b>  12118 -> 3074  Len=128
```

Outbound, 128-byte payload, repeating roughly every 400 ms, to several peers at once. The same
reconnect request as uplink.

The data was already in our own runs and went unexamined. The no-resync downlink run reported
*"Uplink kept flowing throughout (209 packets)"* - a single number, with the reconnect requests
inside it. The report showed every size in the held direction and nothing about the other one.

**Two consequences.**

*For the module:* a resync trigger does not necessarily travel the direction being held, so a
module must watch traffic it is not blocking. Passing untouched traffic without inspecting it
means never seeing the trigger, and a downlink hold would sit blocking until its safety timeout
while the game asks the whole time. `HoldModuleOptions.ResyncTriggerDirection` exists for this and
defaults to the held direction, which is correct only for uplink.

*For the tooling:* calibration now reports the most common sizes travelling the opposite direction
during a hold. That is where a trigger is most likely to be, and it was the one place the report
did not look.

**Consequence.** The downlink module blocks outright, with a safety timeout instead of a resync.

### Finding 22 - ~30 seconds is the game's timeout in either direction

| Hold | Ceiling |
|---|---|
| Uplink, reconnect exchange completed | **none known** - over 20 minutes |
| Uplink, exchange not completed | ~30 s |
| Downlink | ~30 s |

Downlink dies at the same point as an uplink hold whose exchange is never finished, which suggests
~30 s is the game's own limit on an unacknowledged peer rather than anything specific to a
direction. Note that during a downlink hold the client is still sending normally - so it is not
that the peer stops hearing from us, it is that we stop confirming what it sends. Either way the
clock runs out.

That makes resync the only thing that lifts the ceiling: it resets that clock. Without it, every
hold on this port is a ~30 second hold. The downlink default's 25 s timeout leaves a small margin
under the measured limit.

### Finding 23 - what a downlink hold does in play

Incoming world state stops arriving, so the client keeps acting but never learns the result:

- Interacting with an object does not complete until the hold is released.
- Buffs neither expire nor apply while it is on.

The held state is not discarded - releasing replays it, which is the release dump appearing as
~99 packets in a single second. This is the mechanism a downlink hold is *for*, and it explains
why the buffering seen in Finding 17b matters rather than being an implementation detail: the
queued state is the point.

### Finding 21 - the settling after a release is not a hold

The same run produced two phantom windows, at s31-33 and s58-59, immediately after the release
dumps. Draining a buffer leaves traffic briefly below normal while it recovers, which looks exactly
like a shallow collapse - and those windows contributed 36, 42 and 51 to a signature, none of which
belonged there.

A collapse beginning immediately after a burst more than twice baseline is now treated as the tail
of a release rather than a new hold.

---

## What the modules are for

Packet captures show what a hold does to traffic. They do not show why anyone would want one, and
that turns out to matter for testing.

### Uplink hold

Used for **desync**. The game continues to believe the player is where they were when the hold
began, so:

- A "clone" is left at the held position while the player has actually moved.
- A shot can be separated from its shooter - the game does not attribute the bullet to the player.

**Nothing freezes or stalls**, so a hold that worked can look much like one that did nothing, and
the acceptance test was pushed onto packet evidence for a long time - see below. But there *is* an
observable symptom with the right test.

### Testing an uplink hold in play: kill an enemy

Kill something during the hold. Uplink carries what the player does, so the server never learns
the enemy died; downlink is untouched, so the server's state keeps arriving and says it is alive.
**The enemy reappears.**

That makes it a clean binary signal, and the polarity is worth being explicit about:

| Observed | Means |
|---|---|
| Enemy reappears | uplink is genuinely blocked - the kill never reached the server |
| Enemy stays dead | uplink leaked; the kill got through |

It works *because* of the asymmetry that makes uplink hard to see otherwise: downlink stays open,
so the server continuously corrects the client, and any local state the server did not authorise
is visibly undone.

This is more sensitive than the blocked percentage. A single packet carrying the kill is enough to
register it, in exactly the way a single packet carried a buff through a 53%-blocked downlink
hold - so a hold can be 90% blocked and still fail this test.

### Downlink hold

Used to **defer incoming world state**. The client keeps acting but never learns the result:

- Interacting with an object does not complete until release, then lands.
- Buffs neither expire nor apply while held.

Unlike uplink, this is trivially visible in play, which is why the downlink findings were easier
to confirm.

### Consequence: a hold has to prove itself

Since an uplink hold cannot be seen, `d2vpn hold` idles for a warmup period before engaging and
then checks two signals that need nothing observed in play:

1. **The game asks to reconnect.** A trigger-sized packet means it noticed a peer went quiet, so
   the block reached it.
2. **The peer sends harder than usual.** Downlink measured at 17/s against a 10/s baseline during
   an uplink hold - the peer probing for a client that went silent.

Either alone is weak. Together they distinguish "the block worked and the resync carried it" from
"the block never landed", which the first real hold could not: it dropped 780 of 1251 packets,
survived 45 seconds, and proved nothing, because surviving is what both outcomes look like.

---

## Validated: d2vpn's own uplink module

First end-to-end run of `d2vpn hold 3074` against a live game, with an independent packet capture
running alongside.

```
UL 754/1073 dropped   DL 0/794 dropped   resyncs 5
reconnect requests seen : 10
resync windows started  : 5
```

Thirty-five seconds held, no error code - past the ~30 s ceiling a pass-list alone dies at
(Finding 14b). The capture shows the same thing from outside:

```
 23s UL 20 DL 20     baseline
 25s UL  0 DL 20     block engaged, downlink untouched
 32s                 game sends 128 then 136 - asking to reconnect
 35s UL 21           resync window: 1254, 1258, 720, 795 cross
 37s UL  0           blocking resumes
 59s UL 20           released
```

Those larger sizes are not in the pass list. They crossed because the resync had opened, which is
the cycle working rather than a leak.

**Everything the design rests on is now confirmed on our own code**: the filter, directional
blocking, the pass list, trigger detection, resync cycling, and survival past the ceiling.

### A methodological note

This run was first reported as a failure. The module said `reconnect requests seen: 0` while the
capture showed the requests plainly - a diagnostic counter was being cleared during teardown
before it could be read, and the unit test covering it asserted that clearing as correct.

Twice now a measurement has contradicted a mechanism that was already established, and twice the
measurement was wrong: once here, once when Finding 20 concluded a signal was unobservable while
it sat unexamined in our own output.

**When a measurement contradicts a mechanism that has already been verified, suspect the
measurement first.** Findings 14 and 14b said the game floods reconnect requests during an uplink
block. It did. The counter was believed over the mechanism, and a working module was reported
broken.

---

## Validated: the downlink module, and why a timed resync was not enough

First `d2vpn hold 3074 --downlink` against a live game.

```
UL 0/1145 dropped   DL 312/807 dropped   resyncs 6
reconnect requests seen : 12
safety timeout reached at 25s, module disabled
```

**What worked.** Uplink was untouched (0 of 1145), so the hold is correctly directional. The
safety timeout fired on schedule. And the cross-direction trigger works: 12 requests seen and 6
resyncs opened, with the module watching *outbound* 128/136 while blocking *inbound* - the design
that Finding 20's withdrawal forced, never previously exercised.

**What did not.** Only **39%** of downlink was dropped, and an object picked up during the hold
registered instantly instead of deferring. Six windows of 3.5 s inside a 25 second hold is
21 seconds open: the module was passing traffic for most of the time it claimed to be holding.

### Finding 24 - a resync needs a cooldown, and must not be extendable

The game asks to reconnect roughly every two seconds while it is held. Opening a window on every
request leaves the module open almost permanently. The reference implementation does not behave
this way: roughly one ~4 s resync per 16 s of holding (Finding 10).

Two changes, the second found by a test written for the first:

1. **A cooldown.** After a window closes, a peer must stay blocked for a minimum period before
   another may open. Requests during it are counted and ignored.

2. **A fixed window length.** Extending a window on each new request looks obviously right - the
   game is still asking, so the exchange cannot have finished - and is a trap. Requests arrive
   every ~2 s and the window lasts 3.5 s, so an extendable window is refreshed before it can
   expire and **never closes at all**. A test simulating a persistent asker caught this
   immediately; the live run had not, because the safety timeout ended the hold first.

With a 3.5 s window and a 5 s cooldown the cycle is ~8.5 s, most of it blocking, which is close
to the measured reference behaviour.

**Still a timer.** This is a better-shaped approximation of what the reference does by detecting
when an exchange actually completes. The remaining error is in how long a window should last, and
the way to find out is to measure a real resync's duration rather than guess at 3.5 s.

---

## Finding 25 - buffering is not needed for state to arrive late

`d2vpn hold 3074 --downlink --no-resync`, a total block with nothing crossing: an object
interacted with during the hold **did not register until the module released**, then did.

This was predicted to fail. d2vpn drops packets rather than buffering them, and the expectation
was that the interaction would be lost outright rather than deferred - which was the entire
argument for building buffering next.

It was wrong. The deferral does not come from replaying held packets. Blocking downlink stops the
client learning anything; on release the server's ongoing state updates carry the result through.
The client does not need the original packets, it needs *current* state, and the server keeps
sending it.

So the ~99-packet dump measured at release (Finding 17b) is not evidence that replay is what makes
the mechanic work. It may be a buffer draining, or simply the client catching up on state - the
in-game outcome is the same either way.

**Superseded by Finding 36.** This held only for downlink, where the server keeps sending current
state and a dropped packet merely delays what the client learns. Uplink is the opposite: nothing
re-sends the player's actions, so a dropped action is destroyed rather than deferred. Buffering is
load-bearing there, and the reference limiter does it.

## Finding 26 - a resync leaks the very state a hold is meant to defer

The same hold *with* resync enabled did not work: the buff arrived immediately.

With a 3.5 s window and a 5 s cooldown the module blocked 53% of downlink during the hold - close
to the ~58% the cycle predicts, so the cooldown was working as designed. It does not matter. **A
pickup confirmation is a single packet**, and any open window is enough to carry it.

That is the real constraint, and it is tighter than "block most of the time":

| | Session survives | State deferred |
|---|---|---|
| No resync | **no** - dies at ~30 s | **yes** |
| 3.5 s window / 5 s cooldown | yes | **no** |

A working hold needs both, and **timing is the wrong axis**. No window can be long enough to
complete an exchange and short enough to carry no state, because the two travel together.

### Finding 26b - a resync should answer the handshake, not open the gate

The fix is to select by packet rather than by time: during a resync, pass the handshake and keep
blocking everything else. On 3074 downlink those sizes are **51 and 233**, both measured crossing
resync windows, with 233 never appearing in normal traffic at all.

This also corrects an earlier mistake. 51 and 233 were shipped as a downlink pass list, then
withdrawn (Finding 19) because a hold with resync *disabled* passed nothing at all - taken as
proof they were resync artefacts rather than necessary. The inference was backwards. With resync
off the exchange never starts, so those packets are never sent; their absence showed the handshake
had not happened, not that it does not need them.

They are neither ordinary traffic nor a standing exemption. They are the handshake, and they cross
only while a window is open.

Uplink keeps the open-gate behaviour for now: many sizes were seen crossing its resync windows -
1254, 1258, 720, 795 among them - so no short list is established there, and it is validated as
it stands.

`--resync-ms`, `--cooldown-ms` and `--no-resync` exist to find out empirically, and `hold` now
reports what fraction of the held direction was actually blocked during the hold, since a
whole-run percentage understates it by including the warmup.

---

## Validated: the downlink module

```
d2vpn hold 3074 --downlink --seconds 70 --warmup 10 --timeout-s 0

UL 0/816 dropped   DL 512/722 dropped   resyncs 12
reconnect requests seen : 24
blocked while holding   : 96% of 536 packets
```

Sixty seconds held - twice the ~30 s ceiling - **no error code**, and a buff picked up during the
hold did not arrive until the module released.

### Finding 27 - both halves at once, and what it took

Every previous configuration achieved one or the other:

| | Session survives | State deferred | Blocked |
|---|---|---|---|
| No resync | no, dies at ~30 s | yes | 100% |
| Resync opens the gate, 3.5 s / 5 s | yes | **no** | 53% |
| **Resync answers the handshake** | **yes** | **yes** | **96%** |

Two changes made the difference, both of them corrections to assumptions rather than new
mechanisms.

**Selecting by packet, not by time.** A window cannot be long enough to complete an exchange and
short enough to carry no state, because the two travel together. Passing only 51 and 233 while a
window is open keeps the block near-total - 96% against 53% - and still completes the exchange 12
times over the hold.

**Holding long enough for the test to be able to fail.** The 25 s safety timeout existed because
the downlink resync was unproven, and it was also what made proving it impossible: every earlier
run ended safely short of the ceiling, so "it survived" carried no information. The timeout is now
removed, since the mechanism it was guarding against is the one that works.

### The remaining 4%

512 of 536 packets blocked during the hold. The 24 that crossed are the handshake completing 12
times, which is the intended cost rather than leakage - it is what keeps the session alive, and it
demonstrably does not carry the state.

### Note on the corroborating signal

Uplink *fell* during this hold, 20.1/s to 10.2/s, where an uplink hold makes downlink rise. The
"peer sent harder" check therefore did not fire, and the run was correctly identified as working
only because the reconnect requests were counted. Rate direction is not a reliable signal on its
own; a fired resync is.

---

## Finding 28 - the uplink ceiling, confirmed on our own code

`d2vpn hold 3074 --no-resync --no-pass` - a total uplink block, nothing crossing at all - behaved
as intended and produced an error code at about 30 seconds.

That was predicted by Finding 14b, but from watching a reference limiter. It is now reproduced by
this project's own module, which makes the ~30 second ceiling a property of the game rather than
of any particular tool.

It also separates two flags that are easy to conflate. `--no-resync` stops resync windows opening;
the standing pass list is untouched, and uplink keeps passing its six handshake sizes regardless.
Only `--no-pass` as well gives a genuinely total block.

### What this does not settle: uplink's resync

Downlink's shape does not transfer. The blocked half of an exchange is whichever half travels the
held direction:

| | Standing pass list | Blocked half of the exchange |
|---|---|---|
| Downlink | nothing | the peer's replies - 51, 233 |
| Uplink | 36, 51, 128, 136, 233, 1166 | whatever follows the request |

For downlink, opening a window for 51 and 233 completes the exchange, which is why it works. For
uplink the request is *already* in the standing pass list and crosses with or without a resync -
so giving uplink a resync pass list of its handshake sizes would pass exactly what already passes,
making the window a no-op.

And that configuration is known to fail: with a reference limiter's resync disabled, 128 and 136
flooded at 3-4/s the whole time - passing freely - and the session still died at 30 seconds.
**Passing the handshake alone is not sufficient for uplink.**

So uplink's resync must pass something beyond the handshake, and what that is remains unknown. The
usual measurement cannot answer it: uplink's resync opens the gate fully, so every size seen
crossing a window crossed for that reason rather than out of necessity.

`--resync-pass` exists to find it by trial - widen until the session survives, then narrow to the
minimum, watching the blocked percentage.

---

## Validated: the uplink module, selectively

```
d2vpn hold 3074 --seconds 70 --warmup 10 --timeout-s 0 --resync-pass 51,233,720,795,1254,1258

UL 555/785 dropped   DL 0/465 dropped   resyncs 4
reconnect requests seen : 59
blocked while holding   : 96% of 580 packets
```

Sixty seconds, no error code, and enemies killed during the hold **reappeared every time** - the
server never learned of the kills. Both directions on 3074 now block ~96% while keeping the
session alive.

### Finding 29 - uplink needs more than its handshake, and a narrower list is open work

Uplink went from 70% blocked to 96% by passing a named list during a resync instead of opening the
gate. The list is `51, 233, 720, 795, 1254, 1258`.

**These are sufficient, not established as necessary.** They are the sizes seen crossing a
fully-open resync window, so they include whatever happened to be flowing as well as whatever the
exchange requires. The four large ones are the uncertain part: big enough to carry game state, so
if a kill ever travels at one of them it would pass. Narrowing the list while keeping the session
alive is the remaining work, and `--resync-pass` is how to do it.

Notably, 59 reconnect requests produced only 4 resync windows - the cooldown ignoring the rest -
and the session survived anyway. The exchange does not need every request answered.

### Finding 30 - a resync list replaces nothing; it adds

The standing pass list must keep applying while a window is open. Treating the resync list as a
replacement meant a reconnect request **opened a window and was then dropped by it**, because
128 is on the standing list and not the resync one - so the request never reached the peer it was
meant to reach.

The live run still worked, because windows are short and the request crossed freely outside them,
which is exactly the kind of bug that survives a passing test in production. It was found by a
unit test asserting that every signature size still passes.

---

## Measured: what a reference limiter actually lets out of an uplink hold

A capture of BitcoinMiner holding uplink for 30 seconds with its own resync enabled. **Twenty
packets crossed**, in three cycles of the same exchange:

```
t=31.4   136, 128, 51, 1166, 238, 238
t=41.5   233, 51, 1166, 238
t=51.2   136, 128, 51, 1166, 198, 198, 190, 190, 206, 174
```

The full permitted set: **51, 128, 136, 174, 190, 198, 206, 233, 238, 1166**.

### Finding 31 - the resync list was recording the wrong thing

d2vpn's uplink resync list was `51, 233, 720, 795, 1254, 1258`. Against the reference:

| Size | Reference lets out? |
|---|---|
| 51, 233 | yes |
| **720, 795, 1254, 1258** | **never, not once** |
| 174, 190, 198, 206, 238 | yes - and d2vpn had none of them |

The four large sizes came from watching *our own* resync window, which opens fully - so they
recorded what happened to be flowing rather than what the exchange requires. Being the largest,
they were also the likeliest to carry a kill through a hold, which is exactly the risk that
prompted the question.

This is the same error as Finding 26b, in the other direction. There, sizes were dismissed as
resync artefacts when they were the handshake. Here, sizes were adopted as the handshake when
they were artefacts. **A packet crossing an open gate says nothing about whether it needed to.**

### The exchange, read from the capture

```
in  128      the peer asks
OUT 136      answered 52 ms later
OUT 128
in   51
OUT  51
OUT 1166     the large data packet that completes it
OUT 238
```

The peer initiates during an uplink hold, which is the mirror of a downlink hold and was not
obvious: blocking uplink makes *us* go quiet, so the peer is the one that notices.

### Confirmed, by a number nobody was aiming at

Re-running the uplink hold with the corrected list: 95% blocked, no error code, enemies still
failing to register. The same as before on those measures - but the reconnect count collapsed.

| | Previous list | Observed list |
|---|---|---|
| Reconnect requests | **59** | **10** |
| Resync windows | 4 | 5 |

The game stopped asking. Slightly *more* windows opened and six times fewer requests were made,
which means the exchange is now completing rather than being retried. The previous configuration
survived on the volume of attempts rather than on any of them succeeding.

This is a better health signal than the blocked percentage, which barely moved (96% to 95%) across
a change that was clearly substantial. **A falling reconnect count means the resync is working; a
rising one means it is being retried.**

### Caveat

206 and 238 also occur in ordinary traffic - 45 and 22 times across 26 seconds of normal play -
so passing them during a window is not risk-free. The reference passes them anyway and the
mechanic works, so they are included, but they are the weakest part of the list. If a kill ever
does register during a hold, they are the first suspects.

---

## Finding 32 - short sequences and long holds are different problems

Bullet detach - holding uplink, swapping weapon, firing three times, releasing - is done in **under
three seconds**. Almost nothing established for long holds applies to it.

**The resync never runs.** The game takes roughly 3.5 seconds of being blocked before it sends a
reconnect request (Finding 2, where the first escape appeared at s12.6 of a hold that began at
s8.7). A sequence shorter than that never triggers one, so resync windows, their duration, the
cooldown and the resync pass list are all irrelevant here.

**The standing pass list is the only thing that can leak.** It passes unconditionally, resync or
not. If a shot packet happens to be 233 or 1166, it crosses and the bullet attributes normally -
which presents as a trick that mostly works and occasionally does not, with no discernible
pattern.

**Nothing needs to pass.** The ~30 second ceiling cannot be reached in three seconds, so a hold
this short has no reason to let anything through at all. `--no-pass --no-resync` is not a
diagnostic here, it is the correct configuration.

### Consequence

The module's defaults are tuned for a sustained hold, where the session has to survive. A short
sequence wants the opposite: total block, nothing passed, no exchange. Both are legitimate and
they do not share settings - the difference is whether the hold outlives the game's patience.

Hotkey polling was also reduced from 8 ms to 1 ms. At 60 fps, 8 ms is half a frame of slop at each
end of a three-second sequence, and one P/Invoke per key per millisecond is not measurable next to
a game.

---

## Finding 33 - a total block is not enough for a short sequence

`run 3074 --no-pass --no-resync`, holding for about three seconds at a time while performing a
bullet detach: **uplink was blocked essentially completely** - 147 of ~156 outbound packets
dropped across the holds, with nothing on any pass list - and the trick still only detached 2 of
3 shots.

So for this sequence the packets crossing are not the variable. Something else decides whether it
works.

### One hypothesis, untested

The game may batch what the player did and send it on its next tick, at roughly 10-20 per second.
If so, an action taken shortly before releasing leaves on a packet that goes out shortly *after*,
when the block is already gone. Three shots inside three seconds would put the last one in that
window.

`--untick-ms` keeps blocking past the keypress so this can be tested. **It has not been shown to
help**, and the hypothesis is a guess about timing, not something measured.

A previous version of this section claimed a reference limiter does the same thing via an
`UntickDelay` setting. That was wrong: `UntickDelay` belongs to its Swap and Damage macro modules
and governs how long the macro leaves a module ticked, not whether blocking continues past a
release. Reading intent off a configuration key is not evidence.

### A diagnostic that diagnosed nothing

The first attempt logged every packet that crossed during a hold, which for an uplink hold meant
hundreds of inbound packets that pass by design and never could have carried the shot. Eighty-odd
entries, all marked "UNEXPECTED" because they were being checked against a pass list describing
the other direction.

Logging is now restricted to the held direction. A diagnostic that reports things which cannot be
the cause is worse than none, because it looks like evidence.

---

## Finding 34 - the tool-side levers do not explain bullet detach

Bullet detach - hold uplink, swap weapon, fire three times, release, inside about three seconds -
remains inconsistent, and every setting that could plausibly matter has now been tried.

| Configuration | Result |
|---|---|
| Default pass list + resync | 2 of 3 shots, once |
| `--no-pass --no-resync` (total block) | 2 of 3, then 1 of 3, 1 of 3, 0 of 3 |
| `--untick-ms 500` (block past release) | **all failed** |

Uplink was measured at essentially 100% blocked during these holds - 147 of ~156 outbound packets
dropped, nothing on any pass list - so the packets crossing were never the variable.

### The release-delay hypothesis is dead, and so is the timing pattern behind it

Labelled trials had suggested it. Hold durations lined up with outcomes: 2.776 s and 3.001 s
worked, 2.572 s failed, with an unmarked 2.645 s between. Since the shots bunch at the end of a
hold, a shorter hold puts the last shot nearer the release, so the reading was that it leaves on
the next tick after the block lifts.

Holding past the keypress should then have helped. It made every attempt fail.

A later session of **eight** labelled trials removed the pattern entirely:

| Duration | Result |
|---|---|
| 3.023 s | failed |
| 2.924 s | failed |
| 2.820 s | failed |
| 3.119 s | failed |
| 2.796 s | failed |
| 2.753 s | failed |
| **2.883 s** | **worked** |
| 3.712 s | failed |

The single success sits between two failures 60 ms either side of it. The gap since the previous
release does not separate them either - 3.65 s for the success against 3.37-5.11 s across the
failures. The n=3 pattern was coincidence, and was treated as a lead when the sample could not
support one.

That is worth more than a null result. If extending the block makes it strictly worse, the
mechanic is not "prevent the action reaching the server" - something about *when* the block ends
matters, and blocking longer is not a safer version of blocking.

`--untick-ms` remains as an option and should not be recommended.

### What is ruled out

- Which packets pass - a total block behaves no better than the default pass list.
- The resync - it never fires in a three-second sequence.
- Hotkey latency - 1 ms polling made no difference.
- Blocking past the release - actively harmful.

### What is not

Whether this is tool-controllable at all. The same trick is reported as inconsistent with a mature
limiter, and nothing measured so far distinguishes an attempt that works from one that does not.
Settling it would need a packet capture aligned to labelled trials across many attempts, and the
honest possibility is that the variance lives in the game rather than in anything on the wire.

---

## Finding 35 - bullet detach is not distinguishable on the wire

Ten labelled attempts recorded against a simultaneous packet capture, clocks aligned: 3 marked
worked, 5 marked failed, 2 unmarked. Configuration recorded in the log - uplink, no pass list, no
resync, no untick delay.

| | worked (n=3) | failed (n=5) |
|---|---|---|
| Uplink during hold | **0** | **0** |
| Downlink during hold | 10.8/s | 10.1/s |
| Uplink flushed in 1 s after release | 11.0 packets, 8655 bytes | 11.8 packets, 7576 bytes |
| Hold duration | 2.82 s | 3.05 s |

**Uplink was zero in every attempt.** The block was total and identical whether the trick worked
or not, so nothing about the blocking distinguishes the outcomes.

No packet size separates the groups either. Checked across downlink during the hold, uplink
flushed in the second after release, and uplink in the second before the hold: no size appears in
every success and no failure, or the reverse. The per-group differences are consistent with two
samples of different size drawn from the same traffic.

Duration also reversed: the successes were *shorter* on average here, against longer in the n=3
sample that originally suggested a timing effect. That sample was noise.

### Conclusion, and its correction

Within what a capture on this machine can see, an attempt that works and an attempt that does not
are the same. Nothing measured separated them.

That was read as evidence the variance is game-side and not tool-controllable. **That reading was
wrong.** A reference limiter achieves 6 detached shots out of 9, consistently, on the same
machine - so it is controllable, and something in this project's configuration is worse.

The likely cause is a decision made here on bad reasoning. These trials all ran with
`--no-pass --no-resync`, chosen because a three-second hold cannot reach the ~30 second ceiling
and therefore "needs nothing to pass". But the reference limiter *does* pass traffic during an
uplink hold - 20 packets across 30 seconds, sizes 51, 128, 136, 174, 190, 198, 206, 233, 238 and
1166 (Finding 31) - and it is the one getting 2 in 3.

The best result with this tool, 2 of 3, matches that rate. Every session steered onto a total
block did worse.

So the comparison the whole investigation rested on was against a configuration that was never
shown to be right, and was in fact chosen against the only direct evidence available. Passing
nothing is not a stricter version of passing the handshake; it is a different thing, and the
measurement says it is worse.

---

## Finding 36 - the reference limiter buffers uplink; this project destroys it

Captured a reference limiter performing the bullet-detach sequence - the three-second hold that
actually works - rather than the thirty-second hold measured earlier.

| | Uplink during hold (2.4 s) | Uplink in 1.2 s after release |
|---|---|---|
| Attempt 1 | 1 packet | **46 packets**, 31 of 45 gaps under 5 ms |
| Attempt 2 | 1 packet | **57 packets**, 44 of 56 gaps under 5 ms |
| Attempt 3 | 0 packets | **65 packets**, 53 of 64 gaps under 5 ms |

At a 20/s baseline, roughly 48 packets should have left during each hold. Essentially none did -
and then about that many arrive at once, spaced in milliseconds. The burst also carries much
larger packets than ordinary uplink: 692, 827, 1254 against a normal range of 206-478.

**It blocks exactly as completely as d2vpn does, and then replays what it held. d2vpn drops.**

That is the entire difference, and it explains the gap: roughly 6 detached shots in 9 with the
reference, against roughly 1 in 6 here. For detach the shots must *arrive late*, after the desync,
so the server registers them against stale context. Dropping them means the shots never happened
at all.

### This corrects Finding 25

Finding 25 concluded buffering was unnecessary, on the strength of a downlink test: an object
picked up during a hold still registered on release, from a module that drops. That observation
was correct and the generalisation was not.

The two directions are not symmetric:

- **Downlink.** The server keeps sending current state, so dropping only delays what the client
  learns. The state arrives anyway.
- **Uplink.** Nothing re-sends the player's actions. A dropped action is destroyed, not deferred.

Buffering is therefore optional for downlink and **load-bearing for uplink**. The work was
dropped on the basis of a result from the one direction where it genuinely does not matter.

### What this asks for

Hold outbound packets while blocking and reinject them on release, rather than dropping them: a
third verdict beside pass and drop, queues of cloned packets, a drain on release, and a cap so a
stalled game cannot grow one without bound.

### Built - `--buffer`

Implemented as a third verdict, `PacketVerdict.Buffer`, which `HoldModule` returns in place of
`Drop` when `Buffering` is set. `PacketBuffer` holds the clones; `WinDivertTrafficController`
drains it on the hold-to-release transition.

Off by default. Dropping is the behaviour every measurement above was taken against, and turning
the reference limiter's approach on by inference rather than by a result here would make this
project's own findings untestable.

Decisions worth recording, since none of them is forced by the measurement:

- **One queue, not per-peer.** Replay order across peers does not matter - these are separate UDP
  conversations - and arrival order within a peer is preserved by a single FIFO anyway.
- **Capacity 2048.** About 100 s of uplink at the measured 20/s, roughly 3 MB of unmanaged memory.
  The sequences this is for last two to three seconds, so the cap only engages when something has
  already gone wrong.
- **Max age 30 s, oldest-first eviction.** The cap bounds memory, the age bounds usefulness: a
  position update thirty seconds stale is worse than useless on arrival. What matters on release is
  the most recent actions - the shots just taken - so the oldest are the ones to lose.
- **Replayed as fast as they will go.** The reference limiter's own bursts are millisecond-spaced
  and the game copes. Pacing would mean the last actions of a hold arriving later still, which is
  the opposite of what they are held for.
- **Drained on the receive thread, never from the hotkey.** Releases come from a hotkey thread;
  two threads writing one WinDivert handle is not worth finding out about. The drain also runs
  before the packet that woke the loop is sent, so held actions arrive ahead of the live one.
- **Discarded on disposal, not flushed.** Closing the handle is the kill switch and has to restore
  normal traffic at once, rather than first pushing a backlog of stale actions into a session that
  may already be in trouble.
- **A packet that failed to parse is still passed, not held.** Replaying something malformed into a
  live session is worse than the leak it avoids.

### Confirmed in game

15 detach attempts under `--buffer`: **12 worked.** Of the three that did not, all three were
called as execution - too slow through the swap-and-fire - rather than the tool failing.

| | Detached | Attempts | Rate |
|---|---|---|---|
| This project, dropping | ~1 | 6 | ~17% |
| Reference limiter | 6 | 9 | 67% |
| This project, `--buffer` | 12 | 15 | **80%** |

The attribution of those three misses is a judgement made in play, not a measurement, and 15
attempts is a small sample. Neither matters much here: the effect is a four-to-five-fold change
against a baseline that was barely working, which is far too large to be the sample or the
bookkeeping.

**Finding 36 holds end to end.** The mechanism was measured on the reference limiter, the
difference was reproduced by building it, and the rebuilt version performs at least as well as the
thing it was derived from. Nothing about the reference implementation was read to get here - only
its traffic.

The remaining failures are now in the player's hands rather than the tool's, which is the correct
place for them and the signal that this line of work is finished.

---

## Finding 37 - the detach preset disarms the only thing that survives a long hold

Reported symptom: a 3074 uplink hold error codes when held "a tiny bit longer". First hypothesis
was the packet buffer, on the grounds that it was the most recent change. **Wrong, and refutable
without a test** - the buffer only injects on the hold-to-release transition, so during a hold
buffering and dropping are network-identical, and an error code that arrives *while still held*
cannot have come from it.

The answer was already in a session log from the previous evening:

```
config  group=3074 direction=Uplink pass=[(nothing)] resyncPass=[51, 174, ...]
        triggers=[(nothing)] resyncMs=3500 cooldownMs=5000
```

`triggers=[(nothing)]` - `ResyncTriggerLengths` is empty, so `TryBeginResync` is never called.
That run had no resync at all: a total, permanent uplink block with nothing renewing peer liveness.
Every hold in the log is 2.5-3.6 s, so it never reached the ceiling. Held longer, it always will.

That configuration is `--no-pass --no-resync`, which this project recommended on screen for short
sequences. The recommendation was wrong in its second half.

### `--no-resync` is unnecessary even for detach

A resync only starts on an outbound packet of a trigger length. Measuring how often those occur in
ordinary traffic:

| Capture | Packets | `len=128` | `len=136` | Share |
|---|---|---|---|---|
| `detach-packets` | 4442 | 4 | 12 | **0.36%** |
| `bm-packets` | 4062 | 2 | 0 | 0.05% |

And they are not spread evenly. In `detach-packets` twelve of the sixteen fall between t=28.95 and
t=35.03 - the reconnect-request storm at the 30 second mark, i.e. the ceiling being hit inside that
very capture.

So an armed resync almost never fires inside a three-second hold, and costs nothing when it does
not. Disarming it buys a marginally more total block on short holds and guarantees an error code on
long ones. **`--no-pass` alone is the right setting**: it removes the only thing that can leak
during a short sequence while leaving the only thing that saves a long one.

### Consequence

The on-screen advice now recommends `--no-pass` alone, and a configuration with neither a resync
trigger nor a safety timeout prints a warning naming the ~30 s ceiling up front. A tool that lets
the operator select a configuration known to fail should say so at the point of selection, not
leave it to be rediscovered in a session.

### Procedural note

This is the third time on this project that reasoning forward from the most recent change lost to a
measurement that already existed. The log was on disk, unexamined, while a test plan was being
written to rediscover what it said. Check the recorded evidence before designing an experiment.

---

## Finding 38 - the uplink resync holds for 451 s across 35 cycles

The first properly instrumented long hold. `run 3074 --no-buffer` with stock defaults - standing
pass list and resync both armed, no safety timeout - held for **451.0 s (7.5 min) with no error
code**, and released cleanly with the session still live.

| | |
|---|---|
| Held | 451.0 s |
| Uplink seen / dropped while held | 4826 / 4463 - **92.5% blocked** |
| Uplink rate while held | 10.7 packets/s |
| Downlink dropped | **0** of 3710 |
| Reconnect requests | 70 |
| **Resync windows** | **35** |
| Wireshark leak filter | empty throughout |

**The 35 is the result, not the 451.** Surviving one long stretch is weak evidence; the renewal
exchange running to completion 35 consecutive times is the mechanism being demonstrated. One window
every 12.9 s, steady for the whole hold - the game asks, is answered, goes quiet, asks again.

Requests to windows is exactly 2.0:1, which is expected rather than lossy: each reconnect attempt
sends both trigger sizes, 128 and 136, and the second falls inside the 5 s cooldown of the window
the first opened. `TriggersSeen` counts requests, `ResyncsStarted` counts windows.

### What crossed, against what the reference limiter permits

359 packets crossed in 451 s - 0.8/s against 9.9/s dropped. Normalised per resync window, and set
beside the reference limiter's own cycle recorded in `HoldModuleOptions`:

| Size | Count | Per window | In reference cycles |
|---|---|---|---|
| 1166 | 75 | 2.1 | all 3 |
| 51 | 60 | 1.7 | all 3 |
| 198 | 54 | 1.5 | 1 of 3 |
| 128 | 35 | 1.0 | 2 of 3 (trigger) |
| 136 | 35 | 1.0 | 2 of 3 (trigger) |
| 190 | 34 | 1.0 | 1 of 3 |
| 174 | 30 | 0.86 | 1 of 3 |
| 238 | 17 | 0.49 | 2 of 3 |
| 206 | 10 | 0.29 | 1 of 3 |
| 36 | 8 | 0.23 | - |
| 233 | 1 | 0.03 | 1 of 3 |

51 and 1166 lead, and they are the two sizes present in every reference cycle. Nothing outside the
two configured lists crossed at all, in the tool's own accounting or in Wireshark. So this is not
merely "a hold that survived" - it is the reference exchange reproduced, at the same shape.

Exactly one 233 in seven and a half minutes, against its presence in the standing pass list, looked
like near dead weight. **Withdrawn - see finding 39**, where a second hold saw four in 295 s. The
claim was drawn from n=1.

### This corrects Finding 9

Finding 9 recorded the 3074 uplink ceiling as "none known", evidenced by "held over 20 minutes with
no disconnect" - a single casual observation from an early session, taken before the resync pass
list was twice corrected, with no counts attached. It was then quoted in this project as though it
were a requirement, which set a 20-minute target that nothing derived.

The ceiling is still "none known", but now on evidence: 451 s, 35 completed cycles, 92.5% blocked,
zero leaks. A shorter run that demonstrates the mechanism cycling is worth more than a longer one
that only reports an absence.

### Downlink was untouched

0 of 3710 inbound packets dropped during an uplink hold. Worth recording because the direction gate
has been wrong before in the other direction, and because it is what makes a uplink hold safe to
run long: the client keeps learning the world state throughout.

---

## Finding 39 - buffering is safe at length, and the age limit is the operative one

A second long hold, this time on stock defaults with the buffer on: **294.9 s held, released
cleanly, no error code, and no in-game symptom** beyond a visible burst in Wireshark at the moment
of untick.

The counters reconcile exactly, which is worth stating because this is the first run where the
buffer's own accounting could be checked end to end:

```
buffered 2726  ==  uplink dropped 2726
evicted   678  +  held at release 2048  ==  2726          (2048 == capacity)
expired  1796  +  replayed        252   ==  2048
```

### The receive-loop stall hypothesis is dead

252 packets drained in **13.6 ms** - 54 us per packet. Extrapolated, a full 2048-packet drain would
take ~111 ms, during which roughly 2 packets arrive against WinDivert's 4096-deep queue.

That was one of the two mechanisms proposed for why a long hold might error code. It cannot happen
at these rates, and it was never the cause of anything - see finding 37 for what was. The
instrumentation built to measure it stays, because the number is now known rather than assumed.

### The age limit does the work; capacity is only a memory bound

**87.7% of the buffer was discarded as stale** (1796 of 2048). At the measured 9.2 blocked
packets/s, the 30 s age limit can only ever admit ~277 packets, while the 2048 capacity holds 222 s
worth - **7.4x more than the age limit can use**.

So the two limits are not peers. Capacity bounds memory against a rate spike; age decides what is
actually delivered. Sizing the capacity only matters for the worst-case rate: at the 20/s seen in
earlier captures, 30 s is ~600 packets, so 2048 keeps roughly 3x headroom and is left alone.

### This refutes a change proposed earlier in this project

On suspicion that a large backlog was error coding long holds, dropping `BufferMaxAge` from 30 s to
~3 s was proposed, on the reasoning that the detach sequence is under 3 s so nothing older could be
useful. That reasoning was sound and the premise was false: the backlog was never the cause, and
252 replayed packets - 5x anything previously tested - produced no symptom at all.

**30 s stays.** No measurement asks for a shorter one, and the argument for it was built on a
hypothesis that finding 37 had already killed.

### The resync exchange varies between runs

Both long holds, normalised per resync window:

| Size | 451 s / 35 win | 295 s / 23 win | |
|---|---|---|---|
| 1166 | 2.14 | 1.57 | |
| 51 | 1.71 | 1.91 | |
| 128 | 1.00 | 1.00 | trigger, 1.0 by construction |
| 136 | 1.00 | 1.00 | trigger, 1.0 by construction |
| 198 | 1.54 | **0.17** | 9x |
| 190 | 0.97 | **0.17** | 6x |
| 238 | 0.49 | **0.00** | absent entirely |
| 174 | 0.86 | 0.74 | |
| 206 | 0.29 | 0.09 | |
| 36 | 0.23 | 0.30 | |
| 233 | 0.03 | 0.17 | |

The triggers and the always-present sizes (51, 1166) are stable. 198, 190 and 238 swing by up to
9x, and 238 does not appear at all in the second run.

That is consistent with the reference limiter's three recorded cycle variants rather than a single
exchange: 198, 190 and 206 occur in only one of the three, 238 in two, while 51 and 1166 are in all
three. **A pass list cannot be validated from one run** - a size absent from a session is not
thereby unnecessary, it may belong to a variant that session did not hit.

Requests to windows held at exactly 2.0:1 in both runs (70:35, 46:23).

### Correcting the "233 is dead weight" note in finding 38

Finding 38 observed one 233-byte packet in 451 s and suggested it was close to dead weight in the
standing pass list. This run saw four in 295 s. Combined: **one per 149 s** - rare, but real and
four times more frequent than the single sample implied.

Drawn from n=1, which is the same error as validating a pass list from one run, in the same
document that warns against it.

---

## Finding 40 - "slow" names two different mechanisms, and a uniform delay is a lag injector

`run 3074 --slow` at 2000 ms was tested in game. **Wireshark confirms the delay line works** -
uplink packets leave ~2 s after they were produced, in order, nothing dropped. In game the effect
was visible **once** across a whole session.

The mechanism is correct and the design was the wrong one. The prior art has two modules called
"slow" and this project conflated them:

| Module | Mechanism | Built here |
|---|---|---|
| `З0k Slow` | "delays all inbound packets from 30k by a fixed number of seconds" - a blanket delay line | **yes**, as `--slow` |
| `Damage UL Slow` | `TriggerLength` (default 184) plus a **2000 ms untick delay after a damage trigger fires** | no |

`Damage UL Slow` is not a delay line at all. It watches for a packet of a calibrated length, then
holds uplink for a fixed duration and **releases itself**. That is the block-then-dump shape, fired
automatically at the moment damage is dealt.

### Why a uniform delay does little

A constant offset applied to every outbound packet is indistinguishable from latency, and the game
compensates for latency - the server corrects the client's position rather than accepting a stale
one. Nothing about a steady 2 s offset presents a contradiction for it to mishandle.

What worked, at 12 detached shots in 15, was a **discontinuity**: block completely, then deliver the
backlog at once, so actions arrive with a coherent gap behind them. Finding 36 measured exactly that
in the reference limiter and nothing else. A delay line has no discontinuity except at untick, which
is the one moment a delay line and a hold behave identically.

So the two are not variations in strength. **A hold desynchronises; a delay adds lag.**

### The 2000 ms default was borrowed from the wrong mechanism

`SlowDelay` defaults to 2 s, justified here as a starting point between finding 7's measured 3.91 s
and 5.94 s gaps. It sits below both, and 2000 ms is also - exactly - `Damage UL Slow`'s untick
delay, which is a hold duration rather than a delay depth. If the delay line is to be tested
faithfully against the only measurement of one that exists, **4000-6000 ms is the range**, not 2000.

### Consequence

The delay line is what 3074 uplink slow is, by decision: uplink delayed by a configured amount, the
whole buffer dumped on untick, nothing dropped. That is the specified behaviour and it is what is
built.

So this finding does not ask for a change. It records why the in-game effect of a *uniform* delay is
small, which bounds what the module is for: it is a lag injector, not a desync tool, and the two are
different instruments. `--slow` is also the faithful reproduction of `З0k Slow`, whose own README
describes it as being for holding reconnects - so the same code is already the right shape for a 30k
module later.

`Damage UL Slow`'s triggered self-releasing hold remains unbuilt and is noted here only so the
distinction is not lost again. It is a separate module, not a correction to this one.

---

## Finding 41 - measured: the reference limiter's slow is a 1 packet/second rate cap

A capture of BitcoinMiner's 3074 uplink slow, filtered to outbound packets outside both pass lists
- so purely the traffic the module governs.

| Phase | Packets | Behaviour |
|---|---|---|
| Before the toggle | 16 in 0.540 s | **29.6/s**, gaps 18-75 ms, mean 36 ms |
| **Slow on, ~7.9 s** | **7** | gaps 1.1147, 1.0044, 0.9424, 1.0035, 1.0066, 1.0091, 1.0065 s |
| The untick dump | 13+ in **178 us** | gaps 10-37 us |

### This settles the mechanism

At the pre-toggle rate, about **235 packets** should have been produced during that hold. **Seven
left.** A fixed delay preserves the input rate - it would have released ~235, one second late. A
rate cap releases one per second whatever arrives, which is what happened.

So the reference limiter's slow is a **throttle**, confirmed by measurement rather than inferred
from reading HarryWatch's source. And the period is 1.00 s, which is exactly HarryWatch's
`DelayPacket(1s)` constant: the mechanism and the value were both inherited.

That also closes finding 40's open question. d2vpn's `--slow` was a fixed offset, which is why a
uniform 2000 ms produced one visible in-game effect in a whole session: it was reproducing
latency, and the game compensates for latency. `--slow-throttle --slow-ms 1000` is this.

### The dump is unpaced, again

13 packets inside 178 microseconds, spaced 10-37 us. The third independent observation of the same
thing (findings 4, 7, and HarryWatch passing 0 for its flush pacing after trying 1000 / 20 / 3).
**No tool in this family paces a release.**

The dump began 0.838 s after the last throttled packet - inside the 1 s period, so the untick landed
before the next scheduled release rather than triggering one.

### Two things this does not establish

- **Whether the backlog is queued or sampled.** Only 13 dumped packets are visible before the
  screenshot ends. If ~228 follow, it is a FIFO throttle like the one built here; if the dump is
  only those 13, the module keeps the newest and discards the rest, which is a different design.
  The alternating large/small sizes in the dump match the pre-toggle traffic pattern, which weakly
  favours a FIFO, but a longer capture would settle it.
- **Whether the cap is per peer.** Only one peer is present in this capture, so 1/s is 1/s either
  way. HarryWatch's source keys per peer and this project follows it.

### Period jitter

Against a strict chain from the first release, the gaps drift by up to -53 ms and the mean period is
1.0125 s. Small, and consistent either with a drain driven by packet arrivals rather than a timer -
as here - or with the `RateLimiter` library the prior-art notes record. Not worth fitting more
closely than "one per second".

---

## Finding 42 - `--slow-throttle` reproduces the reference limiter on the wire

Same capture and same filter as finding 41, this time running
`run 3074 --slow-throttle --slow-ms 1000`.

| | d2vpn | Reference limiter |
|---|---|---|
| Packets released during the hold | **7** | **7** |
| Gap mean | **1.0268 s** | 1.0125 s |
| Gap spread | **0.102 s** | 0.172 s |
| Individual gaps | 1.098, 0.998, 1.029, 0.996, 0.999, 1.007, 1.061 | 1.115, 1.004, 0.942, 1.004, 1.007, 1.009, 1.007 |
| Untick dump | 14 visible in 404 us | 13 visible in 178 us |

The period matches to within 14 ms of mean, and this implementation holds it *more* tightly - about
half the jitter. That is expected rather than flattering: the chain here is computed from the
previous packet's scheduled release, so error does not accumulate, whereas the reference drifts up
to -53 ms against a strict chain (finding 41).

**The mechanism is reproduced.** Derived from a traffic measurement plus a reading of a different
tool's constants, with no reference implementation of the throttle consulted.

### The backlog behaved as designed

The throttle ran 8.46 s against an input of 10.7/s, so ~90 packets were produced and 7 left: ~83 in
the dump, the oldest about 8.5 s behind by the time it was sent. Comfortably inside the 30 s age
limit, so nothing expired - consistent with what that limit implies, that a throttled hold starts
discarding once its tail reaches `BufferMaxAge`, which at this rate is around 30 s of holding.

### The input traffic differed between the two captures

| | Finding 41 | Finding 42 |
|---|---|---|
| Pre-toggle rate | 29.6/s | 10.7/s |
| Pre-toggle gaps | 18-75 ms, irregular | 96-104 ms, mean 100.5 |
| Payload sizes | 110-550, mixed large and small | 694-766, uniformly large |

A different game state, not a difference between the tools - the throttle governs output rate, and
output was 1/s in both. The 100.5 ms regularity is worth noting on its own: a 10 Hz uplink tick,
where finding 41 caught a moment carrying roughly three times that in mixed-size traffic. Neither
capture is a baseline for the other.

---

## Finding 43 - the 7500 hold works, and TCP needs a different design from UDP

First working TCP module. `run 7500 --hold-all --buffer`, validated over three sessions.

| | |
|---|---|
| Longest hold | **44.7 s**, ended by the operator |
| Segments withheld | 11 |
| **Retransmissions suppressed** | **14** |
| Peer resets | **0** |
| Inbound withheld / seen | 25 / 70 |
| Outbound touched | **0** of 49 |

The suppressed count is the result. Withholding a TCP segment does not remove it - the sender
re-sends the same data under the same sequence number - so a block that only drops has leaked by
the first retransmission. Fourteen suppressed across a 44.7 s hold is the mechanism working; the
earlier sessions, whose holds ran one to two seconds, never saw one because this channel's
retransmissions arrive about 3.4 s apart.

### The two-phase capture does not fit this channel

The reference implementation withholds until the sender goes quiet for 100 ms, then keeps that
slice and passes new data. Measured here, 7500 carries about **2.3 packets a second**, so a gap over
100 ms is simply the spacing between updates: the capture fires after the first segment and
everything later passes.

Seven consecutive holds each withheld **exactly one** segment. With `--hold-all`, which never
captures, withheld tracked inbound-dropped exactly across 26 toggles.

That constant was tuned for a burstier stream. It is inherited, not measured, and it does not
transfer.

### The 1460-byte capture limit is unreachable here

Reported `capture limit reached: 0` in every session, including a 231 s one. 7500 segments are
small - 21 to 278 bytes payload in the captures - so a withheld slice never spans a maximum
segment.

That mattered more than a diagnostic. The hold ceiling was originally measured from capacity, so it
never armed at all, and a 92 s hold ran until the peer reset the connection. The ceiling now runs
from the first withheld segment, which is also when a peer's patience actually starts. **A ceiling
gated on a condition the traffic does not meet is not a ceiling.**

### Retransmissions are coalesced, and keep their sequence number

The captures show the sender repacking on retransmit: original segments of 21 and 27 bytes come
back as a single 482-byte retransmission starting at the same sequence number.

So matching on sequence number alone is right, and matching on length would not be. Recorded
because the opposite is a reasonable guess.

### An adapter capture cannot show what was blocked

npcap captures below the filter this tool installs, so an inbound capture shows every segment that
reached the adapter - including the ones dropped immediately afterwards. Seeing a retransmission in
Wireshark is not evidence the hold leaked.

Blocking is visible only indirectly, in the acknowledgement numbers this machine sends back: they
stall during a hold and jump in a burst after release. Three sessions were read this way before the
point was noticed, which is worth stating plainly for the next person.

### What is still inherited rather than measured

`CaptureQuiet` 100 ms, `CaptureBytes` 1460, `MaxHold` 54 s, `QuietHold` 19 s, `QuietRetransmit`
24 s. Of these only the ceiling has any evidence behind it here, and that evidence is one reset at
92 s. 7500 has never been calibrated on this machine.

---

## Finding 44 - measured: the 7500 ceiling is between 75 and 87 seconds

Run with both ceilings disabled, to find where the real limit is rather than trust an inherited
one.

| Hold | Length | Outcome |
|---|---|---|
| 1 | **86.5 s** | peer reset the connection |
| 2 | **75.2 s** | released by hand, connection recovered |

So a 7500 hold survives at least 75 s and is dead by 87 s. **The first number in this module with
any evidence behind it.**

The reference implementation gives up at 54 s, which is conservative here by around thirty seconds.
That figure stays as the default - it is a safety margin and being early costs nothing - but it is
now known to be a margin rather than a limit.

### What stops at 25 seconds is the counters, not the effect

An earlier run ended at 52 s with "no retransmission for 27 s", meaning the peer stopped re-sending
about 25 s into the hold. Both long holds withheld **exactly one segment**: the first one blocks the
sender's window, so nothing new ever arrives, and the 8-9 suppressions are that same segment being
re-sent with doubling gaps until the sender gives up.

So after ~25 s nothing arrives at all and `withheld` and `resent` stop moving.

**This was first recorded here as "the useful window ended at 25 s", which is wrong.** The game is
deprived of API data for every second the hold runs, from the first one to the last. What stops
growing is the *amount withheld* - one segment either way - not the *duration of deprivation*, and
for anything duration-based, which is most of what this module is for, that duration is the effect.

| | |
|---|---|
| 0-25 s | the peer retransmits, suppressions accumulate |
| 25-87 s | the peer is silent, counters frozen, **the game is still deprived** |
| ~87 s | reset |

The consequence is that the module should not pick a length. It holds until the operator unticks or
the peer resets, with a ceiling available for anyone who wants one.

### Recovery from a 75 second hold is possible

The second hold ran 75.2 s and released cleanly, with the connection carrying on. So the window
between "inert" and "fatal" is large, and unticking inside it recovers rather than error codes.

### A bug this run exposed

The run reported `replayed 0, expired unsent 2`. The buffer's age limit had been derived as
`MaxHold + 10 s`, and `--no-ceiling` sets `MaxHold` to zero - so the age collapsed to 10 seconds
against a 75 second hold and everything expired before the release. Nothing was replayed at all.

The visible burst of traffic after the release was therefore the peer's own retransmissions
resuming, not this tool's replay. Worth recording because the two look identical from a capture,
and the wrong one was nearly credited.

---

## Finding 45 - replaying the withheld slice is what makes a 7500 hold recover

The buffer had never actually replayed anything on this port across four sessions: the first
attempt tied its age limit to the hold ceiling, and `--no-ceiling` set that to zero, so everything
expired. With the age limit removed entirely:

| | |
|---|---|
| Hold | 61.4 s |
| Segments withheld | 3 |
| **Replayed** | **3** |
| **Acknowledgement brought forward** | **3 of 3** |
| Expired / evicted unsent | 0 / 0 |
| Peer resets | 0 |

Recovery after unticking was **visibly faster** than the earlier runs where the segments expired -
which is the whole point, since without a replay the data arrives only when the sender next
retransmits, and after backoff that is most of a second away or longer.

**Every replayed segment needed its acknowledgement rewritten.** Not a decoration: a segment held
for a minute acknowledges a position the connection passed long ago, and three duplicate
acknowledgements are the fast-retransmit signal, so replaying them verbatim would have had this
machine re-sending data the peer already had.

### An ordered stream does not want an age limit

The limit exists because UDP game state is superseded - a position update half a minute old is
worse than useless on arrival, so discarding it avoids delivering something wrong. TCP has no
equivalent: the receiver is still waiting for that exact sequence number and can deliver nothing
after it until it arrives. A withheld segment is not stale, it is **next**.

So discarding it avoids nothing and leaves the receiver stuck until the sender retransmits. 7500
now bounds the buffer by capacity alone.

In practice the capacity never binds either. The first withheld segment closes the sender's window,
so a hold holds one to three segments against a limit of 2048.

### Both behaviours are now defaults

`--hold-all` and `--buffer` were both opt-in while unproven. Both are measured now, so both are on,
with `--capture-slice` and `--no-buffer` to get the reference implementation's behaviour back.

Worth noting what the reference preset now means: two-phase capture, no buffering, and ceilings at
54 / 19 / 24 s. Every one of those was inherited at the start of this module and every one has since
been measured to be wrong for this channel - the capture window because the channel is too quiet,
the ceiling because a reset came at 86.5 s, and the buffering because recovery is visibly better
with it.

### One caveat carried forward

A replayed segment could in principle be rejected by PAWS, which discards segments whose TCP
timestamp option is older than expected. This connection does not use timestamps - its bare
acknowledgements are 40 bytes, so IP and TCP headers with no options - so it does not arise here.
The tell, if it ever did, would be a healthy `replayed` count with no visible recovery.

---

## Finding 46 - measured: a reconnect is one visible 40-byte reset, and recovery takes 6 s

A capture of the reference limiter's Reconnect on 30k, filtered to resets and connection setup.

```
19:34:06.425  <tunnel> -> <server>   40  59114 > 30000 [RST] Seq=489 Win=6146 Len=0
19:34:12.428  <tunnel> -> <server>   52  59460 > 30000 [SYN] Seq=0 Win=64860
19:34:13.443  <tunnel> -> <server>   52  [TCP Retransmission] 59460 > 30000 [SYN]
19:34:13.479  <server> -> <tunnel>   52  30000 > 59460 [SYN, ACK]
```

| | |
|---|---|
| Reset to first reconnect attempt | **6.003 s** |
| That attempt going unanswered, retransmitted | 1.015 s later |
| Reset to the connection actually being back | **7.054 s** |

### Recovery is a few seconds, and varies

HarryWatch's source carries `// inbound rst // solo 6 sec // fireteam 40 sec` as a comment on the
inbound reset. Three reconnects measured solo, over five minutes of one session:

| Killed | Game opened | Reset `Seq` | `Win` | To first SYN | Fully back |
|---|---|---|---|---|---|
| 59114 | 59460 | 489 | 6146 | **6.003 s** | 7.054 s (first SYN unanswered) |
| 59460 | 59574 | 265 | 6143 | **2.813 s** | 2.839 s |
| 59574 | 59605 | 111 | 6145 | **4.228 s** | 4.264 s |

**Range 2.813 to 6.003 s, mean 4.35 s, a spread of 2.1x.**

The three are a chain - each reset kills the connection the previous one caused the game to open -
so they are directly comparable and the variance is real rather than a difference in conditions.

**An earlier version of this finding reported only the first and said "the figure holds".** That was
n=1. The comment's 6 is the right order of magnitude and the top of the observed range, not a
constant to build on.

The 40 s fireteam case is untested here.

### The reset shape is identical across all three

40 bytes, `Len=0`, `[RST]` with no acknowledgement, every time. The window is 6143-6146 throughout,
which is the connection's own receive window being carried through from the sample rather than
invented.

Worth noting as a contrast: ordinary stack-generated resets on unrelated connections in the same
captures - an HTTPS server closing a socket, for instance - show as **`[RST, ACK]`**. A reset with
the acknowledgement flag clear is the unusual one, and is what both reference implementations and
this project send.

### Only one reset is visible, which settles finding 6

Exactly one reset appears, outbound, for one connection. Two are injected per connection - one to
the peer and one delivered up this machine's own stack - and the inbound one never reaches the
adapter for npcap to see, because it originates above the capture point and travels upward.

**An adapter capture cannot verify inbound injection.** A tool reporting two resets per connection
while a capture shows one is correct. That closes the open question on finding 6, which had guessed
exactly this without being able to confirm it.

### The reset is a bare header

**40 bytes, `Len=0`, and `[RST]` rather than `[RST, ACK]`** - an IPv4 header and a TCP header and
nothing else, no acknowledgement.

This project's first implementation set the flag on a clone of the sampled packet, which would have
sent a reset carrying that sample's payload, around 150 bytes. A reset must not carry data and
receivers discard one that does, so it would have been silently ignored. Corrected before it was
ever run, on the strength of this capture.

`Seq=489` is a real position in the stream rather than zero, so the sequence matters: a reset is
only acted on when its sequence sits inside the receiver's window. This project sequences at the
sample's own sequence plus its payload - the next byte that side would have sent.

### What was not observed

Finding 6 saw a *different* server appear 42 s after the reset, which it read as the actual instance
change. Nothing equivalent appears here, and the capture may simply not have run long enough.

---

## Finding 47 - d2vpn's reconnect is indistinguishable from the reference on the wire

First run of `d2vpn rc`. The reset went out, the connection died, and the game reconnected.

```
19:50:13.2946  <tunnel> -> <server>   40  59913 > 30000 [RST] Seq=53 Win=6145 Len=0
19:50:13.5908  <tunnel> -> <server>   52  59921 > 30000 [SYN] Seq=0 Win=64860
19:50:13.6168  <server> -> <tunnel>   52  30000 > 59921 [SYN, ACK] Seq=0 Ack=1
```

Six resets across both tools, every one the same shape:

| Tool | Bytes | Payload | Flags | `Seq` | `Win` |
|---|---|---|---|---|---|
| **d2vpn** | 40 | 0 | `[RST]` | 53 | 6145 |
| **d2vpn** | 40 | 0 | `[RST]` | **11830** | 6143 |
| Reference | 40 | 0 | `[RST]` | 489 | 6146 |
| Reference | 40 | 0 | `[RST]` | 265 | 6143 |
| Reference | 40 | 0 | `[RST]` | 111 | 6145 |
| Reference | 40 | 0 | `[RST]` | 105 | 6145 |

Identical on every observable field. The window is carried through from the sampled packet, landing
in the same 6143-6146 band across both tools because it is the same connection's window.

**The sequence numbers vary by two orders of magnitude and that is not a difference between the
tools.** `Seq` is relative, so it says how much that connection had sent before it was killed: 105
is a connection that had barely spoken, 11830 one that had sent nearly 12 KB. Each reset sits at
the end of whatever its own connection had sent.

That 11830 is worth its own note. It is the only sample taken from a long-lived connection, and it
confirms the sequence rule here tracks the real stream position rather than sitting near zero - if
the rule were wrong, a connection this far along is exactly where it would show.

Exactly one reset is visible, as predicted: two are injected per connection and the inbound one
never reaches the adapter.

### Recovery timing says nothing about either tool

| Sample | Reset to reconnect |
|---|---|
| **d2vpn** | **0.296 s** |
| Reference | 0.330 s |
| Reference | 2.813 s |
| Reference | 3.071 s |
| Reference | 4.228 s |
| Reference | 6.003 s |

The reference alone spans 0.330 to 6.003 s, mean 3.29 s across five samples - an **18x spread
within one tool**. d2vpn's single 0.296 s is the fastest recorded and sits just below the
reference's fastest, which is not a meaningful gap against that spread.

So this separates nothing. It is evidence that the game's reconnect delay varies enormously, and
that five samples of one tool and one of the other cannot distinguish them.

What the timing does establish is that recovery happens at all, and quickly enough to be usable.

### What this validates

The whole path, end to end: sampling a live connection, constructing a reset from it rather than
cloning, sequencing it where the receiver's window is, recalculating the checksum, and injecting in
both directions. Any of those being wrong would have produced a reset that was silently ignored -
the failure mode here is nothing happening, not an error.

Built from a reading of two reference implementations' constants plus three captures of one of them
in action, with no reference implementation of the injection consulted.

---

## Finding 48 - "fireteam 40 sec" is not TCP reconnect time

`d2vpn rc` run in a fireteam, to test the one case where the two implementations might diverge.

```
23:55:27.0815  <tunnel> -> <server>  40  49733 > 30000 [RST] Seq=281 Win=6145 Len=0
23:55:29.4120  <tunnel> -> <server>  52  49927 > 30000 [SYN] Seq=0 Win=64860
23:55:29.4520  <server> -> <tunnel>  52  30000 > 49927 [SYN, ACK]
```

**2.331 s to the reconnect attempt, 2.371 s fully back.** Against a solo range of 0.296 to 6.003 s
across six samples, that is unremarkable - the middle of the distribution.

HarryWatch's source carries `// inbound rst // solo 6 sec // fireteam 40 sec`. If that described
reconnect timing, a fireteam would be an order of magnitude slower. It is not.

### So the comment describes something the wire cannot show

The 30k connection comes back in about two seconds whether solo or in a fireteam. Whatever is worth
40 seconds in a fireteam happens above TCP - rejoining the activity, being pulled back to the
instance, the fireteam reforming - and none of it appears in a capture of port 30000.

**This invalidates a test proposed earlier in this project.** Having noted that the inbound reset is
unverifiable by capture, the suggestion was to run in a fireteam and check whether the disconnect
lasted ~40 s, on the theory that the inbound reset is what produces the longer effect. That cannot
work: the packet-level reconnect is the same either way, so the capture says nothing about whether
the inbound reset landed.

The inbound injection remains unverifiable from the adapter, and now also unverifiable from
reconnect timing. If it is ever to be confirmed it will have to be from in-game behaviour, or by
instrumenting the local stack rather than the wire.

### The reset shape is unchanged in a fireteam

40 bytes, no payload, `[RST]`, `Win=6145` - the same band as all six previous samples, on a
different server, and in a different netblock from the one seen solo.

---

## Finding 49 - 30k error codes at ~18 s, and the reference has a resync for it

`run 30k` blocking inbound reaches an error code at around **18 seconds**. The reference limiter
behaves the same way at the same point, so this is the game's tolerance rather than an artefact of
either implementation.

| Port | Ceiling | What ends it |
|---|---|---|
| 3074 uplink | none known | nothing - 451 s measured, resync keeps the peer alive |
| **30k inbound** | **~18 s** | error code |
| 7500 inbound | ~87 s | peer resets the connection |

Three TCP-or-not distinctions that do not line up with each other: 3074 survives indefinitely
because its resync answers the peer, 7500 lasts a minute and a half because nothing depends on it
urgently, and 30k gives up in under twenty seconds because it carries the world state the game
cannot proceed without.

### The reference has an autoresync for 30k and this project does not

Reported: the reference limiter offers an autoresync option on 30k that allows a longer hold. That
mechanism is unknown here. It cannot be the 3074 one, which answers a UDP reconnect exchange that
does not exist on a TCP port.

It holds **over three minutes** with the reference's resync on, so whatever it does is effectively
indefinite rather than a longer bounded window.

**Holding a slice is not it.** `--capture-slice` - the two-phase capture built for 7500, which
withholds until the sender goes quiet and then keeps only that slice while later data passes -
changed nothing. Still ~18 s. Ruled out.

Remaining candidates:

- **Release periodically.** Drain the queue every few seconds and resume, so the game receives an
  update and its timer resets - the block / resync / block shape that works on 3074, rebuilt for a
  stream. Would show as the outbound acknowledgement jumping forward in steps.
- **Acknowledge on the game's behalf.** Inject outbound acknowledgements covering data that was
  withheld, so the sender never times out while the game still receives nothing. This would keep a
  connection alive indefinitely, which matches three minutes better than a periodic release does.

### The outbound acknowledgement is the only honest diagnostic

npcap sits below the filter, so an inbound capture shows every segment that reached the adapter
whether or not it was dropped afterwards - it cannot say what the game received.

Outbound can. This machine's stack only acknowledges data it actually took, so the acknowledgement
number in outbound packets is a direct readout of what got through. A hold that is working keeps it
**stuck**; anything that advances it is data reaching the game.

And if acknowledgements are being injected rather than earned, the local stack will still be asking
for the segment it never got - so the two would interleave, and outbound acknowledgement numbers
would jump about rather than climb. That is the fingerprint to look for.

Until one candidate is measured, `--safe` on 30k caps at 14 s rather than the 60 s used for 7500.

---

## Finding 50 - 30k runs on a 73-byte heartbeat every 3 seconds

Outbound capture of the reference limiter holding 30k **with its autoresync on**, filtered to
`tcp.dstport >= 30000 && tcp.dstport <= 30009` so the acknowledgement numbers read out what the
game's own stack actually accepted.

Across 54 seconds, the acknowledgement advanced **eighteen times, by exactly 73 bytes, every
3.02 s** - range 2.90 to 3.30. Not once did it stall.

| | |
|---|---|
| Step size | 73 bytes, every time |
| Interval | 3.02 s mean, 2.90-3.30 s |
| Total acknowledged | 1314 bytes in 54 s |

So inbound 30k is a **heartbeat**: a fixed 73-byte message on a three-second timer. That is the
rhythm the whole port runs on, and it explains the ~18 s ceiling measured in finding 49 - roughly
six missed beats and the game gives up.

### Nothing was withheld during this hold

TCP cannot acknowledge past a gap. Had any inbound segment been dropped, the acknowledgement would
have frozen at the hole until it arrived, and every later segment would have sat unacknowledged
behind it. It never froze - not at the toggle, not anywhere, not by a single beat.

**With autoresync on, the reference is not withholding inbound 30k at all.** Whatever lets it hold
for three minutes, it is not a cleverer way of blocking.

Only one connection is involved - a single local port for the whole capture - so this is not a
second connection carrying the traffic.

### And with autoresync off, the same capture answers it

| | Acknowledgement | Outcome |
|---|---|---|
| **Autoresync on** | +73 every 3.02 s, eighteen times, never stalls | holds past three minutes |
| **Autoresync off** | **frozen at 74** for **20.50 s** | game sends FIN, closes the connection itself, reconnects 0.28 s later |

`Ack=74` is exactly one heartbeat taken - relative sequence starts at 1 - and nothing after it. So
without autoresync the block is total, and what it is blocking is the heartbeat.

**Autoresync passes the heartbeat and withholds everything else.** That is the 3074 resync
philosophy applied to a stream: let through exactly what the session needs to stay alive, and
withhold the rest. Nothing is being periodically released and no acknowledgements are being
injected; the keepalive simply never gets stopped.

Note also how it fails. On 7500 the *peer* resets. Here the **game** gives up and sends FIN - it is
the client's own patience that runs out at twenty seconds, not the server's.

### Implemented as a pass list

`ApiBlockOptions.PassLengths`, defaulting to `{73}` on 30k and empty on 7500, which survives about
87 s unaided with no keepalive identified. `--no-resync` clears it; `--pass` overrides the size for
a machine whose heartbeat differs.

One caveat on the evidence: every inbound segment in the autoresync capture was a heartbeat, because
the game was idle. So that capture shows the heartbeat passing but cannot show non-heartbeat traffic
being withheld - the block half is inferred from the module having an effect at all, not observed.

**That caveat turned out to matter - see finding 51.**

---

## Finding 51 - a keepalive pass cannot save an ordered stream

Running `run 30k` with the 73-byte pass on: **71 seconds held, 17 keepalives passed, 2 segments
withheld**, ended by the peer resetting.

Reported alongside it: the hold runs indefinitely while nothing is being blocked, and codes about
**18 seconds after it starts actually withholding**.

### Why the pass does not help

TCP delivers in order. Withhold one segment and everything after it - **including every keepalive
that follows** - still reaches this machine's stack but cannot be handed to the application, because
there is a hole in front of it. The stack acknowledges them selectively; the game never sees them.

So passing the heartbeat only keeps a hold alive while nothing else is withheld. From the first
segment actually blocked, the game is starved regardless, and the same ~18 s applies.

Which is exactly what the run shows: two segments withheld across 71 seconds, the rest of the time
nothing to block and the keepalives genuinely arriving.

### This corrects finding 50

Finding 50 concluded that autoresync "passes the heartbeat and withholds everything else", and built
that as `PassLengths`. The first half is observed and correct. The second half was inferred, and it
cannot work as described on an ordered stream.

The likeliest reading now is that **the reference's autoresync has the same limitation** and its
three-minute hold was taken while 30k was idle - nothing to block, so nothing starved. That is
testable: hold it with autoresync on while something is actually happening on 30k, and see whether
it too codes at ~18 s.

The keepalive pass is kept because it does do something real - it stops an idle hold from dying at
twenty seconds - but it is not what makes a *blocking* hold survive, and nothing found so far is.

### What could survive an active block

Not a pass list, because the problem is the hole rather than the traffic around it. The hole has to
be filled before the game gives up, which means releasing what was withheld rather than passing
something else.

That is `--slow`: hold each segment a fixed time and let it go. At a delay below the ~18 s tolerance
the stream never starves, the game runs that far behind, and the connection survives indefinitely.
Untested on 30k.

---

## Finding 52 - 30k slow desyncs longer on the reference, and the reason is not yet known

Both tools slowing inbound 30k, time before the game resynchronises:

| | Time to resync |
|---|---|
| d2vpn, `--slow` at 6 s | **~33 s** |
| Reference limiter, its 30k slow | **~50 s** |

So the mechanism works on both - a delay line does survive an active 30k hold where a block cannot
(finding 51) - but the reference keeps the desync going about 17 s longer.

### With the reference's autoresync off, the two match

**~33 s either way.** So the delay mechanism is the same on both tools, and the whole 17 s
difference comes from autoresync.

That rules out the two explanations first recorded here - that the delay is not really 6 seconds, or
that it is a rate cap like 3074's. Either would have shown up with autoresync off. Both were
proposed before this comparison existed and neither survives it.

### And it exposes a problem with this project's own keepalive pass

`Instance30kSlow` passes the 73-byte heartbeat, inherited from the block preset. Under a delay line
that cannot help, and makes things worse:

```
WITH the pass                        WITHOUT it
 t=0 heartbeat passed  -> delivered   t=0 heartbeat held -> delivered at 6
 t=1 content   held    -> due at 7    t=1 content   held -> delivered at 7
 t=3 heartbeat passed  -> stuck       t=3 heartbeat held -> delivered at 9
 t=6 heartbeat passed  -> stuck
 t=7 content released  -> all deliver
 nothing between 0 and 7: a 7s gap    a smooth stream, uniformly 6s late
```

Passing a packet cannot move it ahead of a hole already in front of it. So under `--slow` the pass
buys nothing and converts a smooth delay into a sawtooth of gaps up to `ReleaseAfter + 1`.

**Tested, and the test was invalid.** With `--no-keepalive` two holds ran 55 s and 61.6 s - but
**both were released by hand**, not ended by anything. Neither was a ceiling. Comparing them against
the ~33 s, which was an in-game resync observation, compared a length the operator chose against a
length the game imposed. Those are not the same measurement and the comparison establishes nothing.

Finding 54 settles it the other way: with the keepalive **on**, a hold ran **244.3 s** and was also
released by hand. Four times the number that was supposed to show the keepalive hurting.

So there is no evidence the pass costs anything under a delay line. The ordering argument still says
it cannot *help* - a passed packet does not move ahead of a hole already in front of it - which
makes it a no-op rather than a mistake, and it stays.

### An open problem: duplicate acknowledgement floods

The same run shows this machine emitting **dozens of duplicate acknowledgements inside 30 ms**,
repeatedly - `Dup ACK #30` through `#74` on one packet, all carrying the same `Seq` and `Ack`.

A 6 s delay means the sender goes six seconds unacknowledged, so its retransmission timer fires
repeatedly: 293 suppressed across 134 withheld segments, about 2.2 each. That much is expected.

Dozens of duplicate acknowledgements are not. They mean the receiver is being handed data it cannot
use - either beyond a hole, or data it already has.

One candidate, unconfirmed. `ApiBlockModule` forgets a withheld segment once it is well past release
- `2 x ReleaseAfter`, so 12 s at a 6 s delay - to stop the set growing for as long as a delay line
runs. A sender retransmission arriving after that window is no longer recognised, so it is withheld
again and delivered a second time. The receiver already has that data, and answers with duplicate
acknowledgements.

If that is the cause, the fix is to keep an entry until its delivery is acknowledged rather than for
a fixed multiple of the delay. Not yet investigated.

### If that is not it, the capture that would settle it

Inbound 30k is 73 bytes every 3.02 s (finding 50), so the outbound acknowledgement cadence under the
reference's slow reads directly: steady +73 every ~3 s is a smooth delay, bursts with gaps between
are a sawtooth like the one above. Filter `tcp.dstport >= 30000 && tcp.dstport <= 30009`, taken with
autoresync **on**, since that is the configuration that differs.

---

## Finding 53 - the 30k block is identical on both tools, and autoresync does nothing for it

Three captures of the same filter, one session, the connections chaining one to the next.

| | Port | Acknowledgement frozen at | Frozen for | Ends |
|---|---|---|---|---|
| Reference, autoresync **off** | 51997 | 147 | **20.55 s** | FIN, reconnect |
| **d2vpn** | 52056 | 99 | **20.53 s** | FIN, reconnect |
| Reference, autoresync **on** | 52122 | 147 | **20.53 s** | FIN, reconnect |

**A spread of 0.02 seconds across all three.** In each the acknowledgement stops dead while the
outbound sequence keeps climbing - this machine keeps sending, and receives nothing it can use -
until the game gives up and sends FIN.

The order is checkable from the captures rather than from their labels: the d2vpn run ends by
opening port 52122, which is the connection the third capture is of. So it genuinely sits between
the two reference runs.

### This project's 30k block matches the reference exactly

No difference in behaviour, timing or failure mode. Nothing is missing.

### And autoresync does nothing for a block once content is flowing

The reference freezes for 20.53 s with autoresync **on** and 20.55 s with it **off**. Identical.

That confirms the ordering argument in finding 51 by measurement rather than reasoning: a keepalive
pass cannot help once a segment has been withheld, because everything behind the hole is
undeliverable whatever is done with it.

It also puts finding 50 in its place. That capture showed the acknowledgement climbing steadily with
autoresync on, and it was taken while 30k was idle - only heartbeats, nothing to withhold. The
caveat recorded there turns out to have been the whole story: with content actually flowing,
autoresync-on and autoresync-off are the same thing.

**~20.5 s is simply what a 30k block is.** Neither tool does better, and nothing found suggests
better is available.

## Finding 54 - d2vpn's 30k slow outlasts the reference by 2x

Three runs of the same thing, one session:

| | Outcome |
|---|---|
| Reference, slow + autoresync | **error code at ~125 s** |
| Reference, slow, no autoresync | **error code at ~70 s** |
| **d2vpn, slow** | **244.3 s, no error code** - released by hand |

d2vpn did not hit a ceiling at all. The run ended because the operator unticked, with `peer resets
0` and nothing expired or evicted.

```
withheld 58   resent 259   replayed 58   ack brought forward 58/58
keepalives passed 4        capture limit reached 1
```

### The keepalive barely fired, and it did not matter

**Four** keepalives in 244 seconds, where a 73-byte heartbeat every 3 s would give around eighty. So
on this connection - a different server from the one findings 50 and 53 measured - the heartbeat is
not 73 bytes, and the pass was very nearly inert.

The hold survived four minutes anyway. Under a delay line the connection stays fed because the queue
keeps delivering; the keepalive has nothing to add. That is consistent with the ordering argument
and inconsistent with the keepalive being load-bearing either way.

**The 73-byte figure holds - see finding 57.** It looked wrong because the pass matched 0-4 packets
across busy runs, but a heartbeat is not sent while there is real content to send. Measured idle on
a third server, inbound is 73 bytes twenty times a minute and nothing else.

### What is not known

Whether the effect was working for those 244 seconds. Surviving and desynchronising are different
things, and this run only establishes the first - the same gap that made finding 52's comparison
invalid. The reference's runs ended in error codes, which is at least evidence they were doing
something; d2vpn's ended because the key was pressed.

### What might explain outlasting it

Untested, in rough order of plausibility:

- **The reference's delay grows and this one does not.** Its 3074 slow is a rate cap rather than an
  offset (finding 41), and a growing backlog would eventually exceed what the game tolerates -
  giving exactly the pattern of coding at 70 s or 125 s where a fixed 6 s offset never does.
- **The acknowledgement rewriting.** All 58 replayed segments here needed it. A tool replaying them
  unrewritten would deliver stale acknowledgements, and three duplicates trigger fast retransmit.
- **Suppression is more complete here**, so the sender retransmits into a queue rather than around
  it - 259 suppressed across 58 segments, about 4.5 each.

## Finding 55 - 30k slow survives in proportion to how little it is withholding

Three slow runs, and the variable that predicts the outcome is not a setting.

| Keepalive | Held | Withheld | Per second | Ended |
|---|---|---|---|---|
| on | 244.3 s | 58 | **0.24/s** | released by hand |
| off | 69.7 s | 30 | **0.43/s** | released by hand |
| on | 54.1 s | 58 | **1.07/s** | **peer reset** |

The only run that died was the one withholding four times as fast as the one that lasted four
minutes. Survival tracks the withholding rate, not the configuration.

**Withdrawn - see finding 56.** Four more holds put the two resets at opposite ends of the range,
so the rate does not predict the outcome.

That makes sense from finding 51: the pressure comes from unacknowledged data, so a port carrying
more content produces more of it per second and exhausts the sender sooner. A hold on an idle port
withholds nothing and survives indefinitely; a hold during heavy traffic is withholding constantly.

### The keepalive was inert in all three

**Zero passes** in the two runs directly compared, four in the third. This server's heartbeat is not
73 bytes, so `--no-keepalive` changed nothing about what was happening - the two runs differed only
in what the game happened to be sending.

Which means the pair establishes nothing about the keepalive, in either direction. It has now been
inert in every slow run measured.

### This weakens finding 54

That finding reported d2vpn's 244.3 s against the reference's ~125 s and ~70 s, and read it as
outlasting the reference by 2x. The 244.3 s run was withholding at 0.24/s. What the reference's runs
were withholding at is unknown, and at 1.07/s this project's own hold died at 54 s - inside the
range the reference was coding in.

So the comparison may be measuring how busy the port was rather than which tool was running. It is
not withdrawn, but it cannot stand on one run from each side with the traffic rate uncontrolled.

### Consequence for how these are compared

Any A/B on 30k has to report the withholding rate, and ideally be taken back to back in the same
game state. Several comparisons in this document do neither - findings 49 and 52 among them - and
the variance they were attributing to settings is at least partly this.

## Finding 56 - the withholding rate does not predict survival, and holding through a reconnect can

Seven slow holds now, across two sessions:

| Delay | Held | Withheld | Per second | Outcome |
|---|---|---|---|---|
| 8 s | 32.2 s | 1 | **0.03/s** | **peer reset** |
| 6 s | 244.3 s | 58 | 0.24/s | survived |
| 8 s | 45.0 s | 13 | 0.29/s | survived |
| 8 s | 95.0 s | 28 | 0.29/s | survived |
| 8 s | 45.0 s | 17 | 0.38/s | survived |
| 6 s | 69.7 s | 30 | 0.43/s | survived |
| 6 s | 54.1 s | 58 | **1.07/s** | **peer reset** |

**The two resets are at the lowest rate and the highest.** Everything between survived. Finding 55
read a correlation off three points and it does not survive four more - withdrawn.

### The low-rate reset is a different failure

That hold was started in the same second as a reconnect, and ran with `live 0` and one segment
withheld for its whole 32 seconds. The connection it was holding had just been killed and was trying
to re-establish; almost nothing flowed because there was almost nothing there.

So it is not a hold that died under load, it is **a new connection that was prevented from
establishing**. Withholding during setup leaves the exchange incomplete, and the peer gives up.

Two later holds in the same session also began within a second of a reconnect and survived 45 s
each, so it is not deterministic - presumably those connections got far enough along before anything
was withheld. But it is a real hazard of using the two together, which is the combination this
command exists for.

### 8 seconds, reported as better

The operator's reading is that slow works better at `--slow-s 8` than at 6. That is a judgement
about the in-game effect, and nothing measured here speaks to it either way: the longest hold at 8 s
was 95.6 s against 244.3 s at 6 s, but those were both ended by hand at different traffic levels, so
the durations are not a comparison.

Recorded as the operator's preference rather than as a result. The default stays 6 s, which is the
reference limiter's stated figure.

## Finding 57 - an idle 30k connection is heartbeats and acknowledgements, nothing else

`watch 30k --seconds 60`, standing still, sniff mode. A third server, different from the two the
heartbeat was measured and then doubted on.

```
inbound (DL): 68 packets, 2 distinct sizes
      0 bytes  x48        bare acknowledgements
     73 bytes  x20        one every 3.0 s
```

**Twenty 73-byte segments in sixty seconds.** That is 3.0 s apart, against the 3.02 s measured in
finding 50 on a different server. The figure holds, and inbound carries *nothing else* while idle -
two distinct sizes, one of which is empty.

### Which explains the runs where the pass never fired

Those reported `alive 0` with 30 to 58 segments withheld, and it looked like 73 was wrong for those
servers. It was the other reading: **a heartbeat is not sent while there is real content to send.**
The server says "nothing to report" only when it has nothing to report.

So zero keepalives during a busy hold is correct behaviour rather than a mismatched pass list.

And it means the keepalive fires exactly when it is useful and never otherwise: its whole job is to
stop an idle hold dying, and the heartbeat exists precisely when the hold is idle. The two
conditions are the same condition.

### Outbound is busier than inbound when idle

67 outbound against 68 inbound, but eight distinct sizes against two - 52 bytes twenty-seven times,
plus 90, 1360, 1027, 84, 123, 140. This machine keeps talking on 30k while the server only ticks.
Not pursued, but worth knowing that "idle" is one-sided.

## 30k - where it was left

Working and closed for now. The block matches the reference exactly; slow works and outlasts it in
the runs taken, though the comparison is not controlled well enough to claim that.

### Against the reference

| | d2vpn | Reference |
|---|---|---|
| Block, active | 20.53 s | 20.53 s with autoresync on or off - **matches exactly** (finding 53) |
| Block, idle | indefinite with the keepalive | same mechanism (findings 50, 57) |
| Slow | 45-244 s, reset in 2 of 7 holds | codes at ~125 s / ~70 s (findings 54-56) |
| Reconnect | identical on the wire (findings 46, 47) | |

### Settled

- The block is a total hold, ~20.5 s once anything is being withheld, and **neither tool does
  better**. That is the game's tolerance, not a limitation of either.
- The keepalive is the reference's autoresync: a 73-byte heartbeat every 3 s, sent **only while the
  connection is idle**, which is exactly when a hold would otherwise die for nothing. It fires when
  useful and never otherwise, and needs no per-server tuning (finding 57).
- Retransmission suppression, buffering with acknowledgement rewriting, the delay line, and the
  reconnect sharing one handle.

### Left open

1. **Duplicate acknowledgement floods under `--slow`** - dozens inside 30 ms, repeatedly. Candidate:
   the module forgets a withheld segment at `2 x ReleaseAfter`, so a retransmission arriving after
   that is treated as new, withheld again and delivered twice. The fix would be keeping an entry
   until its delivery is acknowledged rather than for a fixed multiple of the delay.

2. **Whether the effect is working during a long slow hold.** Every slow measurement here is
   survival, not effect. A four-minute hold that desynchronises nothing would look identical in the
   counters. Only in-game observation can tell them apart, and this is the gap that made finding 52
   wrong.

3. **What the reference's autoresync does under slow.** Findings 50 and 57 explain it under a block.
   Under a delay line a keepalive has nothing to rescue, so either it does something else there or
   the extra time seen was traffic rather than mechanism.

4. **The right delay.** 6 s is the reference's stated figure; 8 s is preferred in play but on
   judgement rather than measurement. Any sweep needs the runs back to back in the same game state -
   the traffic varies more than the setting does (finding 56).

### How to compare anything here

Back to back, same game state, and report the withholding rate. Several comparisons in this document
do neither and had to be withdrawn for it.

---

## Finding 58 - 27k calibrated, and it confirms the prior art's constants independently

`calibrate 27k` and `calibrate 27k --downlink`, sniffed while a working limiter held its 27k module.

### Uplink

| Size | In hold | Normally | Concentration | |
|---|---|---|---|---|
| 373 | 2 | **never** | only in hold | |
| 306 | 15 | 0.1% | **112x** | |
| 303 | 13 | 0.1% | **97x** | |
| 305 | 22 | 0.2% | **82x** | |
| **1300** | 23 | 0.5% | **34x** | reference's resync trigger |
| **1185** | 2 | 0.1% | **15x** | reference's window-closer (1176-1194) |
| 100 | 9 | 5.0% | 1.4x | ordinary - slipped through |

**1300 and 1185 are independent confirmation.** Both were read out of the reference's source hours
before this was measured, as the size that opens its resync window and the size that closes it. They
fall straight out of the calibration at 34x and 15x concentration. Nothing about the measurement
knew to look for them.

That is the strongest prior-art corroboration in this project: every other constant taken from a
reference has needed correcting, and these two came back exactly.

### Downlink

One size crossed, **9 bytes, 52 of them**, and the tool called it **ordinary** at 2.8x - 9-byte
packets are 36% of normal inbound traffic. A size that common is game state as much as handshake,
so nothing inbound is treated as load-bearing.

The same answer 3074 downlink gave, and for the same reason.

### Also measured

- **41 peers**, of which **3 were held.** The reference holds connections individually rather than
  blocking the port, which per-peer analysis surfaces and aggregate analysis would have hidden.
- A held peer drops to **0/s** and returns to its normal rate within a second of release.
- At release, one peer showed a **68-packet spike** in a single second against a 20/s baseline -
  the reference's own buffer draining.

### Built

`Players27k`, `Players27kDownlink` and `Players27kFull`, wired into `MeasuredDefaults.For`. Uplink
passes the six measured sizes during a resync window and nothing outside one; downlink passes
everything during a window, there being no list to narrow to. The trigger is an outbound 1300 either
way, since it is this machine that notices a peer has gone quiet whichever direction is held.

Only the two-direction hold carries a safety timeout: with nothing flowing either way there is no
traffic left to notice a problem in.

### Where this differs from the reference deliberately

It passes **everything** during a resync window; this passes only the six measured sizes on uplink.
That is the 3074 approach and leaks less. If a session fails to survive a long hold, widening that
is the first thing to try.

> **Superseded by finding 59.** A second calibration shares only one size with this one, and the
> six-size list was replaced by a threshold. The 1185 and 1300 result below stands; the four
> mid-sized values do not.

It also closes its window early, on an outbound 1176-1194 or an inbound 1101-1299. `HoldModule` has
no equivalent, so a window here always runs its full five seconds. Worth adding if the leak matters.

---

## Finding 59 - the 27k signature does not replicate, and the real rule is a threshold

A second `calibrate 27k` uplink run against the same reference limiter, autoresync on. 25 peers,
3 flagged held.

### The two runs barely agree

| | Uplink signature |
|---|---|
| Run 1 (finding 58) | 303, 305, 306, 373, **1185**, **1300** |
| Run 2 | 6, 100, 237, 266, 337, 339, 349, 350, 396, **1300** |

**One size in common out of fifteen.** The five others shipped in `Players27k` would have passed
nothing at all in run 2. That list was fitted to a single 45-second sample.

100 makes the point twice over: run 1 called it **ordinary** at 1.4x because it was 5.0% of normal
traffic, and run 2 called it **only in hold** because it never appeared in normal traffic at all.
The same size, opposite verdicts, two runs apart.

### What does replicate is the shape

The union, sorted: 6, 100, 237, 266, 303, 305, 306, 337, 339, 349, 350, 373, 396 — then a gap —
1185, 1300. Everything is **under 400 or is one of two large sizes**, with nothing between 396
and 1185.

The prior art has the same shape. The stopwatch `KickModule` allowlists 9, 11 and 25, notes 100,
284 and 315 in a comment, and passes 1300 in both directions. Every one of those is under 400 or
is 1300.

So the rule is a threshold plus two named sizes, not an allowlist. Small control packets carry
variable-length fields, so their exact sizes shuffle between runs while the class stays put — which
is precisely what two runs sharing only 1300 looks like.

### A tiny keepalive, only during a hold

**6 bytes, 28 packets, 58% of everything that crossed, and never seen in normal traffic.** Paired
with a **10-byte downlink at 68%**.

One peer ran at 76/s normally and then trickled at a steady ~2/s for the twenty seconds it
was held. A 6-byte payload is far too small to be game state, and a steady trickle at 3% of normal
rate is what a keepalive looks like — the same role 30k's 73-byte heartbeat plays.

It falls under the 400-byte threshold, so it passes without being named.

### One of the three "held" peers was not held

The third flagged peer sat at 0-1/s before its window and ran at 14-38/s after it. That is a peer
**joining**, not one being held.

The tool already handles this: `HasUsableBaseline` requires 20 baseline packets and that peer had
2, so it was dropped from `ReliablePeers` and never reached the signature. The printed numbers
confirm it — the signature table totals 48 packets, the header says 90, and the peer's own timeline
accounts for the 42-packet difference.

Worth recording because the peer list still prints it as `HELD 12-21s (18% of normal)`, which reads
as a weak hold rather than as noise. The per-peer timeline is what distinguishes them.

The cleanest hold: a flat 99-106/s for seven seconds, then 17, 1, 0, 0, 0, 0, 0, 1,
4, 0, 2. **1% of normal.**

### Built

`HoldModuleOptions.ResyncPassMaxLength`, an optional payload-size bound applied during a resync
window alongside `ResyncPassLengths`. Set to 400 on `Players27k` and `Players27kFull` — just above
the largest small size observed and far below 1185 — with the explicit list narrowed to the two
sizes that survived both runs. Null everywhere else, asserted by test.

### What this costs

A threshold leaks more than a list: run 1 measured 100-byte packets at 5% of normal traffic, so
ordinary game data crosses along with the handshake. What bounds it is that this applies **only
while a resync window is open**, never during the block itself. If a hold turns out to block too
little, the window duty cycle is the thing to tighten, not the threshold — narrowing it back to an
exact list is what just failed.

### Still unknown

Whether 400 is anywhere near the real boundary. Nothing has been observed between 396 and 1185, so
the cut could sit anywhere in that range; 400 is the conservative end. A third run would help, and
a size landing between 400 and 1185 would settle it.

---

## Signatures claimed by prior art — NOT verified here

Everything above this line was measured on this machine. Everything in this section was read out
of other people's code and must be treated as a hypothesis until we confirm it ourselves. Packet
lengths shift with game patches and with the user's own loadout, so none of these are safe to
hardcode.

### The core technique: selective pass-through

`stopwatch`'s `KickModule` (source at `..\d2\stopwatchsteamversion-main`) does not block a port.
It blocks everything *except* an allowlist of packet lengths:

```csharp
int[] c = new int[] { 9, 1300, 11, 25 };   // out 100 315 284 // in <20 looks like coords
return p.Outbound && c.Contains(p.Length) || p.Length == 1300;
```

This is the difference between a naive limiter and a good one. Drop everything and the peer sees
a dead connection, so the game throws an error code and kicks you. Keep the small control packets
flowing while dropping the payload, and the connection survives while game state stalls.

### Length signatures seen in prior art

| Port group | Lengths | Claimed meaning | Source |
|---|---|---|---|
| UDP 3074 | 42, 128, 136, 233 | Reconnect-handshake packets, allowed through during resync | HarryWatch `XboxProvider` |
| UDP 3074 | 1258 | Data packet; exactly one must be accepted for resync to complete | HarryWatch `XboxProvider` |
| UDP 27k | 9, 11, 25 | Small control packets, outbound allowlist | stopwatch `KickModule` |
| UDP 27k | 1300 | Passed in both directions unconditionally | stopwatch `KickModule` |
| UDP 27k | 100, 315, 284 | Outbound, noted in a comment, not in the allowlist | stopwatch `KickModule` |
| UDP 27k | < 20 inbound | "looks like coords" | stopwatch `KickModule` comment |
| TCP 7500 | ~170-220, typically 184 | Weapon-swap packet. **Explicitly per-user** - varies with weapon, ornament, catalyst | BitcoinMiner README + `TriggerLength` default |
| TCP 30k | 1300, 100, 315 | Reconnect-related | stopwatch `KickModule.Reconnect` field |

### Timing thresholds

Tuned against the game's own timeouts, which is why they are oddly specific:

| Value | Meaning | Source |
|---|---|---|
| ~25 s | **27k kick only.** The *targeted player* drops; holding past this risks a BEAVER error for the operator. Does **not** apply to a 3074 hold | stopwatch `KickModule` comment |
| 26-27 s | **27k kick only.** Module force-disables to stay inside that window | stopwatch `KickModule` |
| 35 s | Fixed buffer hold for a blocked 3074 packet | HarryWatch `PveModule` |
| 6 s solo / 40 s fireteam | Effect duration of an injected RST on 30k | HarryWatch `ReconnectModule` comment |
| 2000 ms | Default untick delay after a damage trigger fires | BitcoinMiner `Damage UL Slow` |

**Design consequence.** Both the allowlists and the thresholds belong in configuration with a
calibration path, never in a constant. This is the one point on which BitcoinMiner clearly beats
HarryWatch, and d2vpn should go further by calibrating in-app.

---

## Still unknown

Open items, in the order Phase 3 will need them:

1. ~~Packet-length fingerprints on UDP 3074.~~ **Answered** - see "what a 3074 uplink block
   actually does". Measured on this machine, as payload bytes: 51/128/136 control,
   190/198 handshake, 1166 data. HarryWatch's 128 and 136 match exactly; its 42/233/1258 do not.
2. **What a reconnect handshake looks like.** Partly answered - the escape sequence above is
   very likely it. Needs a capture spanning a *full* disconnect to confirm.
3. **Whether the 3074 peer is a dedicated server or another player.** Determines what blocking
   uplink actually does to game state.
4. **Whether WinDivert observes tunnelled packets once or twice** (inner + encapsulated outer).
   Current hypothesis: exactly once per filter, because the outer WireGuard packet is UDP to the
   VPN endpoint's own port and therefore does not match a filter keyed on 3074 / 27k / 7500 /
   30000. If that holds, VPN-agnostic behaviour falls out for free. Must be confirmed in the
   Phase 1 spike by running the same counting filter with the tunnel up and down.
5. **Per-scenario port behaviour** — orbit vs patrol vs fireteam vs strike. Only one steady-state
   sample exists so far.
