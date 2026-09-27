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

## HarryWatch's delay values, read from source

`HarryWatch-1.2/Interception/Modules/PveModule.cs` and `PacketProviderBase.cs`. **Read from its
code, not measured here** - and HarryWatch carries no license, so these are facts to compare
against, never code to lift.

| Setting | Value |
|---|---|
| `SlowInbound` / `SlowOutbound` delay (3074) | **1 second** |
| `Buffer` (block mode) delay (3074) | **35 seconds** |
| Delay queue capacity | **2048**, evicting 16 at a time |
| Default delay when unspecified | 1 second |
| Flush pacing on untick | **0 ms** - unpaced |

Two things stand out more than the numbers.

### HarryWatch has no "drop" - a block *is* a delay

Blocking is `DelayPacket(p, 35s)`. There is one mechanism, and block versus slow is only its depth:
35 seconds against 1. That is a cleaner idea than this project's separate drop and buffer paths,
and it explains the 35 s figure already recorded in the thresholds table.

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

Block mode passes `addFromLatest: false`, so each packet is delayed 35 s from *its own* arrival.
That is a true fixed delay line.

**So the two modes differ in kind, not only depth**: block is a fixed offset deep enough to act as
a drop, slow is a rate limiter at one packet per second per peer.

d2vpn's `--slow` is a fixed offset, which is the block-mode semantic at a shallow depth rather than
the slow-mode semantic. That is a deliberate specification here, not an oversight - but it means a
constant amount of lag, where HarryWatch's produces a desync that deepens the longer it is held.
Worth knowing when a uniform delay turns out to have little in-game effect (finding 40).

### The flush was never paced

`ClearDelayQueue(null, Buffer, Buffer ? 0 : 0)  // 1000 / 20 / 3`

The parameter is flush pacing in milliseconds, its default is 25, and both branches pass 0. The
comment preserves three values that were tried and abandoned. Independent corroboration of findings
4 and 7 here: **neither reference tool paces a release**, and this one stopped trying.

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
