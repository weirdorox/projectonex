# Roadmap

Status legend: ✅ implemented (see [FEATURE-STATUS.md](FEATURE-STATUS.md) for device verification) · 🟡 partially · ⏭ planned

## Phase 0 – Foundation ✅
- ✅ Solution architecture, layered projects, central package management
- ✅ Logging with startup phase timing, crash-loop guard, safe mode
- ✅ SQLite schema + migrations, repository, settings
- ✅ Unit tests for all platform-independent logic; CI for Linux + Windows

## Phase 1 – MVP (this drop)
- ✅ Fullscreen dashboard (Home: hero + shelves; Library: filtered grid; Settings)
- ✅ Complete controller navigation (D-pad, sticks, triggers, shoulders, chords, repeat, focus ring)
- ✅ Library discovery: Steam, Epic, GOG, Xbox/Store (GDK), folders
- ✅ Launching (store URIs, executables, packaged apps), favorites, recently played, play count
- ✅ "Desktop" button + emergency chord + hotkey; desktop always recoverable
- ✅ Explicit, reversible "start at sign-in"; auto sign-in *status* + Microsoft tools for setup
- ✅ Recovery tool + `RestoreWindows.cmd`
- ✅ Quick-access overlay window: battery, volume, brightness, refresh rate, power plans, CPU/GPU/RAM, thermal zone
- ✅ Sleep/resume & display on/off handling, controller hot-plug, battery-aware polling
- ✅ Inno Setup installer; MSIX build option

**Exit criteria:** builds on a clean Windows 11 Pro machine with the .NET 10 SDK, all tests pass,
the manual checklist in [TESTING.md](TESTING.md) passes on the OneXPlayer 3.

## Phase 2 – Polish
- ✅ On-screen keyboard (Lumen's own for search/collections; Windows touch keyboard from Quick Access)
- 🟡 Game details page (X): play history & stats, hide, collections, profile ✅ · custom cover picker, launch options ⏭
- ⏭ Manual "Add game" (exe / URI) with controller-friendly file browser
- ⏭ Artwork: Epic/GOG cover retrieval (local Galaxy DB / public store metadata), optional SteamGridDB (user API key), local icon extraction
- ✅ Search (on-screen keyboard) · ⏭ sort options
- ⏭ Localisation (resw), accessibility pass (Narrator, text scaling)
- ⏭ Configurable global hotkey and chords
- ✅ Play time for all stores (foreground process detection) + per-session history

## Phase 3 – Hardware (only verified, documented paths)
- ⏭ Windows.Gaming.Input backend (in addition to XInput) – Guide button where exposed
- ⏭ Windows 11 power mode (Best efficiency / Balanced / Best performance) if a supported public API becomes available
- ✅ Per-game profiles (refresh, plan, brightness, TDP)
- 🟡 TDP through the signed PawnIO driver (Intel RAPL, opt-in elevated helper) ✅ · fan/RGB stay
  unsupported until OneXPlayer documents an interface.
- ✅ Battery health and discharge watts (aggregate battery report)

## Phase 4 – Deeper console mode (opt-in, reversible)
- ⏭ Hide the desktop flash at sign-in (black splash window as early as possible; scheduled task "at logon" instead of the Run key for faster start)
- ⏭ Optional Assigned Access / multi-app kiosk exploration (Windows 11 Pro supports Assigned Access; evaluated separately because it changes the user account experience)
- ⏭ Suspend/resume of games is **not** planned: Windows has no safe, universal mechanism.
