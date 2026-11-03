# Lumen – a console-style shell for Windows handhelds

Lumen turns a Windows 11 handheld (built and tuned for the **OneXPlayer 3**) into something that
feels like a games console: power on, sign in automatically if you choose to, and land in a
fullscreen, controller-first library of all your games – Steam, Epic, GOG, Xbox/PC Game Pass and
your own folders. The normal Windows desktop is always one button away.

Built with C# / .NET 10, WinUI 3 (Windows App SDK), XInput and SQLite.

## Safety first

- Does **not** replace `explorer.exe` or edit the Winlogon shell setting.
- Does **not** store or write your password, and never touches BitLocker, Defender, UAC or Windows Hello.
- Every console-mode step is explicit and reversible – in the app, in Task Manager, with
  **Lumen Recovery**, or with the plain `RestoreWindows.cmd`.
- Crash-loop protection drops into a windowed **safe mode** instead of trapping you.
- Hardware controls use documented Windows APIs only; TDP and fan control are intentionally not
  implemented (no documented interface exists for the OneXPlayer 3).

## Quick start

```powershell
winget install Microsoft.DotNet.SDK.10
./scripts/build.ps1                     # tests + build
./scripts/publish.ps1                   # self-contained build + installer (needs Inno Setup 6)
```

| Doc | |
|---|---|
| [docs/BUILD-RUN-INSTALL.md](docs/BUILD-RUN-INSTALL.md) | build, run, install on the OneXPlayer 3 |
| [docs/CONSOLE-MODE.md](docs/CONSOLE-MODE.md) | auto start, auto sign-in, turning it all off |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | projects, flows, controller mapping |
| [docs/HARDWARE.md](docs/HARDWARE.md) | hardware abstraction layer, what is (not) supported |
| [docs/PACKAGING.md](docs/PACKAGING.md) | installer, MSIX, signing, limitations |
| [docs/TESTING.md](docs/TESTING.md) | automated tests + on-device checklist |
| [docs/ROADMAP.md](docs/ROADMAP.md) | what's done, what's next |

## What is implemented and what is verified

See [docs/FEATURE-STATUS.md](docs/FEATURE-STATUS.md). CI builds every commit, but **no feature counts
as working on the OneXPlayer 3 until it has passed the device procedures in
[docs/TESTING.md](docs/TESTING.md).**

## Controller cheat sheet

| | |
|---|---|
| D-pad / left stick | move |
| A / B | select / back |
| Y | favorite |
| LB / RB | tabs |
| LT / RT | jump shelf / filter |
| right stick | scroll |
| ≡ | quick access |
| ⧉ + ≡ | quick access from anywhere (also in games) |
| ⧉ + ≡ + LB + RB (hold 3 s) | emergency: show the Windows desktop |
| ⧉ + Y | screenshot (Xbox Game Bar) – also in games |
| ⧉ + X | cycle the performance overlay – also in games |
| Ctrl + Alt + Home | bring Lumen back |
