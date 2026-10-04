# Prior art

Two existing limiters inform d2vpn. Understanding how they relate to each other changes how we
use them.

## The family tree

Nearly every Destiny 2 limiter descends from a common ancestor: a **stopwatch app**. That is why
they all look alike. The shared config schema, module names, and even the Cyrillic `З` in "З0k"
are inherited ecosystem convention, not evidence of shared authorship.

The ancestor is on this machine: `..\d2\stopwatchsteamversion-main`, namespace `steam`,
README in full: *"use for whatever. Visual studio 2022"*.

```
stopwatch  (namespace `steam`)
   |
   +-- HarryWatch 1.2      source available, net6.0
   +-- BitcoinMiner v2.1   binary only, net10.0
```

**HarryWatch is not BitcoinMiner's parent** - they are siblings. Proof is in the dependencies
stopwatch shipped that HarryWatch dropped but BitcoinMiner kept:

| Dependency | stopwatch | HarryWatch 1.2 | BitcoinMiner v2.1 |
|---|---|---|---|
| `WindivertDotnet` | 1.1.2 | 1.1.2 | 1.1.2 |
| `DotNetBungieAPI` | 2.12.1 | 2.12.1 | 2.15.0 |
| `Hardcodet.NotifyIcon.Wpf`, `MathConverter`, `OxyPlot.Wpf`, EF Core Sqlite | yes | yes | yes |
| **`SharpCompress`** | 0.36.0 | **dropped** | **present** |
| **`System.Management`** | 7.0.0 | **dropped** | **present** |
| **`System.ServiceProcess.ServiceController`** | 8.0.0 | **dropped** | **present** |

Same story in the config: BitcoinMiner's `Settings.Window_Snow` traces to stopwatch's
`WpfSnowfall` namespace, which HarryWatch does not contain. And stopwatch carries `KickModule`
and `TestModule`, which HarryWatch also dropped.

### What this changes

- **HarryWatch stays a valid architectural reference.** Same ancestor means the same design, and
  it is the only one of the three whose source we have.
- **BitcoinMiner's extras are not "HarryWatch plus two years".** They may be a different author's
  independent work, or inherited from a newer stopwatch base we have not seen. Treat them as a
  specification of good behaviour, not as a known evolution path.
- **Originality matters more, not less.** This architecture is widely copied across the
  ecosystem, so a clean, independently written, properly documented implementation is exactly
  what distinguishes d2vpn as portfolio work. Sharing a code lineage with a dozen forks would not.
- stopwatch's *"use for whatever"* is an informal grant, not a license. It does not make derived
  code safe to publish, and HarryWatch carries no license file at all.

## What BitcoinMiner added — the actual "why it's better"

Read from its README and `VermillionOsteichthyes.cfg`, both plain user-facing files. Fourteen
modules: `PVE`, `PVP`, `API Block`, `З0k`, `З0k Slow`, `Load Manip`, `Payload Block`,
`Damage UL Slow`, `Dialogue Skip`, `Swap`, `Super З0k`, `Reconnect`, `GamePauser`, `Weasel`.

### 1. User-calibrated packet signatures — the big one

`Payload Block` and `Damage UL Slow` expose **`TriggerLength`** (default 184) as a setting. The
README tells the user to find their own value with Wireshark:

> open wireshark and use the filter `tcp.srcport==7500` and select the correct network interface
> switch between the two weapons and look at wireshark
> numbers seem to be between 170 and 220
> typically you will see a burst of 3 to 4 packets when switching your gun

This is the single most important architectural idea to copy. HarryWatch hardcodes signatures
(42 / 128 / 136 / 233 / 1258 in `XboxProvider`). BitcoinMiner recognises that the right value
varies per user - weapon, ornament, catalyst all shift packet size - and makes it calibratable.
**d2vpn should treat every length signature as configuration, never as a constant**, and should
ship a built-in calibration view so the user never needs Wireshark for it.

### 2. Self-disabling modules

`SelfDisable` on `Payload Block` and `API Block`. Per README: "you do not need to untick between
shots, the module does that." A module fires, does its job, and unticks itself. Removes a whole
class of human timing error.

### 3. Hotkeys on sub-toggles, not just the module

HarryWatch binds the module and a couple of directions. BitcoinMiner adds `BufferKeybind`,
`AutoResyncKeybind`, and `FlushBufferKeybind`, so buffering and resync are switchable mid-fight.

### 4. Manual buffer flush as its own action

"3074 Flush Buffer - when triggered any 3074 packets currently in the buffer are injected."
Decoupled from module release. HarryWatch can only drain on toggle-off.

### 5. `З0k Slow` split from `З0k`

> delays all inbound packets from 30k by a fixed number of seconds
> good for holding reconnects / bad for blocking joining and objectives

Matches the requirement for 30k-slow as a separate module, and confirms why: the two have
opposite use cases and should not share a toggle.

### 6. Macros coordinate with the limiter

`Swap` carries `Port3074`, `Packet3074DL`, and `AutoDisableBuffering` alongside `SwapDuration` /
`SwapDelay` / `EndingLoadout` / `LoadoutEnabled[20]`. The loadout macro drives limiter state
directly rather than sending keystrokes for a global hook to catch - exactly the control-API
design chosen for d2vpn. It also makes `dual.ahk` / `izivigi2700_20.ahk` redundant in that tool.

### 7. Library choices worth adopting

| Library | Version | Why it matters |
|---|---|---|
| `RateLimiter` + `ComposableAsync.Core` | 2.2.0 | "Slow" is a real rate limiter. So is HarryWatch's - see below; the "fixed `DelayPacket(1s)`" description here was wrong. Likely also paces buffer drain, the thing HarryWatch never settled (`// 1000 / 20 / 3`). |
| `PacketDotNet` | 1.4.7 | Proper packet dissection instead of hand-walking headers with raw pointers. |
| `RawInput.Sharp` | 0.1.3 | Raw Input API for hotkeys rather than a low-level keyboard hook. More reliable alongside a fullscreen game. |
| `SharpPcap` | 6.3.0 | Powers the built-in capture recording. |

## HarryWatch's PVP - the 27k module, read from source

`HarryWatch-1.2/Interception/Modules/PvpModule.cs`. **Read from its code, not measured here.**

Provider map, which settles which module owns which port:

| Module | Provider |
|---|---|
| PVE | Xbox (3074) |
| **PVP** | **Players (27k)** |
| Solo, Revive | Players (27k) |
| Multishot | Players **and** Xbox |
| API Block | 7500 |
| З0K, Reconnect | 30000 |

So PVP is the 27k counterpart of PVE, and its description is *"block players updates [27k] /
teamkill / prevent fireteam joining"*. It exposes the same three switches wanted here: independent
inbound and outbound toggles, `AutoResync`, and `Buffer`.

### Its resync is a window, not a pass list

3074's resync passes a list of handshake sizes and blocks everything else. PVP's does the opposite:
it opens a short window in which **everything** passes, and closes it as soon as the exchange it was
waiting for goes by.

| | |
|---|---|
| Opens on | **outbound payload exactly 1300** |
| Window | **5 seconds** maximum |
| Closes early on | outbound **1176-1194**, or inbound **1101-1299** |
| During the window | everything passes |
| Buffer hold | **30 minutes** - effectively indefinite, against PVE's 35 s |

Opening also clears that peer's delay queue, so a resync discards what was being held rather than
delivering it late.

### What that means for this project

`HoldModule` can express most of it already: trigger on outbound 1300, a 5 s duration, and a null
resync pass list, which it already treats as "everything passes".

It cannot express **closing the window early**. Without that the window always runs its full five
seconds, and since everything passes during one, that is five seconds of leak per resync rather than
however long the exchange actually takes.

### These numbers are not to be transcribed

1300, 1185, 1100-1300 and 5 s are HarryWatch's constants. Every equivalent taken from prior art in
this project has needed measuring: 3074's hardcoded signature was wrong twice over, 7500's capture
window and 1460-byte limit were unreachable on this channel, and its 54 s ceiling was thirty seconds
short. They are a prior for what to look for, not values to ship.

## HarryWatch's Reconnect, read from source

`HarryWatch-1.2/Interception/Modules/ReconnectModule.cs`. **Read from its code, not measured here**,
though finding 6 measured its traffic independently.

Its own description: *"instant disconnect/reconnect [30k] / change public instances / reload world
state / pull yourself to ft leader"*.

| Setting | Value |
|---|---|
| Group | **30k** (TCP 30000-30009) |
| Connection counts as live within | **10 s** of its last packet |
| Resets per connection | **2** - one built from an outbound sample, one from an inbound |
| Sample requirement | last packet **carrying payload** in that direction |
| Auto-deactivates after | **150 ms** - it is one-shot, not a hold |

```csharp
// inbound rst // solo 6 sec // fireteam 40 sec
```

That comment sits on the *inbound* reset, so the longer fireteam effect is attributed to the one
delivered up the local stack rather than the one sent to the peer.

### Both directions, and why

```csharp
var out_example = con.LastOrDefault(x => !x.Inbound && x.Length != 0);
var in_example  = con.LastOrDefault(x =>  x.Inbound && x.Length != 0);
if (out_example is null || in_example is null) { /* can't kill */ }
```

A reset has to look like it came from the side it claims, so each is built from a sample of that
side. If either sample is missing the connection is skipped entirely - half a kill leaves the other
end believing the connection is alive.

Payload-only samples: a bare acknowledgement parses fine but its sequence number sits where the
stream already is rather than where it is going.

### It mutates a clone rather than constructing a packet

```csharp
var p1 = out_example.BuildSameDirection();
p1.ParseResult.TcpHeader->Rst = true;
p1.Recalc();
```

Set the flag on a copy of a real packet, recalculate, inject. Nothing is built from scratch, because
a reset carrying invented ports or sequence numbers belongs to no connection and is discarded. This
project does the same, in `ConnectionSampler`.

## HarryWatch's API Block, read from source

`HarryWatch-1.2/Interception/Modules/ApiModule.cs`. **Read from its code, not measured here.**

| Setting | Value |
|---|---|
| Capture window (quiet before the slice is held) | **100 ms** |
| Capture limit | **1460 bytes** - one maximum segment |
| Hold ceiling after capacity | **54 s** |
| Early release: held for | **19 s** |
| Early release: and silent for | **24 s** |
| Self-disable on inbound RST | yes, logged `Weasel :<` |

Its own description: *"blocks api updates [7.5k] / infinite rally / lobby crash (patched???) /
predamage"*.

### The mechanism is retransmission suppression, not blocking

This is the part that does not carry over from the UDP modules at all. Dropping a UDP packet
destroys it. Dropping a TCP segment only delays it - the sender gets no acknowledgement and re-sends
the same data under the same sequence number, so a module that merely drops has leaked by the first
retransmission.

So it runs in two phases:

1. **Capturing.** Every inbound `PSH` segment with payload is dropped and its sequence number kept.
   Bare acknowledgements are left alone.
2. **Holding.** Once nothing new has been dropped for 100 ms, the sender has run out of window and
   is only repeating itself. From then on it blocks *only* retransmissions of sequence numbers
   already held, and lets genuinely new data through.

That second phase is what makes it a hold on a fixed slice of the conversation rather than a block
on the whole connection.

### The ceiling is a reset, not a timeout

Unlike a 3074 uplink hold - no known ceiling, measured at 451 s across 35 resync cycles - nothing
renews a TCP connection while it is starved. The peer eventually resets, which the game shows as an
error code, and the module's own log line for that case names the Weasel error. Hence three
independent ways out: the 54 s maximum, the 19 s + 24 s quiet path, and an immediate exit on RST.

### On replay it rewrites the acknowledgement number

```csharp
p.AckNum = 0; // let storepacket assign highest
```

A TCP segment captured earlier carries an acknowledgement number that is stale by the time it is
replayed. This project's buffer replays packets verbatim, which is correct for UDP and wrong here -
so buffering on 7500 is exposed but off by default, and marked unproven until the same rewriting
exists.

## HarryWatch's delay values, read from source

`HarryWatch-1.2/Interception/Modules/PveModule.cs` and `PacketProviderBase.cs`. **Read from its
code, not measured here** - and HarryWatch carries no license, so these are facts to compare
against, never code to lift.

| Setting | Value |
|---|---|
| `SlowInbound` / `SlowOutbound` delay (3074) | **1 second** |
| `Buffer` max hold while blocking (3074) | **35 seconds** |
| Delay queue capacity | **2048**, evicting 16 at a time |
| Default delay when unspecified | 1 second |
| Flush pacing on untick | **0 ms** - unpaced |

### The architecture is the same as this project's

```csharp
if (Inbound)
{
    if (Buffer) xbox.DelayPacket(p, TimeSpan.FromSeconds(35));
    return false;
}
```

`return false` blocks the packet unconditionally. `Buffer` is a **separate opt-in toggle** that
additionally queues a clone for later delivery. So blocking drops, buffering is optional on top, and
35 s is the maximum a held packet waits - the direct equivalent of this project's `BufferMaxAge`,
independently set here to 30 s.

Queue capacity 2048 is also the value chosen independently here. Two of the three buffer parameters
match to within 17%, which is worth more as corroboration than any amount of reasoning about them.

**Correction.** An earlier version of this section claimed HarryWatch has no drop at all and that a
block *is* a 35 s delay, making block and slow one mechanism at two depths. That was wrong - read
from a grep of `DelayPacket` call sites without the surrounding `return false`. They are separate
toggles with different queue semantics, described below.

### Its "slow" is a throttle, not an offset

Slow passes `addFromLatest: true, sameDirection: true`, and that flag changes the meaning entirely:

```
clone.CreatedAt = (last ?? clone).CreatedAt.Add(delay);
```

The packet is scheduled one second after the **last already-delayed packet for that peer and
direction** - not one second after its own arrival. So the delays accumulate. At an inbound stream
of ~10 packets/s, one leaves per second and the queue grows by nine per second, with the effective
delay climbing without bound for as long as the toggle is held. There is a 10 ms floor for packets
arriving within 10 ms of the previous one, as an anti-burst guard.

Buffering passes `addFromLatest: false`, so a held packet is scheduled 35 s from *its own* arrival -
a ceiling on how long it may wait, not a pacing rule.

**So the two are different instruments**: buffering preserves the stream's shape and replays it,
while slow reshapes the stream into one packet per second per peer.

d2vpn's `--slow` is a fixed per-packet offset, so it preserves the stream's shape and shifts it.
HarryWatch's slow does not: it reshapes the stream and the backlog grows for as long as the toggle
is held. That is the one substantive difference between them, and it is worth knowing when a uniform
delay turns out to have little in-game effect (finding 40).

### The two uplink-slow designs side by side

| | HarryWatch `SlowOutbound` | d2vpn `--slow` |
|---|---|---|
| Delay value | 1 s | configurable, 2 s default |
| Scheduled relative to | the last already-queued packet for that peer | the packet's own arrival |
| Output rate | 1 packet/s per peer, fixed | equal to the input rate |
| Effective delay | climbs while held | constant |
| Stream shape | reshaped - 10/s becomes 1/s | preserved, shifted |
| Queue size | grows to the 2048 cap, then evicts 16 at a time | steady at rate x delay |
| On untick | sent only if `Buffer` is also on, else discarded | always dumped |
| Burst handling | sub-10 ms arrivals spaced 10 ms, not 1 s | untouched |

**One is a delay, the other a throttle.** d2vpn shifts uplink into the past and leaves it otherwise
intact, so the peer receives a complete, correctly shaped stream that is late - which is
indistinguishable from latency and is compensated for. HarryWatch caps uplink at one packet per
second per peer, so input exceeds output, the backlog grows by ~9 packets/s, and the effective delay
climbs linearly: held 30 s, the oldest queued packet is minutes behind. The peer is not receiving
late data, it is receiving a starved stream, and that compounds rather than being absorbed.

This is the likeliest explanation for finding 40's result - a uniform offset producing one visible
effect in a whole session.

The 10 ms floor is an anti-burst guard: without it a four-packet burst would be smeared across four
seconds, so the throttle is less brutal than 1/s alone suggests.

### The flush was never paced

`ClearDelayQueue(null, Buffer, Buffer ? 0 : 0)  // 1000 / 20 / 3`

The third parameter is flush pacing in milliseconds, its default is 25, and **both branches of the
ternary pass 0**. The comment preserves three values tried and abandoned. Independent corroboration
of findings 4 and 7: **neither reference tool paces a release**, and this one stopped trying.

The flush itself groups by peer and runs those groups in parallel, ordered by scheduled time within
each. So ordering is guaranteed per peer and not across peers - which is the same conclusion reached
here independently, that a single FIFO suffices because cross-peer order carries no meaning.

The second parameter is `sendSaved`, and it is passed the `Buffer` flag. **A slow queue is therefore
discarded on untick unless buffering is separately enabled.** d2vpn always dumps, which is a
deliberate difference rather than an oversight.

### 8. Built-in recording

Screen (960x540 compressed JPEG), a pcap of all Destiny-relevant ports except 443, and a JSON
event log of module toggles: `{"ts":"...","module":"PVE","event":"deactivated"}`. Nothing is
auto-uploaded. Worth having - it turns every session into a debugging artifact, and it is
essentially the Phase 0 tooling built into the product.

### Unexplained

`Super З0k` is described by its author only as "Propietary tech - use for holding joining and/or
objective updates, do not use for holding reconnects." `Weasel` is undocumented. Neither has
config settings to infer from. These stay unknown unless measured on the wire.

## Boundary

`BitcoinMiner.dll` is a 5.2 MB .NET assembly and would decompile in seconds. **We do not do
that.** Reading its README and config is reading documentation the author wrote for users;
decompiling and transcribing its logic would produce derivative code, which is a genuine problem
for a repository intended as portfolio work, and would defeat the point of building it.

What we use instead, all legitimate:
- HarryWatch's **source**, as an architectural reference for the shared ancestral design.
- BitcoinMiner's **README and config**, as a specification of what good behaviour looks like.
- BitcoinMiner's **observable network behaviour**, measured with our own captures.

## Note for Phase 1

BitcoinMiner targets `net10.0-windows`; only the .NET 9 SDK (9.0.306) is installed here. d2vpn
targets `net9.0-windows` unless we install the .NET 10 SDK. Nothing in the plan depends on 10.

Also: BitcoinMiner holds a WinDivert handle while running. Two handles with overlapping filters
both receive every matching packet and can each drop or reinject it independently, so
**BitcoinMiner must be fully exited during any d2vpn test**, not merely idle.

## HarryWatch's instance timer - read from source

Read out of `HarryWatch-1.2`: `Interception/PacketProviders/XboxProvider.cs` and
`Utility/D2CharacterTracker.cs`. **Not verified by measurement here**, like everything else in this
file - but it is read from the implementation rather than inferred from a dependency list.

It is a hybrid, and the traffic half comes first.

### Traffic: a ten-second gap on 3074

`XboxProvider.StorePacket` is called for every packet the provider sees, and the whole rule is one
comparison:

```
if ((DateTime.Now - last).TotalSeconds > 10)
    InstanceStarted = DateTime.Now;
last = p.CreatedAt;
```

A gap of more than ten seconds in **UDP 3074** traffic starts a new instance, dated from the packet
that broke the silence. No connection tracking, no addresses, no flags.

Leaving is the same rule read the other way, in `InstanceDuration()`: more than ten seconds since
the last packet returns `TimeSpan.Zero`, meaning not in one. A gap between one and ten seconds
returns the previous value rather than recomputing, so a brief stall does not make the figure
stutter.

### API: the server's own start time, as a correction

`D2CharacterTracker` resolves a Bungie name typed into settings - `Name#1234`, via
`SearchDestinyPlayerByBungieName` - then polls `GetProfile` for the `CharacterActivities`
component and reads **`DateActivityStarted`**.

```
var savedTime = UserActivities.First().Period.ToLocalTime().AddSeconds(5);
var apiTime   = ActiveCharacter.DateActivityStarted.ToLocalTime();

if (savedTime > apiTime) return;        // history newer than "current" - stale, ignore
if (InstanceStarted != apiTime && !usedStarts.Contains(apiTime))
    InstanceStarted = apiTime;          // overwrite the traffic guess
```

So the traffic detects the change at once and sets an approximate start; the API then replaces it
with the server's authoritative time. `GetActivityHistory` is cross-checked against it so a stale
`CurrentActivity` cannot drag the timer backwards, and `usedStarts` stops one timestamp being
applied twice.

**No OAuth.** Public components and an API key are enough, provided the player's privacy settings
allow it - the code checks for an empty `CharacterActivities` and calls that "private profile".

Polling is bounded rather than continuous: a detected change starts a task that polls twenty times
at twenty-second intervals and then stops, cancelling and restarting on the next change. Underneath
that, current activity is rate-limited to once per 15 s and history to once per 30 s.

**An API key is hardcoded in the source.** Not reproduced here - it is someone else's, and
republishing it would share its rate limit and its liability. Worth knowing as a decision to avoid:
a shipped key is one revocation away from breaking for every user at once.

### Where d2vpn differs, and why

| | HarryWatch | d2vpn |
|---|---|---|
| Port watched | UDP 3074 | TCP 30k |
| Entering | first packet after a 10 s gap | connection to a server not already in use |
| Leaving | the same 10 s gap | server closes (FIN), else 30 s silence |
| API | corrects the start time | not built yet |

Two differences worth stating plainly.

**3074 is peer traffic.** It is busy in both directions during play, which makes a ten-second gap a
very strong signal - but it is *peer* traffic, so being alone with no peers nearby is the same
silence as a loading screen. 30k is client-to-server and carries a heartbeat every 3.02 s whatever
else is happening, which is why d2vpn watches it.

**d2vpn notices leaving sooner, when it notices at all.** A FIN ends the instance immediately where
HarryWatch waits out ten seconds of quiet. But measurement here found a return to orbit that sent no
FIN, so that advantage does not hold for every departure - and HarryWatch's single blunt rule covers
every case the same way, which has something to be said for it.

---
