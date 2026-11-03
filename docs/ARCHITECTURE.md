# Lumen – Architecture

Lumen is a controller-first, fullscreen gaming shell that runs **on top of** the normal Windows 11
shell. It never replaces `explorer.exe`, never edits the Winlogon `Shell` value and never stores
passwords. Console behaviour comes from three ordinary, reversible pieces: a per-user startup entry,
a fullscreen window, and (optionally) Windows' own automatic sign-in configured with Microsoft tools.

## Solution layout

```
Lumen.sln
├─ src/
│  ├─ Lumen.Core/               net10.0 – no Windows dependencies, fully unit-tested
│  │   ├─ Models/               GameEntry, LaunchSpec, ArtworkSet, GameSource
│  │   ├─ Library/              IGameScanner + Steam/Epic/GOG/Folder scanners, VDF parser,
│  │   │                        LibraryService (merge rules), repository contracts
│  │   ├─ Input/                GamepadState, InputMapper (repeat, hysteresis, chords)
│  │   ├─ Hardware/             HAL contracts, device profiles (OneXPlayer 3), composer
│  │   ├─ Power/                PowerPolicy (battery-aware shell budget)
│  │   ├─ Startup/              CrashLoopGuard, StartupOptions
│  │   └─ Diagnostics/          Log (file logger, startup phase timing)
│  ├─ Lumen.Data/               net10.0 – SQLite (Microsoft.Data.Sqlite): schema, migrations,
│  │                            game repository, settings store
│  ├─ Lumen.Platform.Windows/   net10.0-windows – Win32/WinRT behind Core contracts:
│  │   ├─ Input/                XInput (4 slots, merged, hot-plug, haptics)
│  │   ├─ Launch/               GameLauncher (URI / exe / packaged AUMID), PlaySessionTracker
│  │   ├─ Library/              XboxScanner (PackageManager + MicrosoftGame.config)
│  │   ├─ Hardware/             battery, Core Audio volume, WMI brightness, display modes,
│  │   │                        power plans, CPU/GPU/RAM/thermal-zone monitor, factory
│  │   ├─ Power/                suspend/resume + display on/off notifications
│  │   └─ Shell/                startup registration, auto sign-in inspector, desktop
│  │                            switching, window message hook, recovery actions
│  ├─ Lumen.App/                WinUI 3 (Windows App SDK) – the shell UI
│  │   ├─ Program.cs            single instance + activation redirection
│  │   ├─ AppHost.cs            composition root, fail-safe startup
│  │   ├─ MainWindow            fullscreen host, top bar, legend, toasts, routing
│  │   ├─ QuickAccessWindow     separate always-on-top side panel
│  │   ├─ Views/                Home, Library, Settings, SafeMode pages
│  │   ├─ Controls/             GameTile, ButtonHint
│  │   ├─ Input/                GamepadNavigator (poll + route), FocusNavigation (XY focus)
│  │   └─ Services/             LibraryStore (UI-thread cache), GameActions
│  └─ Lumen.Recovery/           console tool: restore normal Windows (no UI framework)
├─ tests/
│  ├─ Lumen.Core.Tests/         scanners, VDF, merge rules, input mapper, profiles, crash guard
│  └─ Lumen.Data.Tests/         SQLite schema, repository, settings
├─ installer/inno/Lumen.iss     conventional installer
├─ scripts/                     build.ps1, publish.ps1
└─ docs/
```

Dependency direction is strictly inward:

```
Lumen.App ──► Lumen.Platform.Windows ──► Lumen.Core
    │                                      ▲
    └──────► Lumen.Data ───────────────────┘
Lumen.Recovery ──► Lumen.Platform.Windows, Lumen.Core
```

Everything with logic worth testing lives in `Lumen.Core` behind interfaces (`IRegistryReader`,
`IGameRepository`, `IGamepadSource`, hardware provider interfaces), so it builds and tests on any OS
(CI runs it on Linux too).

## Key flows

### Startup (fail-safe)
1. `Program.Main` – registers the single-instance key; a second launch redirects to the running
   shell, which comes back to the foreground.
2. `AppHost.Create` – logging → recovery flag check → `CrashLoopGuard.OnStartup` → database
   (falls back to an in-memory library + safe mode if SQLite fails) → services. Each phase is
   timestamped in the log (`[startup +123 ms] ...`).
3. `MainWindow` – fullscreen presenter (unless safe mode / `--windowed` / setting off), hotkey,
   power notifications, controller polling, then Home (or the Safe Mode page).
4. Library loads from SQLite immediately; a background scan updates it a moment later.
5. Hardware probing (WMI, performance counters) runs lazily off the startup path.
6. After 60 s of stable running the crash history is cleared.

### Crash loop protection
`CrashLoopGuard` records "session active" on start and clears it on clean exit/sign-out. Three
unclean exits within ten minutes ⇒ next start is **safe mode**: windowed, no scanning, a page with
"Go to desktop / Stop autostart / Open logs / Continue". The recovery tool can force safe mode via
`%LOCALAPPDATA%\Lumen\recovery.flag`.

### Controller input
`XInputGamepadSource` polls 4 slots (disconnected slots only every 2 s) and merges all connected
pads into one logical pad (handles the OneXPlayer 3's detachable controllers whether they appear
as one or two devices). `InputMapper` (pure, unit-tested) turns snapshots into `NavAction`s with
press edges, auto-repeat (400 ms → 110 ms → 55 ms), stick hysteresis, trigger thresholds and chords.
`GamepadNavigator` polls at 60 Hz while Lumen is foreground, 5–15 Hz otherwise, and in the
background only reacts to the global chords. Actions are routed to the overlay (if it is focused) or
to the current page, then to default behaviour: XY focus (`FocusManager.TryMoveFocus` with a search
root), invoke via UI Automation patterns, tabs, back.

| Input | Action |
|---|---|
| D-pad / left stick | Move focus (sliders: change value) |
| A | Activate / launch |
| B | Back (to Home) |
| Y | Toggle favorite |
| LB / RB | Previous / next tab |
| LT / RT | Previous / next shelf or filter |
| Right stick | Scroll |
| ≡ (Menu) | Quick access (inside Lumen) |
| ⧉ + ≡ (View + Menu) | Quick access – works in games too |
| ⧉ + ≡ + LB + RB, hold 3 s | Emergency: leave fullscreen and show the desktop |
| Ctrl + Alt + Home | Bring Lumen back (keyboard / remappable vendor button) |

### Game library
Scanners return `GameEntry` records; `LibraryService` merges them into SQLite:
discovery fields come from the scan, user fields (favorite, hidden, play stats, custom cover) are
preserved, vanished games are marked *not installed* (never deleted), a failing scanner leaves its
games untouched, manual entries are never touched.

| Source | Discovery | Launch | Artwork |
|---|---|---|---|
| Steam | `libraryfolders.vdf` + `appmanifest_*.acf` (StateFlags & 4) | `steam://rungameid/{id}` | local `appcache/librarycache` (flat & per-app layouts), optional Steam CDN |
| Epic | `ProgramData\Epic\...\Manifests\*.item` (DLC/engines skipped) | `com.epicgames.launcher://apps/ns:item:app?action=launch` | placeholder |
| GOG | `HKLM\SOFTWARE\WOW6432Node\GOG.com\Games` (DLC skipped) | game exe (or Galaxy, opt-in) | placeholder |
| Xbox / Store | `PackageManager.FindPackagesForUser` + `MicrosoftGame.config` | `IApplicationActivationManager` (AUMID) | `ShellVisuals` logos |
| Folders | each sub-folder; exe picked by heuristics | exe | `cover.jpg/png` in the folder |

### Hardware abstraction layer
See [HARDWARE.md](HARDWARE.md). In short: one interface per capability, a device profile that can
only *permit* documented features, and a composer that exposes a provider only if it exists **and**
the profile permits it. No profile permits TDP or fan control; no such provider ships.

### Data
SQLite at `%LOCALAPPDATA%\Lumen\lumen.db` (WAL). Schema versioned with `PRAGMA user_version`;
forward-only idempotent migrations. Logs in `%LOCALAPPDATA%\Lumen\logs` (7 days).

### Resource use
- No polling of anything while the display is off.
- Controller polling drops to 5–15 Hz when a game is in the foreground.
- Hardware monitor samples only while the quick-access panel is open.
- Artwork decoded at ~2× tile size; tile view models are cached and shared between pages.
- ItemsRepeater virtualisation for large libraries.
- Low battery (≤ 20 % or battery saver): slower polling, no focus animations.
