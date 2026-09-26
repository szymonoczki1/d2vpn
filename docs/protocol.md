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
<tunnel>:60059 -> 172.97.56.160:7500    ESTABLISHED
<tunnel>:64968 -> 205.209.21.144:30000  ESTABLISHED
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
| 11.219 | Two RSTs injected; streams to `205.209.22.52` and `205.209.19.191` both end |
| 11.548 | New SYN to `205.209.22.52` - **330 ms later**, same server |
| 53.540 | New SYN to `205.209.22.100` - a *different* server. The actual instance change |

**Open question.** HarryWatch injects *two* RSTs per peer - one built from an outbound sample and
one from an inbound sample. Only outbound-looking RSTs appear in this capture. The likely
explanation is that a reinjected inbound packet is delivered up the local network stack and never
reaches the adapter, so npcap cannot see it. If so, **adapter-level capture cannot verify inbound
injection**, and d2vpn's own tests must confirm it another way. Worth settling in Phase 1.

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
| 3074 UL (`PVE` outbound) | **None known** | Held over 20 minutes with no disconnect |
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
10.2.0.2 -> 205.209.22.74   12118 -> 3074  Len=128
10.2.0.2 -> 205.209.19.172  12118 -> 3074  Len=128
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

**Buffering is therefore not a prerequisite**, and the work queued behind that assumption is not
needed. It may still be worth having for other modules, but nothing measured so far requires it.

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
