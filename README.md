# Fanner

A GUI for the fans on your motherboard. Windows, .NET 10, Avalonia.

Reads every fan header and temperature, drives any header manually or from a
temperature curve, keeps named profiles, and can start with Windows.

![Fanner running a temperature curve on an MSI X870 board](docs/screenshot.png)

**[Download the latest release](https://github.com/f0rmat1k/Fanner/releases/latest)** — a single
self-contained executable. Read [Requirements](#requirements) first: it needs the PawnIO
driver and administrator rights.

## What it talks to

On a desktop board, fan control is not a vendor feature — it is a Super I/O chip.
Fanner reaches it through [LibreHardwareMonitor][lhm], which covers the common
Nuvoton and ITE parts. Developed against an MSI X870 GAMING PLUS WIFI with a
**Nuvoton NCT6687D-R**, which is what most of MSI's current desktop range uses, so
support is broad rather than board-specific.

GPU fans come along for free through the vendor APIs.

MSI *laptops* are a different path entirely — vendor WMI and EC registers, not LPC.
Fanner does not handle those.

## Requirements

- Windows 10 or 11, x64
- **[PawnIO][pawnio]**, a separate install — see below
- Administrator rights

The released executable is self-contained, so it needs no .NET runtime.
Building from source needs the [.NET 10 SDK][dotnet].

### About the driver

Reading a Super I/O chip means port I/O from ring 0, which needs a kernel driver.
Fanner uses **PawnIO**: signed, and compatible with Memory Integrity (HVCI), so
nothing about your system's security has to be turned off to run it. It replaces
WinRing0, which is deprecated, flagged by Defender, and on Microsoft's vulnerable
driver blocklist — on a current Windows 11 install it would not load at all.

Install it once:

```powershell
winget install namazso.PawnIO
```

Or get `PawnIO_setup.exe` from [pawnio.eu][pawnio] and run `-install`.

Without it, nothing throws and nothing is logged — every ring-0 read just returns
zero, which looks exactly like an unsupported motherboard. Fanner checks for the
driver at startup and says so rather than showing you an empty list.

## Running

```powershell
dotnet run --project src/Fanner.App
```

Fanner starts unelevated on purpose: you get a window that explains what it cannot
do and a button to restart with rights, instead of a UAC prompt before you have seen
anything.

To work on the UI with no driver, no elevation and no risk to your fans:

```powershell
dotnet run --project src/Fanner.App -- --simulate
```

The simulated board mirrors the real one, awkward parts included — non-contiguous
header indices, headers that report a duty but never spin.

```powershell
dotnet test
```

## Layout

| Project | |
|---|---|
| `Fanner.Core` | Models, the `IHardwareBackend` contract, the polling service, rolling history, the curve engine, the watchdog, profiles and config persistence, and the simulated backend. No dependency on any hardware library. |
| `Fanner.Hardware.Windows` | LibreHardwareMonitor behind that contract, plus PawnIO detection. |
| `Fanner.App` | Avalonia UI, MVVM, the tray icon and the logon task. |
| `Fanner.Core.Tests` | The parts worth pinning down: the empty-header heuristic, curve evaluation, smoothing, deadband and kickstart, watchdog thresholds, config round-tripping and migration, monitor lifecycle and the release-on-exit guarantee. |

Everything that touches hardware runs on one thread, owned by `MonitorService`.
Writes are queued to it rather than called directly, so the UI thread never re-enters
the driver mid-poll.

### Reading a fan card

The badge says who is driving the header: **BIOS** for the motherboard firmware,
**FANNER** once you have moved its slider. `↺` gives one fan back; **Restore all to
BIOS** gives back everything and appears whenever any fan is held.

Headers that are being driven but report no tachometer are hidden: they are almost
certainly empty. This board exposes ten and six are wired. A fan at **0 % duty** is
*not* hidden — it is stopped, not absent, and hiding it would make a fan you just
turned off disappear. A header we have driven to a standstill is flagged
**STOPPED** and outlined, because that is the one outcome of a drag that can cost
hardware.

### Why the number lags the slider

The chip does not jump to a new duty cycle, it travels — about **2 % per second**
on an NCT6687D, so 60 % to 85 % takes roughly fourteen seconds. While it is moving,
the card reads `69 % → 96 %`: what the fan is doing now, and where it is headed.
Nothing is stuck.

### Curves

`∿` on a card binds that fan to a temperature source and starts driving it. Drag the
points to reshape; the dashed marker shows where the source currently reads, so the
shape means something while you draw it. Three presets are a starting point, not a
constraint.

A curve is more than a lookup table, because three things break the naive version:

- **Response** smooths the source. A CPU temperature moves ten degrees in a second
  when a core wakes up, and a curve that chases it makes the fans surge constantly —
  far more irritating than a steady speed slightly too high.
- **Deadband** is the smallest change worth writing. Without it the duty shuffles by
  a fraction of a percent every second forever and the fan never settles.
- **Kickstart** gets a stopped fan turning. Most fans will not start from rest at the
  duty that keeps them spinning, so a curve asking for 15 % on a stopped fan leaves
  it stopped — looking, alarmingly, like a dead fan. A short burst fixes it. Capped
  at three attempts, since an empty header never reports RPM however hard it is
  driven.

Sources are named `channel · hardware`, because several drives report a channel
called simply "Temperature" and the bare name cannot tell them apart.

### Profiles

![Profiles and settings](docs/settings.png)

A profile is a named set of fan settings — curves and fixed duties together, because
a real setup mixes them: a curve on the CPU fan, the pump pinned at 100 %, the rest
left to the firmware. Switch profiles from the header or the tray icon.

The active profile always mirrors what is running: every edit writes straight into
it, so there is no such thing as unsaved changes. A new profile starts as a copy of
what is running now, which is almost always what you want after tuning something.

Switching **releases any fan the incoming profile does not mention**. Without that,
moving from a profile that pinned a fan to one that does not would leave it stuck at
the old duty, silently inheriting a setting from a profile you just left.

Everything lives in `%APPDATA%\Fanner\config.json` and is applied on the next launch.
Entries naming hardware that is no longer present are dropped rather than kept, so a
fan never looks configured when nothing is driving it.

### Tray and startup

Closing the window hides Fanner to the tray rather than quitting, because a fan
controller that stops controlling fans when you close its window is rarely what
closing a window is meant to mean. **Quit Fanner** in the tray menu genuinely stops
it, handing every fan back on the way out. The behaviour is a setting if you disagree.

**Start with Windows** registers a scheduled task, not a `Run` registry entry. The
registry key is the obvious choice and the wrong one: it launches without elevation,
so Fanner would come up at every logon showing "administrator rights needed" and
controlling nothing. A scheduled task with `HighestAvailable` starts elevated and
raises no UAC prompt. It starts Fanner hidden, so a logon does not put a window in
front of you.

### The watchdog

While any fan is held, Fanner watches temperatures and hands **everything** back to
the firmware if one crosses its limit — 95 °C CPU, 90 °C GPU. The limits are high on
purpose: a 9800X3D sits in the low nineties under sustained load by design, and a
warning that fires during ordinary gaming is a warning people learn to ignore.

Handing control back beats ramping to 100 %, because the board's own curve is the
known-good configuration and the duty cycle we picked is exactly what is in doubt at
that moment.

Tripping also **pauses the curves**. Releasing the fans without stopping the engine
would be theatre: the next poll would evaluate the curve and take them straight back.
Resuming is a separate, deliberate button — acknowledging that a chip got too hot
should not silently restart the thing that let it.

**Restore all to BIOS** pauses rather than deletes. A panic button that quietly
destroys the setup is one nobody presses when they need to.

Fans are never left pinned to a software duty cycle: the monitor hands every header
back on the way out, and the release runs on the hardware thread before the UI is
even told, so a hung window cannot delay it. The one case it cannot cover is the
process being killed outright — no user-mode program survives `TerminateProcess`.
The firmware resumes control on the next reboot regardless.

## Roadmap

- [x] Read fans, duty cycles and temperatures; live charts
- [x] Detect and explain missing driver or missing rights
- [x] Manual duty slider per fan, with a "return control to BIOS" button
- [x] Temperature watchdog
- [x] Temperature-to-speed curves, saved between runs
- [x] Profiles, tray icon, run at startup

## Licence

Fanner is MIT licensed — see [LICENSE](LICENSE).

`Fanner.Hardware.Windows` links [LibreHardwareMonitorLib][lhm], which is MPL-2.0.
MPL is file-level copyleft: it covers that library's own files, not the code that
uses it, so the rest of this repository stays under MIT.

[lhm]: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
[pawnio]: https://pawnio.eu/
[dotnet]: https://dotnet.microsoft.com/download/dotnet/10.0
