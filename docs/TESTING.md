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
| SQLite | idempotent migrations, round-trip, upsert, launch/playtime/favorite/hidden, settings persistence, LibraryService on SQLite |

Windows-only code (XInput, Core Audio, WMI, WinUI) is covered by the manual checklist below.

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

**Touch & scaling**
- [ ] Tap tiles to launch, swipe shelves/grid, tap toggles/sliders – all hit targets ≥ 48 epx
- [ ] Check at 125 %, 150 %, 175 % scaling: no clipped text, top bar fits
