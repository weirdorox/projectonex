# Lumen v0.2 – implementation plan

Branch: `claude/v0.2`. Each feature lands as its own commit(s) and must pass CI (Linux tests +
Windows build/publish/installer) before the next one starts. Existing behaviour, controller
mapping, architecture layers and visual identity (ink + ember) are preserved.

## Codebase inspection – relevant findings

| Area | Current state (v0.1) | Gap for v0.2 |
|---|---|---|
| Home hero | One `Image`; source swapped instantly on every tile focus | No transition; decodes on every D-pad step (stutter when scrolling fast) |
| Artwork | Local Steam cache or Steam CDN URL loaded directly by `BitmapImage` | Remote art re-downloaded each session; no logo/colour use |
| Page changes | `SuppressNavigationTransitionInfo` | No motion between tabs |
| Launch | Toast "Starting…" | No launch moment |
| Overlay | `QuickAccessWindow`: one scrolling panel (volume, brightness, refresh, power plan, stats) | No sections, no knowledge of the running game, no radios / Windows settings |
| Hardware | `HardwareServices` + composer (documented APIs only) | Nothing remembers settings per game |
| Data | SQLite schema v1 (`games`, `settings`) | Needs a profiles table (migration v2) |
| Resume | Display-off pauses polling; resume resets mapper + `Rescan()` | Disconnected pads re-probed only every 2 s; `ApplyPresenter()` always re-sets the presenter (visible flicker); no re-apply of display settings; no "where you left off" across restarts |

## Feature 1 – Cinematic game selection
- **ArtworkCache** (Core, tested): disk cache for remote art (`%LOCALAPPDATA%\Lumen\artwork`),
  stable hashed file names, size-capped, atomic writes, offline-safe. App resolves art through it.
- **HeroStage** control: two stacked image layers cross-fading via composition `OpacityTransition`
  (runs on the compositor thread → 60 fps independent of UI-thread work), slow "Ken Burns" scale
  drift, Steam logo art when available (fallback: title text), dominant-colour tint gradient.
- **Focus debounce** (Core `Debouncer` logic tested): hero updates 140 ms after focus settles, so
  fast D-pad scrolling never decodes intermediate images.
- **ArtworkPalette** (Core, tested): dominant/vibrant colour from a 24×24 thumbnail (decoded off the
  UI thread with `Windows.Graphics.Imaging`).
- **Tab transitions**: horizontal slide in the direction of LB/RB; **launch overlay**: hero art zoom
  + title + progress, auto-dismissed when the game takes focus (timeout 20 s).
- **Tiles**: focused tile lifts (Translation Z + shared `ThemeShadow`).
- All motion disabled automatically on low battery (existing `PowerPolicy`) and when Windows
  "Animation effects" is off (`UISettings.AnimationsEnabled`).

## Feature 2 – Universal quick-access overlay
- **Sections** (LB/RB inside the overlay): *Quick* (existing controls), *Game* (current game,
  performance profile, close game), *System* (Wi-Fi/Bluetooth radios, Lumen settings toggles,
  Windows settings shortcuts that open over Lumen, never via the desktop).
- **ForegroundGameDetector**: foreground window → process path (documented
  `QueryFullProcessImageName`) → library match by longest install-path prefix (`GameMatcher`, Core,
  tested). Works for every store, not only games launched from Lumen.
- **Close game**: polite `CloseMainWindow`, then an explicit second "Force close" step.
- **Radios**: `Windows.Devices.Radios`; hidden when access is denied / no radio.

## Feature 3 – Per-game performance profiles
- `PerformanceProfile` (refresh rate, power plan, brightness; TDP/fan fields exist but only apply
  when a verified provider exists – none ships).
- SQLite migration v2: `game_profiles` table; repository + tests.
- **ProfileEngine** (Core, tested): captures a *baseline* before applying, applies only supported
  fields, restores the baseline when the game process exits, ignores brief focus changes (overlay,
  Alt+Tab), re-applies after resume, reports per-field results (applied / unsupported / failed).
- **GameProcessWatcher**: 1 Hz (0.2 Hz on low battery) foreground check while Lumen is in the
  background; process exit detection via handle wait.
- UI: Game details (X on a tile) with profile editor; overlay *Game* section "Save current settings
  for <game>" and live status.

## Feature 4 – Instant-feeling resume
- **ResumeCoordinator** (Core, tested): ordered resume steps with budgets: input first, then
  clock/battery, presenter check, profile re-apply, and deferred work (stats, library) later.
- **Controller fast re-acquire** (`ReconnectPolicy`, tested): probe empty XInput slots every 250 ms
  for 15 s after wake/display-on, then back to 2 s.
- **No flicker**: presenter changed only when its kind differs; transient UI (toast, launch overlay)
  cleared on suspend so the first frame after wake is clean.
- **Where you left off**: last tab + last focused game persisted; restored after restart/crash.
- **Timing log**: resume → input-ready and → first rendered frame, logged for verification.

## Cross-cutting
- SQLite advisory GHSA-2m69-gcr7-jv3q (bundled SQLite < 3.50.2, no patched NuGet build yet):
  exploitable only with attacker-controlled SQL; Lumen runs only its own parameterised queries on a
  local file → documented suppression, revisit when a patched package ships.
- Tests: every new Core component gets unit tests; Data gets migration v1→v2 tests.
