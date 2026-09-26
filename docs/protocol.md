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

**Confidence: good for the pass list, none for the trigger.** `51, 233` is reproduced and shipped
as the downlink default. What is *not* known is which of them - if either - is the reconnect
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
