# Feature status (branch `claude/v0.2`)

Legend: **Implemented** = code exists in the app. **CI** = compiles in the Windows build *and* the
logic is covered by unit tests (✅), or compiles only (🟡 – Windows-only code without automated tests).
**Device** = verified on a OneXPlayer 3 with [TESTING.md](TESTING.md). Nothing below has been run on
the device yet – every hardware feature is **not device-tested** until you fill in that column.

| Feature | Implemented | CI | Device-tested | Notes |
|---|---|---|---|---|
| Fullscreen controller shell, Home/Library/Settings, safe mode, recovery tool | ✅ | ✅ | ☐ | v0.1 |
| Steam / Epic / GOG / Xbox / folders | ✅ | ✅ | ☐ | |
| **Steam sign-in + owned library** (uninstalled games, Install via Steam) | ✅ | ✅ (OpenID/API parsing) / 🟡 (WebView) | ☐ | Steam's own login page; needs your free Web API key + public "Game details" |
| **Steam store artwork** for new games (hashed asset paths) | ✅ | ✅ | ☐ | public store service, no account needed |
| **Ubisoft Connect / EA app / Battle.net** discovery | ✅ | ✅ | ☐ | registry based; launch via `uplay://` or the game exe |
| **Universal search** (OSK, every store) | ✅ | ✅ | ☐ | |
| **Collections**, favorites | ✅ | ✅ | ☐ | |
| **Hide / unhide** | ✅ | ✅ | ☐ | Library › Hidden |
| **Play history & per-game stats** | ✅ | ✅ | ☐ | FPS only when measured; CPU W only with the helper |
| Cinematic UI, quick access tabs, per-game profiles | ✅ | ✅ | ☐ | v0.2 |
| TDP (Intel RAPL via PawnIO + elevated helper) | ✅ | ✅ | ☐ | may be **firmware-locked/overridden** on the device – shown as such |
| Smart Power, battery/AC automatic profiles | ✅ | ✅ | ☐ | |
| **FPS / frame-time telemetry** (PresentMon/ETW in helper) | ✅ | ✅ | ☐ | PresentMon 2.6.0 bundled, Intel signature checked before running |
| **Performance overlay** Off/Minimal/Basic/Detailed/Advanced + graph | ✅ | ✅ | ☐ | not visible over exclusive fullscreen (Windows) |
| **Resolution** with keep/revert | ✅ | ✅ (option logic) / 🟡 (switching) | ☐ | |
| **HDR** switch | ✅ | 🟡 | ☐ | CCD API, 24H2 + legacy requests; always shown, disabled with the reason when unsupported |
| **Brightness on external monitors** (DDC/CI) | ✅ | 🟡 | ☐ | plus built-in panel via WMI; monitor must have DDC/CI on |
| **Wi-Fi networks** (scan, connect with password, disconnect) | ✅ | 🟡 | ☐ | Windows needs location access for desktop apps to list networks |
| **Docked mode** (external display detection, move Lumen) | ✅ | ✅ (detection) / 🟡 (moving) | ☐ | Win+P mode is never changed |
| **Mic mute** | ✅ | 🟡 | ☐ | default console + communications mics |
| **Audio output selection** | ✅ | 🟡 | ☐ | undocumented Windows interface; falls back to Sound settings |
| **UI sounds, accent themes, OLED black** | ✅ | ✅ (palette) / 🟡 (apply) | ☐ | |
| **Customizable Home** (rows, pinned collection) | ✅ | 🟡 | ☐ | |
| **On-screen keyboard** (Lumen) / **touch keyboard** (Windows) | ✅ | 🟡 | ☐ | TabTip toggle, osk.exe fallback |
| **Controller mouse mode** | ✅ | ✅ (math) / 🟡 (injection) | ☐ | opt-in, off in games, can't click admin windows (UIPI) |
| **Controller shortcuts** View+Y screenshot, View+X overlay | ✅ | ✅ | ☐ | the game also sees View/Y presses (XInput can't be blocked) |
| **Screenshots / recording** | ✅ | 🟡 | ☐ | Xbox Game Bar (default shortcuts must be unchanged) |
| **Performance advisor** | ✅ | ✅ | ☐ | read-only tips from real readings |
| **Global Quick settings button** (detected HW button, Ctrl+Alt+Q, View+Menu) | ✅ | ✅ (binding logic) / ✅ CI: shortcut opens panel over Notepad without focus change, 5 rapid presses = 1 toggle | ☐ | Raw Input (read-only) + RegisterHotKey; the game still sees controller input |
| **OneX button opens Quick settings** (OneXPlayer vendor HID, automatic) | ✅ | ✅ (frame parsing) | ☐ | ids from Handheld Companion's OXP3 support; confirm with Detect |
| **Quick settings over games without taking focus** | ✅ | ✅ (CI smoke) | ☐ | not over exclusive fullscreen |
| **In-app updates** (signed GitHub releases, stable/beta, resume, rollback) | ✅ | ✅ (rules, signature, journal) | ☐ | needs public repo + LUMEN_UPDATE_KEY; end-to-end update not yet run |
| **Diagnostics page, log export** | ✅ | 🟡 | ☐ | zip in Documents, nothing uploaded |
| Sleep/resume, unlock, session restore | ✅ | ✅ (reconnect policy) / 🟡 | ☐ | |

## Not supported (by design or no documented interface)

| Request | Why |
|---|---|
| Fan control | no documented OneXPlayer 3 interface – never faked |
| FPS **limiter** | would require injecting into games (anti-cheat risk); use the game's or Intel's limiter |
| Overlay over exclusive fullscreen | Windows does not composite normal windows over it |
| OEM buttons (Turbo / keyboard / home) as Lumen shortcuts | not XInput; map them to a keyboard shortcut in the OEM tool |
| Universal game suspend / instant game resume | not possible on Windows |
| AMD TDP | no AMD backend shipped (no hardware to verify) |
| Windows power-mode slider, night light, rotation lock | no public setter – Lumen links to Settings |
