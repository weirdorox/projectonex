# Testing

## Automated (any OS)

```powershell
dotnet test tests/Lumen.Core.Tests
dotnet test tests/Lumen.Data.Tests
```

| Area | Covered |
|---|---|
| VDF parser | modern + legacy `libraryfolders.vdf`, escapes, comments, conditionals, truncated input |
| Steam | multiple libraries, StateFlags filtering, redistributable exclusion, LastPlayed, flat + nested artwork cache, offline mode |
| Epic | manifest parsing, launch URI, DLC / incomplete / non-game skipping, corrupt files |
| GOG | registry parsing (fake registry), DLC skipping, working directory |
| Folders | main-exe heuristics, installer/redist exclusion, title prettifying, cover.jpg |
| Library merge | add, update preserving user data, mark-missing (not delete), failing scanner isolation, manual entries, reinstall, shelf ordering |
| Input mapper | press edges, prime-after-reset, repeat + acceleration timing, stick hysteresis, D-pad priority, View/Menu release semantics, quick-access chord, emergency hold, triggers, disconnect, right-stick scroll, detachable-controller merge |
| Device profiles | OneXPlayer 3 / family / generic resolution, no profile permits TDP/fan, composer vetoes |
| Power policy | plugged/battery/low/saver |
| Crash guard | clean exits, 3-crash safe mode, window expiry, stability reset, recovery flag, corrupt state |
| SQLite | idempotent migrations (now v3), round-trip, upsert, launch/playtime/favorite/hidden, settings persistence, LibraryService on SQLite, collections + membership cascade, play sessions |
| Search | prefix / word / acronym / accent-insensitive / subsequence ranking, hidden + uninstalled rules |
| Collections & stats | name cleaning, membership, play-stats aggregation (blips ignored, time-weighted measured FPS, battery %/h, 14-day series) |
| Ubisoft / EA / Battle.net | registry-based discovery with fake registry + temp folders, launcher exclusion |
| Display | resolution choice (aspect, cap, native/current kept), dock detection and target display |
| Theme | every accent readable on both backgrounds (WCAG contrast), OLED black |
| FPS telemetry | PresentMon CSV (current, legacy v2, noise), rolling window, 1 % low, displayed FPS, overlay content per mode, "never a number when not measured" |
| Advisor | refresh, headroom, stutter, GPU/CPU-bound, heat, firmware override, low battery, no data |
| Mouse mode | deadzone/curve, fractional accumulation, precision, button edges, modifier suppression, whole-notch scrolling |
| Shortcuts | View+Y / View+X chords suppress View and the face-button action |

Windows-only code (XInput, Core Audio, WMI, CCD/HDR, PresentMon, SendInput, WinUI) is covered by the
manual checklists below.

> **A green CI run proves that Lumen compiles, packages and passes its unit tests. It does not prove
> that anything works on the OneXPlayer 3.** Hardware-facing features stay "not device-tested" in
> [FEATURE-STATUS.md](FEATURE-STATUS.md) until the procedures below have been run on the device.

## Manual checklist on the OneXPlayer 3

Run each item; note pass/fail and attach `%LOCALAPPDATA%\Lumen\logs` for failures.

**Startup & library**
- [ ] `Lumen.exe --windowed` opens in a maximised window within ~2 s; log shows `[startup +… ms]` phases
- [ ] Steam / Epic / GOG / Xbox games appear (toast "Found N new games"); Steam tiles show artwork
- [ ] Add a folder in Settings → games from sub-folders appear; `cover.jpg` is used
- [ ] Uninstall a game → after rescan it disappears from shelves; reinstall → it returns with its favorite flag

**Controller**
- [ ] D-pad and left stick move focus; holding repeats and accelerates; ember focus ring always visible
- [ ] A launches (Steam/Epic/GOG/Xbox/folder game each once); "Jump back in" updates afterwards
- [ ] Y toggles favorite (star badge, Favorites shelf); LB/RB switch tabs; LT/RT jump shelves / change filter
- [ ] Right stick scrolls; B returns to Home
- [ ] Detach both controllers / reattach: "Controller disconnected/connected" toast, navigation continues
- [ ] Holding a button while a game closes does not trigger anything in Lumen

**Overlay**
- [ ] ≡ in Lumen and View+Menu in a borderless game open the quick-access panel on the right
- [ ] Volume and mute work; brightness works (or section hidden); refresh-rate buttons list 60…144 Hz and switching works
- [ ] CPU/GPU/RAM update every 1–2 s while open; temperature says "approximate" or "not available"
- [ ] TDP/fan explanation text is shown; B or tapping outside closes the panel

**Desktop & recovery**
- [ ] "Desktop" button → Windows desktop with taskbar; Ctrl+Alt+Home → back to fullscreen Lumen
- [ ] Hold View+Menu+LB+RB 3 s (in Lumen and in a game) → desktop
- [ ] Kill `explorer.exe` in Task Manager, press Desktop → explorer restarts
- [ ] Settings → Start Lumen when I sign in → sign out/in → Lumen starts fullscreen
- [ ] Task Manager → Startup apps → disable Lumen → Settings shows "turned off in Task Manager"
- [ ] `RestoreWindows.cmd` → autostart removed, next Lumen start is safe mode
- [ ] Lumen Recovery → option 2 (UAC) → Settings shows automatic sign-in off
- [ ] Kill Lumen 3× within a minute (Task Manager) → 4th start shows the safe-mode page

**Power**
- [ ] Sleep (power button) and wake: Lumen responds to the controller immediately, clock/battery update
- [ ] Unplug charger: battery icon changes; at ≤ 20 % focus animations stop
- [ ] Lumen idle in background while a game runs: < 1 % CPU in Task Manager

**v0.2 – cinematic UI, overlay, profiles, power**
- [ ] Moving between tiles cross-fades the backdrop smoothly; fast D-pad scrolling stays fluid; logos replace titles for Steam games
- [ ] Turning off Windows "Animation effects" (or ≤ 20 % battery) disables the motion
- [ ] Launching shows the launch curtain; it disappears when the game window appears (B dismisses it)
- [ ] Quick access tabs (LB/RB): Quick, Power, Battery, Game, System
- [ ] In a running game: Game tab recognises it (also when started outside Lumen); "Close game" asks politely, "Force close" only after
- [ ] Wi-Fi / Bluetooth toggles really switch the radios (or are hidden with an explanation)
- [ ] X on a tile → details; set refresh rate / plan / brightness / TDP; start the game: settings applied after ~2 s; close it: originals back
- [ ] Kill Lumen while a profile is active, start Lumen: original settings restored
- [ ] Settings → Performance: install PawnIO + IntelMSR.bin, enable helper (one UAC prompt) → status shows CPU, range, lock state
- [ ] Power tab: TDP slider/presets change "Limit read back" and the measured package power under load; presets outside the range are disabled with a reason
- [ ] Smart Power: preview lists exactly what will change; Apply reports applied/skipped; estimate appears on battery
- [ ] Battery tab: discharge watts, capacity, health, estimate
- [ ] System tab: Sleep (screen off, wakes back into Lumen), Hibernate (if enabled), Restart/Shut down need two presses, Restart Lumen, Task Manager
- [ ] Wake from sleep: controller responds within ~1 s; log shows "Resume: input ready … first frame …"; no fullscreen flicker; TDP/profile still applied
- [ ] Lock (Win+L) and unlock: controller works immediately
- [ ] Restart Lumen / reboot: same tab, same game focused

**Touch & scaling**
- [ ] Tap tiles to launch, swipe shelves/grid, tap toggles/sliders – all hit targets ≥ 48 epx
- [ ] Check at 125 %, 150 %, 175 % scaling: no clipped text, top bar fits

---

## OneXPlayer 3 device test procedures (v0.3)

Before starting: Settings › Diagnostics › **Run diagnostics** and save the text; after a session,
**Export logs (zip)** and keep the file with your results. Record every result in a copy of
[FEATURE-STATUS.md](FEATURE-STATUS.md) ("Device-tested" column).

### A. TDP – requested vs. read back vs. measured vs. firmware

Setup: install PawnIO, put `IntelMSR.bin` in `%LOCALAPPDATA%\Lumen\pawnio\`, Settings › Performance ›
enable the helper (one UAC prompt). Use a steady load (a game's benchmark or a fixed scene) on AC.

1. Power tab shows CPU name, range and its source, lock state. **Fill in:** min / max W, "range source".
   If it says *locked*, stop here: TDP is firmware-locked on this unit – record it, nothing else applies.
2. For each preset (Eco 10, Balanced 20, Performance 28, Turbo 35) and the firmware default, record:

   | Requested | PL1 read back | PL2 read back | Measured package W (after 30 s load) | Firmware override shown? | Diagnostics "Note" |
   |---|---|---|---|---|---|
   | default | | | | | |
   | 10 W | | | | | |
   | 20 W | | | | | |
   | 28 W | | | | | |
   | 35 W (only if inside the range) | | | | | |

   Pass: read-back equals the request, measured package power settles at or below it (it may stay
   lower if the game does not need more), presets outside the range are disabled with a reason.
3. **Never forced repeatedly:** with a request active, open OneXConsole (or the OEM tool) and change
   its mode. Within ~1 min Lumen must show "Firmware override" and the helper log must show at most
   **3** `PL1 drifted … re-applied` lines followed by `Firmware keeps resetting PL1 … no longer
   re-applying` – no further writes after that.
4. Close Lumen normally → read back returns to the firmware default (check with Diagnostics after
   restart). Kill the helper in Task Manager → Lumen reports the helper unavailable, then restarts it.
5. Leave Lumen idle > 2 min with a request active and the shell closed → helper restores firmware limits.
6. Battery: unplug, verify the same request is kept (or Smart Power auto switches as configured).
7. Thermal protections: under Turbo, temperature must never exceed the CPU's own limit (Diagnostics
   "Package temperature"); Lumen never touches thermal registers.

### B. FPS telemetry and overlay

1. Without the helper: overlay modes work, FPS shows "—" with "FPS needs the Lumen helper". No number.
2. With the helper, borderless game: Minimal shows FPS within ~2 s; compare with the game's own FPS
   counter or Intel's overlay (± 5 %). Note game, API (DX11/12/Vulkan) and present mode (Advanced).
3. Advanced: frame-time graph moves, 1 % low drops when you force a stutter (alt-tab, loading).
4. Pause/minimize the game → "No new frames"; resume → numbers return. Close the game → "No game running".
5. Exclusive-fullscreen game: record whether the overlay is visible (expected: not visible – Windows
   limitation). Record whether FPS is still measured in the play history afterwards.
6. Anti-cheat title (e.g. one using EAC/BattlEye): play 10 min with the overlay on – no warnings or
   kicks expected (PresentMon only reads ETW events, nothing is injected). Record title and result.
7. Overlay off → Task Manager shows no `PresentMon.exe` within ~6 s.
8. Click-through: tap on the overlay area in a game – the game receives the tap. If the overlay is
   invisible, set `overlay.clickThrough` off (compatibility) and report it.
9. Play ≥ 10 min with the overlay on, close the game → game details show "Avg FPS" for that session.

### C. Sleep / resume

Repeat 5× each: power button sleep, QAM › Sleep, lid/screen-off timeout; on AC and on battery.

- [ ] Controller works within ~1 s of the screen turning on (log: `Resume: input ready … first frame …`)
- [ ] Detachable controllers re-attached while asleep are found (toast "Controller connected")
- [ ] Settings restored: running game's profile (refresh, plan, brightness, TDP read back) still applied
- [ ] Focus: same tab and focused tile as before sleep; no fullscreen flicker; QAM closed
- [ ] **No duplicate instances**: Task Manager shows one `Lumen.exe` (plus at most one `Lumen.PowerHelper.exe`); starting Lumen again from the Start menu brings the existing window forward
- [ ] Docked during sleep → undocked on wake (and vice versa): Lumen ends up on the right display
- [ ] Overlay comes back over the game after wake (if it was on)

### D. Display, HDR, docked mode

- [ ] Resolution: pick a lower one → "Keep? Going back in 15 s"; let it time out → original restored; pick again + Keep → persists after reboot
- [ ] Refresh-rate list matches the panel (and the new resolution)
- [ ] HDR toggle appears only when supported; switching matches Windows Settings › Display › HDR; on battery note whether Windows blocks it
- [ ] Connect a USB-C dock/monitor/TV: toast "Docked · <name>", Lumen moves to the external display (setting on); disconnect → back to the built-in panel; with the setting off nothing moves
- [ ] Projection "Second screen only" (Win+P): state "External display only"

### E. Audio

- [ ] Mic mute toggle mutes the default microphone (check in Windows Sound settings and a voice app)
- [ ] Audio output list shows speakers + headphones/Bluetooth/HDMI; switching changes the Windows default; if it fails, Sound settings open
- [ ] Volume slider follows the new device

### F. Library extras

- [ ] Search tab: OSK typing with A, X backspace, Y space, RT to results, A launches, X opens details
- [ ] Ubisoft Connect, EA app and Battle.net games are found (record titles) and launch
- [ ] Hide a game (details) → gone from Home/Library/Search; Library › Hidden → Unhide
- [ ] Create a collection (OSK), add games from details, pin it to Home (Settings › Personalization), delete it (two presses)
- [ ] Play 2+ sessions → details show total, sessions, 14-day bars, battery drain on battery, CPU W when helper on

### G. Input, capture, personalization

- [ ] View+Y in a game → screenshot appears in Videos › Captures (Game Bar enabled); with Game Bar captures off → explanation toast
- [ ] QAM › Record starts/stops a Game Bar recording
- [ ] QAM › Keyboard opens the touch keyboard over a game/launcher (or osk.exe as fallback)
- [ ] Mouse mode: on → desktop cursor moves smoothly, A/B click, right stick scrolls, RT precision; inside Lumen the controller navigates; starting a game turns it off
- [ ] Accent colours / OLED black apply instantly across Lumen and the QAM; interface sounds on/off
- [ ] Home rows can be hidden; a pinned collection row appears
- [ ] Advisor (Power tab) shows tips matching what you see (e.g. 60 Hz tip at low FPS on battery)

### H. Steam account, artwork, brightness, HDR, Wi-Fi

- [ ] Settings › Steam account › Sign in through Steam: Steam's page opens over Lumen; after login it closes and shows "Signed in"
- [ ] Get API key: register with domain "localhost" → "Use this key" appears → library loads (owned count shown)
- [ ] Library › Not installed lists owned games; A opens Steam's install dialog; after installing it moves to the normal library
- [ ] Private "Game details" → clear message; Sign out → Not installed list empties
- [ ] New Steam games (e.g. Phantom Blade Zero) show cover and hero art after a scan
- [ ] Brightness slider changes an external monitor (DDC/CI on) and the built-in panel; unsupported → disabled with explanation
- [ ] HDR toggle visible; switching matches Windows Settings; on a non-HDR display it is disabled with the reason
- [ ] QAM › System › Wi-Fi networks lists networks; connect to a saved one; enter a password with the on-screen keyboard (symbols via #+=); wrong password says so; tapping the connected network disconnects

### I. Global Quick settings button (OneXPlayer 3)

- [ ] Settings › Quick settings button › Detect, press the OneXPlayer button(s): record what is listed (type, device, codes) - or "no input received"
- [ ] Use it; from the desktop, Notepad, Steam and a borderless game: press → panel slides in on the right, the game keeps focus (keeps running, no minimise, no resolution change, taskbar does not appear), press again → gone
- [ ] Dashboard stays hidden/minimised throughout
- [ ] Controller in the open panel: D-pad/stick move, A selects, B closes, LB/RB switch tabs; note that the game also sees these presses
- [ ] Hammer the button 10× fast: one panel, no flicker, ends open or closed consistently
- [ ] Sleep/resume, then press again: works without restarting Lumen
- [ ] Ctrl+Alt+Q (fallback) works the same; change it via Detect › Use as keyboard shortcut
- [ ] Exclusive-fullscreen game: record whether the panel is visible (Windows may not show it)
- [ ] If OneXConsole is running: record whether it intercepts the button

### J. Updates (end-to-end - requires a public repo and the LUMEN_UPDATE_KEY secret)

- [ ] Install release N; publish N+1 → Check shows N+1 with notes, date, size
- [ ] Download shows progress; disconnect Wi-Fi mid-way → resumes after reconnect
- [ ] Install and restart → Lumen closes, installer runs silently, Lumen starts as N+1 with the same settings, library, favorites, profiles, theme
- [ ] With a game running: Install is disabled with the reason
- [ ] Beta channel sees N+1-beta.1; Stable does not
- [ ] Tamper test: edit a byte of the downloaded `%LOCALAPPDATA%\Lumen\updates\*.partial` before it finishes → rejected
- [ ] Rollback from Settings (or safe mode) returns to N
- [ ] Offline / rate limited → clear message, no crash
