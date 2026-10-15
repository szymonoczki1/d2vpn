# d2vpn

Network traffic control and macro automation for Destiny 2, on Windows.

It intercepts UDP and TCP traffic on specific port groups through the
[WinDivert](https://reqrypt.org/windivert.html) kernel driver, and lets you hold, delay or reset
each group independently while the game is running. A WPF window drives it, a game overlay shows
what is held, and an in-process macro can click loadouts while holding a port.

Every packet-length signature it uses was **measured from live traffic**, not inherited from
another tool. Where a number appears in this codebase there is a capture behind it, and usually a
comment saying which run - see [docs/protocol.md](docs/protocol.md).

---

## Install

**Installer** - download `d2vpn-x.y.z-setup.exe` from Releases and run it. Installs to
`Program Files`, adds a Start Menu entry, and uninstalls cleanly.

**Zip** - download `d2vpn-x.y.z-win-x64.zip`, extract anywhere, run `d2vpn-ui.exe`.

Either way there is nothing else to install. The build is self-contained, so no .NET runtime is
needed, and the WinDivert driver is embedded and extracted on first use.

> **It needs administrator rights.** Opening a WinDivert handle is a privileged operation, so
> Windows will prompt on launch. No other packet limiter may be running at the same time - two
> handles on overlapping filters both receive every packet and both can drop it.

Windows 10/11, 64-bit.

## Quick start

1. Launch it. Accept the elevation prompt.
2. The sidebar lists every hosted module. Pick one and press its key, or flip the switch.
3. **Settings → Overlay** puts a panel over the game showing what is held and for how long.

If a module says it could not open a handle, something else already has one - close any other
limiter first.

## What it can do

| Module | Port | What holding it does |
|---|---|---|
| `3074-ul` / `3074-dl` | UDP 3074 | Peer traffic, one direction at a time. Uplink: your actions stop reaching other players. Downlink: the world stops updating for you. |
| `3074-ul-slow` / `3074-dl-slow` | UDP 3074 | Rate-caps instead of blocking - one packet per peer per interval. |
| `27k-ul` / `27k-dl` | UDP 27015-27200 | The same for player traffic. |
| `30k` / `30k-slow` | TCP 30000-30009 | The instance server connection. |
| `7500` | TCP 7500-7509 | API traffic. |
| `<group>-rc` | — | Injects a reset, forcing a reconnect without dropping the session. |
| `pause` | — | Suspends the game process outright. |
| `macro-1` … `macro-5` | — | Clicks loadouts on a timer, optionally holding a port for the run. |

Each hold has two switches that change while it runs: **autoresync**, which lets the handshake
cross so a long hold does not kill the session, and **buffering**, which keeps withheld packets
and replays them on release instead of destroying them.

The longest validated hold is **15 minutes on `3074-dl`** with no error code - it ended in the
game's own inactivity kick.

## The macro

Up to five presets, each with its own key, rotation and timings. A preset opens the character
screen, cycles the loadouts you list, finishes on the one you name, and can hold `3074-ul` or
`27k-ul` for the length of the run.

Timings are in milliseconds because every one of them is a race against something being drawn.
They are separate settings for a reason: the gap between rotation clicks can be single-digit, but
the clicks that decide the outcome need the game to have caught up first.

Input goes through `SendInput` - the same OS input queue a real mouse feeds. Nothing is attached to
the game: no window is subclassed, no message hook is installed, no memory is read or written.

## Configuration

`%APPDATA%\d2vpn\d2vpn.json`, created on first run and editable from the window. It holds your
keybinds, which modules are hosted, the macro presets and the overlay position.

```jsonc
{
  "modules": [
    { "name": "3074-ul", "bind": "capslock", "resync": true, "buffer": true },
    { "name": "30k",     "bind": "f6",       "resync": true, "buffer": true }
  ],
  "resetBinds": [ { "module": "30k", "bind": "f9" } ],
  "pause": { "enabled": true, "bind": "f11" }
}
```

Comments and trailing commas are allowed. Anything omitted keeps its measured default, so write a
number here only to deliberately override one. `d2vpn config init` writes a fully commented
template.

## Command line

The window is the usual way in; the CLI is where the measurement tools live.

```
d2vpn list                        show the port groups
d2vpn hold 3074 --downlink        hold one group, report what crossed
d2vpn calibrate 3074 --downlink   derive this machine's signature for a group
d2vpn instance 30k                watch for instance changes
d2vpn host --config d2vpn.json    run modules headless, serving the control pipe
d2vpn ctl set 3074-ul resync off  drive a running session from a script
```

`calibrate` is the one worth knowing about. Hold a module partway through a run and it reports the
payload sizes that crossed while that direction was collapsed - the packets a module must let
through for the session to survive. That output is where this project's constants come from.

## Building from source

Needs the .NET 9 SDK. [Inno Setup](https://jrsoftware.org/isinfo.php) is needed only for the
installer; without it you still get the zip.

```powershell
dotnet test                # 599 tests, none need the driver or elevation
.\build.ps1                # test, publish, zip, installer -> dist\
```

## Layout

```
src/D2Vpn.Core/            net9.0, no UI dependency
  Network/                 port groups
  Interception/            WinDivert handles, packet buffer, slot composition
  Modules/                 the traffic modules and their measured options
  Calibration/             per-peer signature finder
  Macros/                  loadout macro, independent of any front end
  Api/                     Bungie API client, for the instance timer
  Processes/               process suspend, for game pause
  Hosting/                 session host, control protocol, config file
src/D2Vpn.Ui/              WPF window and game overlay  (net9.0-windows)
src/D2Vpn.Cli/             command-line front end       (net9.0-windows)
tests/D2Vpn.Core.Tests/    599 tests, none needing the driver or elevation
installer/                 Inno Setup script
tools/capture/             PowerShell capture and analysis scripts
docs/                      measured findings, prior-art analysis, evidence
```

`Hosting` is the seam both front ends attach to. `ModuleHost` holds the modules, `IControllable` is
one addressable toggle, `IModuleOptions` is a switch that changes while running, and `HostConfig`
is the file all of it is configured from - so a front end binds to those rather than to WinDivert.

The tests run without the driver and without elevation, which is deliberate: the interception layer
sits behind interfaces so the state machines, the control protocol, the config and the macro
sequence are all testable without a network or a game.

## Documentation

- [docs/protocol.md](docs/protocol.md) - every measured finding, numbered, with the run behind it
- [docs/prior-art.md](docs/prior-art.md) - what other limiters do, read from their source, and where this differs
- [docs/api.md](docs/api.md) - the Bungie API half of the instance timer
- [docs/evidence/](docs/evidence/) - raw session logs behind the measurements

## Licence

MIT - see [LICENSE](LICENSE).

This is an independent project. It is not affiliated with or endorsed by Bungie, and Destiny 2 is
their trademark. It modifies network traffic to and from your own machine; what that is used for,
and whether it is within the terms you have agreed to, is your decision.
