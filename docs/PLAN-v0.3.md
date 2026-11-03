# Lumen v0.3 – audit and plan for the "SteamOS-like" specification

This supersedes the remaining items of `PLAN-v0.2.md` (Features 1–3 of v0.2 are done; Feature 4
"instant resume" is folded into Phase 5 below).

## 1. Audit – what exists today (branch `claude/v0.2`)

**Stack:** C# / .NET 10, WinUI 3 (Windows App SDK 1.8), XInput, SQLite (Microsoft.Data.Sqlite),
Inno Setup installer, GitHub Actions CI (Linux tests + Windows build/publish/installer).
**Architecture:** `Lumen.Core` (pure, tested) → `Lumen.Data` (SQLite) → `Lumen.Platform.Windows`
(Win32/WinRT) → `Lumen.App` (WinUI) + `Lumen.Recovery` (console). 100+ unit tests.

| Area | Status | Notes |
|---|---|---|
| Fullscreen controller-first shell, Home/Library/Settings, safe mode, crash-loop guard | ✅ done | v0.1 |
| Steam / Epic / GOG / Xbox(GDK) / folders discovery, favorites, recent, play count | ✅ done | |
| Cinematic hero (cross-fade, Ken Burns, art accent colour), artwork disk cache, launch curtain, tab slides | ✅ done | v0.2 F1 |
| Quick access: separate topmost window, Quick / Game / System sections, close game, Wi-Fi/BT toggles | ✅ done | v0.2 F2 |
| Brightness (WMI), volume/mute (Core Audio), refresh rate, power plans, CPU/GPU/RAM, ACPI thermal zone | ✅ done | documented APIs |
| Foreground game detection (any store), per-game profiles (refresh, plan, brightness), baseline restore incl. crash recovery | ✅ done | v0.2 F3 |
| Startup at sign-in (Run key / MSIX task), auto sign-in via Microsoft tools, recovery tool | ✅ done | |
| **TDP** | ❌ missing | contract `ITdpControl` exists, no backend |
| Battery wattage / runtime / health | ❌ missing | only % and AC state |
| FPS telemetry / limiting, performance overlay | ❌ missing | |
| Power menu (sleep, hibernate, restart, shutdown, restart Lumen, Task Manager) | ❌ missing | |
| Search, collections, hide/unhide, launch args, EA/Ubisoft/Battle.net | ❌ missing | |
| Sounds, themes, docked mode, remote play, statistics | ❌ missing | |

## 2. Hardware reality on the OneXPlayer 3 (Intel Arc G3 Extreme, "Panther Lake")

- **TDP.** Intel package power limits live in MSR `0x610` (PL1/PL2), units in `0x606`, limits in
  `0x614`, live energy in `0x611`. Writing MSRs needs a kernel driver. Lumen will **not** ship or
  silently install a driver. The supported route is **PawnIO** (open-source, signed, works with
  Memory Integrity; the replacement for the vulnerable WinRing0) with its signed `IntelMSR` module,
  which explicitly allows reading/writing `MSR_PKG_POWER_LIMIT`. The PawnIO device is restricted to
  SYSTEM/Administrators, so a small **elevated helper** (`Lumen.PowerHelper`) is used, started through a
  scheduled task the user creates once with explicit consent (one UAC prompt, removable at any time).
- Limits are **read from the CPU** (`0x614`, firmware PL2), never hard-coded: presets (Eco 10 W,
  Balanced 20 W, Performance 28 W, Turbo 35 W) are targets clamped to what the CPU/firmware report;
  presets outside the range are disabled with the reason. A firmware **lock bit** makes TDP read-only.
- Firmware/Intel DTT may also enforce MMIO limits; the effective limit is the lower of the two. Lumen
  shows *requested*, *MSR value read back* and *measured package power* (from `0x611`) so you can see
  what really happens. Original limits are saved and restored on exit/crash/uninstall.
- AMD: the HAL has an AMD slot; no AMD backend ships in this version (no hardware to verify on).
- **Fan control:** the OneXPlayer EC interface is not documented for this model → stays unsupported.
- **CPU/GPU temperature:** readable via `IntelMSR` (`IA32_PACKAGE_THERM_STATUS` + `TEMPERATURE_TARGET`)
  through the same helper → added when the helper is enabled.

## 3. Things Windows does not allow without invasive techniques (documented, not faked)

| Request | Decision |
|---|---|
| FPS **limiting** for any game | No non-invasive universal limiter exists on Intel/Windows (RTSS and similar inject into games → anti-cheat risk). Lumen offers *refresh-rate based* pacing (clearly labelled, not a limiter) and points to the game's / Intel Graphics Software's limiter. Optional RTSS integration may be added later, opt-in, with the anti-cheat warning. |
| FPS **telemetry** | Possible with ETW present events (PresentMon approach) in the elevated helper → planned Phase 3b. |
| Audio output switching, night light, airplane mode, rotation lock | No public API → controller-friendly shortcuts to the exact Windows Settings page. |
| HDR toggle, resolution | Documented (DisplayConfig / ChangeDisplaySettingsEx) → implemented. |
| Gyro, mouse emulation, button remapping of OEM keys | Vendor buttons may not be XInput; remapping stays in OneXConsole. Stick-as-mouse "desktop mode" planned Phase 5. |
| Universal game suspend / instant game resume | Not possible on Windows; not promised. |

## 4. Implementation order (each phase: build + tests in CI before the next)

1. **Phase 2 – Core system controls:** battery telemetry (`Windows.Devices.Power` report: watts,
   runtime, health); Intel RAPL math (tested); PawnIO backend + elevated helper + explicit setup;
   TDP presets with detected range; **Smart Power Control** (tested policy mapping one slider to the
   supported settings, runtime estimate from real telemetry, battery/AC variants).
2. **Phase 3 – Quick Access Menu:** tabs Quick Settings / Performance / Battery / Controller / System;
   power actions (sleep, hibernate, restart, shutdown, restart Lumen, Task Manager, desktop);
   resolution + HDR; mic mute; performance overlay modes; FPS telemetry (3b).
3. **Phase 4 – Game integration:** profiles gain TDP + Smart Power + battery/AC variants;
   launcher/child-process aware session end (already process-based); search, collections, hide,
   launch arguments; EA / Ubisoft / Battle.net discovery.
4. **Phase 5 – Console experience:** instant resume (fast controller re-acquire, no presenter
   flicker, profile re-apply, session restore), session lock, display/dock changes, UI sounds,
   haptics toggle, reduced motion setting, desktop controller (mouse) mode.
5. **Phase 6 – Premium:** advisor (rule-based recommendations from telemetry, opt-in), activity
   statistics, themes, Moonlight/Steam Link launchers.
6. **Phase 7 – Validation** on the device using `docs/TESTING.md`.

Nothing in this plan replaces `explorer.exe`, edits Winlogon shell values, disables Windows security,
or injects into games.
