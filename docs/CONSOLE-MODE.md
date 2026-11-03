# Console mode: power on → straight into Lumen

Goal: press power, land in Lumen without touching the desktop – and always be able to get back
to normal Windows.

## What Lumen does and does not do

| | |
|---|---|
| ✅ Adds a **per-user startup entry** (`HKCU\…\Run\Lumen` or the MSIX startup task) | Remove any time: Lumen Settings, Task Manager → Startup apps, Lumen Recovery, uninstall |
| ✅ Runs **fullscreen** over the normal desktop | Desktop button, quick access → Desktop, emergency chord, Ctrl+Alt+Home, Alt+Tab |
| ✅ **Shows** whether automatic sign-in is on and opens Microsoft's tools to change it | |
| ❌ Replace `explorer.exe` or edit the Winlogon `Shell` value | Windows 11 Pro has no Shell Launcher; Lumen doesn't emulate one |
| ❌ Store, read or write your password | Sysinternals Autologon / netplwiz do that, with your explicit action |
| ❌ Touch BitLocker, Defender, UAC, Windows Hello or firewall settings | |

## Step 1 – Start Lumen when you sign in

Lumen → **Settings → Console mode → Start Lumen when I sign in**. Keep **Fullscreen console mode** on.

Sign out and back in: Lumen opens fullscreen. Windows starts Run-key apps a few seconds after the
desktop appears, so you will briefly see the desktop – that is expected in this version
(see ROADMAP Phase 4).

## Step 2 (optional) – Automatic sign-in

Automatic sign-in means **anyone holding the device is signed in as you**. BitLocker still
protects the drive if the device is off and the drive removed; it does not protect a running,
signed-in device. Only enable it if you accept that.

Lumen → Settings → *Automatic sign-in*:

**Option A – Sysinternals Autologon (recommended).** Microsoft's tool; stores the password as an
encrypted LSA secret instead of plain text.
1. *Get Sysinternals Autologon* → download, run `Autologon64.exe` as administrator.
2. Enter user name, domain (the PC name for a local account, or `MicrosoftAccount` for MS accounts –
   see the tool's help) and password → **Enable**.

**Option B – User Accounts (netplwiz).**
1. If *"For improved security, only allow Windows Hello sign-in…"* is on, Lumen shows a warning –
   open **Sign-in options** and turn it off first (otherwise the checkbox is hidden).
2. *User Accounts (netplwiz)* → untick **Users must enter a user name and password** → OK → enter the password.

Lumen's Settings page then shows "Automatic sign-in is ON for …".

Notes:
- With a **Microsoft account + Windows Hello PIN only**, you may need to set/know the account
  password for either tool to work.
- Automatic sign-in does not bypass BitLocker's pre-boot PIN if you configured one.

## Turning it all off again

Any of these – pick whichever is reachable:

| Where | What it does |
|---|---|
| Lumen → Settings → **Restore normal Windows startup** (press twice) | removes autostart, turns off fullscreen |
| Lumen → Settings → **Turn off automatic sign-in…** | runs Lumen Recovery elevated: `AutoAdminLogon=0`, removes any plaintext `DefaultPassword`, deletes the Autologon LSA secret |
| Start → **Lumen Recovery** | menu: restore startup / turn off auto sign-in / both |
| Start → **Restore normal Windows startup** (`RestoreWindows.cmd`) | plain batch file: closes Lumen, removes autostart, safe mode next start, restarts explorer if needed – works even if .NET is broken |
| Task Manager → Startup apps → Lumen → Disable | Windows' own switch (Lumen shows "turned off in Task Manager") |
| Sysinternals Autologon → **Disable** | turns off automatic sign-in |
| Uninstall Lumen | removes autostart |

Command line (e.g. from a keyboard on the handheld, Win+R):

```
"%LOCALAPPDATA%\Programs\Lumen\Lumen.Recovery.exe" --all
```

## If something goes wrong

- **Lumen crashes at start repeatedly** → after 3 unclean exits within 10 minutes Lumen starts in
  **safe mode** (windowed, with Desktop / Stop autostart / Logs buttons).
- **Stuck in a fullscreen game or Lumen** → hold **View + Menu + LB + RB** for 3 s → Lumen leaves
  fullscreen and minimises; or Ctrl+Alt+Del → Task Manager.
- **No desktop/taskbar** → Lumen restarts `explorer.exe` whenever you switch to the desktop; Lumen
  Recovery does the same.
- **Logs** → `%LOCALAPPDATA%\Lumen\logs\lumen-YYYYMMDD.log` (Settings → Open log folder).
