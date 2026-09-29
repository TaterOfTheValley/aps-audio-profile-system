# APS — Audio Profile System

**Saved audio device profiles, held in place.**

Windows reassigns default audio devices on its own. Plug in a headset, dock a laptop,
wake a monitor with HDMI audio, and the default output or the default microphone moves
somewhere you did not put it. There is no notification. The first time you find out is
when you join a call and nobody can hear you, and you spend the first ninety seconds of
the meeting in Sound settings instead of in the meeting.

APS keeps named profiles of your input and output devices, switches between them from a
tray menu or a hotkey, and — this is the part that matters — notices when Windows moves
a default away from the profile you are on, and can put it back.

Fast switching is the convenience. Being told, and optionally corrected, is the fix.

---

## Status

Pre-release. The settings format is versioned and migrated, but it has not been tested
widely.

MIT licensed — see [LICENSE](LICENSE).

---

## Install

Download from the [APS releases](https://github.com/TaterOfTheValley/aps-audio-profile-system/releases).
During the alpha period, use the releases list because GitHub's **Latest** link
excludes prereleases.

| Download | Use |
|---|---|
| `APS-AudioProfileSystem-win-Setup.exe` | **Recommended.** Installs APS once, adds shortcuts, and enables in-app updates. The .NET runtime is bundled. |
| `APS-<version>-win-x64.exe` | Standalone EXE. Run it from a folder you choose; updates are manual. The .NET runtime is bundled. |
| `APS-<version>-win-x64-requires-dotnet8.exe` | Small standalone EXE. Requires the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). Updates are manual. |

If you already run a standalone APS, **Exit** it from the tray before running
`APS-AudioProfileSystem-win-Setup.exe` for the first time, and remove the old EXE or
shortcut once the installed copy opens. Both copies use the same profiles, so no export is
needed. The installer is per-user and needs no administrator rights. It installs under
`%LOCALAPPDATA%\APS-AudioProfileSystem`; profiles remain under `%LOCALAPPDATA%\APS`.
Windows' Installed apps list and the installed shortcuts call it **APS — Audio Profile
System**, with **TaterOfTheValley** as the publisher. The executable, shortcuts,
installer, update dialog, and tray share the same speaker icon; the tray adds a lock
badge while a profile is being watched.

For manual standalone updates, put the EXE in a permanent folder as `APS.exe`. On each
update, exit APS, replace that file with the newly downloaded version (renamed to
`APS.exe`), then launch it. The first launch after moving a copy repairs the **Start with
Windows** path if that setting is enabled.

### Updates

Installed copies check for updates quietly after startup and every 12 hours while
running. When one is available, APS shows a tray notification — click it, or choose
**Check for updates** or **Update to APS ...** from the tray menu — review the release
notes, then select **Install and restart**. APS downloads and verifies the package, exits,
applies the update, and relaunches. You can choose **Later** and keep working. A
standalone EXE directs you to the installer when you choose **Check for updates**.

Profiles and hotkeys live outside the installed application, so updates preserve them.

**Stable or alpha.** An installed copy follows one of two channels, and the tray menu's
**Include alpha builds** tick box picks which:

- **Stable** (the default for a full release) offers only full releases such as `0.2.0`.
- **Alpha** also offers the automatic builds made from `main` — `0.2.1-alpha.4` and so
  on. Every push that changes the app publishes one, numbered from the last stable
  release, so they lead up to the next patch version and are replaced by it when it ships.
  Installing an alpha build starts you on this channel.

Unticking the box on an alpha does not step back to an older release; you stay where you
are until a stable release newer than your build appears.

> **Windows will warn you the first time.** The executable is not code-signed, so
> SmartScreen shows *"Windows protected your PC"*. Choose **More info → Run anyway**.
> Every release ships a `SHA256SUMS.txt` for the standalone downloads if you would
> rather verify one first: `Get-FileHash .\APS-<version>-win-x64.exe`.

## Requirements

- Windows 11, x64. Developed and tested on Windows 11. Nothing here is knowingly
  11-only, but Windows 10 is untested — and the load-bearing COM
  interface is undocumented, so that is a real gap rather than a formality.
- To **run** a release build: nothing. Releases are self-contained.
- To **run** a local `build.cmd` build: the
  [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
- To **build**: the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

APS runs as a tray icon. There is no main window.

---

## Using it

Click the tray icon and the menu opens above it.

**Profiles.** Each one names an output, optionally a separate output for calls, and an
input. Clicking a profile applies it. A filled gold dot marks the profile the machine
actually matches right now; a hollow ring marks the one you last picked when nothing
matches any more. When no profile matches, the menu says so above the "Now" block rather
than leaving you to notice an absent dot.

**Now.** A block showing what is actually in effect this second — the question the app
exists to answer, one click away and with no settings page to read.

**Add a profile** captures whatever is in use now and opens a name field on the new row.

**Each profile row** carries three glyphs: rename, update, and delete.

| Glyph | Does |
|---|---|
| ✎ | Rename, in place on the row |
| ↻ | Update the profile to whatever is in use now, keeping its name, hotkey, place and settings |
| 🗑 | Delete, with a confirmation on the row |

Drag a row to reorder the list. From the keyboard: <kbd>F2</kbd> renames,
<kbd>Delete</kbd> deletes, <kbd>Ctrl</kbd>+<kbd>↑</kbd>/<kbd>↓</kbd> moves a profile up
and down, <kbd>Esc</kbd> closes.

**Tell me when "X" changes** watches the profile you are on. If Windows moves one of its
defaults, you get a notification, with switching back one click away.

**Put it back automatically** turns that notification into a correction: APS restores the
profile itself — within five seconds by default, and the row says which interval is
currently set — and tells you it did. It gives up after three
corrections in ten minutes, on the grounds that losing an argument with another
application should end rather than continue forever in the background.

**Undo last switch** appears for 20 seconds after any apply and restores exactly what was
there before.

### Hotkeys

A profile can carry a global hotkey. There is no picker yet — set it in the settings file
(see below) as a `+`-separated string:

```json
"Hotkey": "Ctrl+Alt+1"
```

`Ctrl`, `Alt`, `Shift` and `Win` are the modifiers; the key can be a letter, a digit, or
any `System.Windows.Forms.Keys` name (`F9`, `Numpad3`, `Oem3`). A hotkey another
application has already claimed simply fails to register.

---

## Settings

One JSON file:

```
%LOCALAPPDATA%\APS\.aps
```

Local rather than Roaming on purpose — a profile identifies devices by endpoint IDs
specific to one machine's driver stack, so roaming it somewhere else would sync data
that cannot match anything there.

**Portable mode.** Put an empty file named `.aps` beside `APS.exe` and settings live in
that folder instead and travel with it.

"Edit profiles…" in the tray menu opens the file. Alongside the profiles it holds
settings that have no UI, and **editing them here is how you override the defaults** —
APS is watching the file, so a saved change takes effect without a restart.

| Field | Default | Meaning |
|---|---|---|
| `PollSeconds` | `5` | How often to check whether the devices have moved. Set it to anything from 2 to 300; values outside that are ignored in favour of the default rather than rewritten, so the file keeps saying what you typed. Longer is cheaper and slower to notice; shorter is the opposite. The "Put it back automatically" row in the menu shows the value in force. |
| `FormatVersion` | `1.2` | Read before the data is interpreted. MAJOR changes mean a build refuses the file rather than misreading it; MINOR changes are additive and migrate forward automatically. |

An existing file keeps the interval already written into it — a new default only applies
to a `PollSeconds` that is missing or out of range. Change the number in the file to pick
one up.

---

## Building

```
build.cmd                  framework-dependent, fast, needs .NET 8 installed
build.cmd selfcontained    what a release actually ships (~68 MB, slow)
run.cmd                    build if needed, then launch
```

Output goes to `dist\`, which is gitignored — releases are built by CI, so a binary in
the repo would only ever be a stale copy of one.

`dist\APS.exe` cannot be overwritten while APS is running. Exit it from the tray first.

The Windows icons are checked in under `src/Assets/` and embedded in the executable,
so no icon files need to sit beside `APS.exe`. To edit the artwork, update
`tools/generate_icon.py`, install Pillow (`python -m pip install Pillow`), and run
`python tools/generate_icon.py` to regenerate both the plain and watching icons.

### Releasing

**Alphas are automatic.** Every push to `main` that changes the app publishes a
pre-release, numbered from the last stable tag: after `v0.1.1` they are `0.1.2-alpha.1`,
`0.1.2-alpha.2`, and so on. Pushes that only touch docs or the license do not.

**Stable releases are a tag.** Push `v0.2.0` (or `v0.2.0-beta`) and
`.github/workflows/release.yml` derives every version number from it. Update
`RELEASE_NOTES.md` first: its text appears in the in-app update dialog and on the release.
Alphas use their commit messages as notes instead. To start work toward a minor or major
version, tag that stable release; the alphas after it count from there.

Either way one GitHub release holds the Velopack installer and update packages, both
standalone builds, and `SHA256SUMS.txt`. The version is never bumped in `APS.csproj` — the
one there is a local-build default that CI overrides.

---

## Command line

Every command below also works as a diagnostic, and all of them accept `--out <file>`:

> **`--out` is not optional in practice.** `APS.exe` is a GUI-subsystem executable, so
> its console writes do not reach a redirected pipe. Run it from a script without
> `--out` and you get nothing. Read the file back as UTF-8 or the em dashes become
> mojibake.

| Command | Does | Touches devices |
|---|---|---|
| `--preflight` | Five environment checks, with reasons | no |
| `--dump-devices` | Every endpoint, all states, with its properties | no |
| `--current` | What holds each default role right now | no |
| `--list-profiles` | Saved profiles and how each reference resolves | no |
| `--capture <name>` | Save what is in use now; updates the profile if the name exists | no |
| `--test-apply <name>` | Resolve everything and report what *would* change | no |
| `--apply <name>` | Switch to a profile | **yes** |
| `--test-pin <name>` | Apply, then watch with a real guard and print every decision. `--seconds <n>`, default 180 | **yes** |
| `--screenshot-icon` | Render the tray icon to a PNG | no |
| `--screenshot-update [path]` | Render the update dialog, for a made-up release, to a PNG. Downloads nothing | no |
| `--screenshot-menu [path]` | Render the tray menu to a PNG. `--scale <n>`, `--state <drag\|rename\|update\|delete\|drifted>`, `--profile <name>` | no |

`--screenshot-menu` exists because the menu is drawn rather than assembled from
`ToolStrip` items, which means it can be rendered to a file and actually checked —
including at high DPI, where clipping shows up. `--state` renders the states a
still of the resting menu cannot show.

---

## How it works, and why it works that way

Three decisions are load-bearing enough to be worth stating up front. All three were
measured on real hardware rather than assumed, because the internet's advice about
this API is out of date.

### Setting a default device has no public API

Every tool in this space uses the undocumented `IPolicyConfig` COM interface, and on
current Windows 11 builds the usual incantation does not work:

| CLSID | Interface | Result |
|---|---|---|
| `CPolicyConfigClient` | `IPolicyConfig` | `E_NOINTERFACE` |
| `CPolicyConfigClient` | `IPolicyConfigVista` | `E_NOINTERFACE` |
| `CPolicyConfigVistaClient` | `IPolicyConfigVista` | **works** |

Only the last pairing returns `S_OK` from `SetDefaultEndpoint`. It is not the pairing
nearly every code sample uses. Had it been assumed rather than probed, the app would have
failed on the only machine it needs to run on.

### It polls, and does not subscribe

Windows publishes device-change notifications through `IMMNotificationClient`, and the
first build used them. Registering that callback destabilises the process: under a burst
of default changes it dies with an access violation on the notification thread, with no
managed frames, and it reproduces with a callback object that does nothing but return
`S_OK`. So APS reads six cheap values on a timer instead, on the same call path `--apply`
has exercised thousands of times without incident. The cost is latency; the alternative
was an app that falls over.

### A device has two identities

Endpoint IDs are exact but not durable — the same speakers in a different USB port get a
different one, and on the development machine a single pair had accumulated four. So each
saved reference also stores a fingerprint (`Desc` + `InterfaceName`) taken from properties
Windows keeps clean of the volatile numeric prefix that pollutes friendly names. The
resolver tries the exact ID first, falls back to the fingerprint, and rewrites the stored
ID when the fallback hits, so the slow path runs once rather than forever.

---

## Repository layout

```
src/                    the app; one namespace, no DI container, and
                        System.Text.Json and Velopack as the only packages
  AudioInterop.cs       MMDevice COM interop
  PolicyConfig.cs       the undocumented setter
  AudioEngine.cs        enumerate, read defaults, dump
  ProfileEngine.cs      capture, apply, match
  DeviceResolver.cs     two-tier device identity
  ProfileGuard.cs       polls for drift; notifies or corrects
  ProfileManager.cs     the versioned settings file
  TrayContext.cs        the tray icon, its menu contents, and every action
  TrayPopup.cs          the menu, drawn rather than assembled
  UpdateService.cs      where updates come from (this repo's releases)
  UpdateForm.cs         the review / download / install dialog
  Preflight.cs          five environment checks
  Program.cs            CLI plumbing and the diagnostics

build.cmd  run.cmd  .github/workflows/{ci,release}.yml
```

The tray menu is a drawn popup rather than a `ContextMenuStrip`: `ToolStrip` sizes rows
from the item's text and its own font, so an owner-drawn row that paints something larger
is simply clipped. Measuring its own content means DPI scaling is one explicit multiplier
instead of an interaction between three layout systems — and it is why the menu can be
screenshotted for review.

---

## License

MIT. See [LICENSE](LICENSE).

Copyright (c) 2026 Patrick Filtz.

The only third-party packages are `System.Text.Json` and `Velopack` (the installer and
updater), both MIT licensed. A self-contained release also bundles the .NET 8 runtime,
likewise MIT. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
