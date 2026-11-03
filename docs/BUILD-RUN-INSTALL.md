# Build, run and install on the OneXPlayer 3

> **Status of this drop:** the code was written without access to a Windows build machine. The
> platform-independent projects and tests are straightforward .NET 10; the WinUI project has not yet
> been compiled. Expect the first build to surface a few small fixes (typically a package version
> or an API signature). The GitHub Actions workflow builds everything on `windows-latest`, which is
> the fastest way to get a compile log.

## 1. Prerequisites (on the OneXPlayer 3 or any Windows 11 PC)

| Tool | Why | Install |
|---|---|---|
| .NET 10 SDK (x64) | build + tests | `winget install Microsoft.DotNet.SDK.10` |
| Visual Studio 2022 17.14+ or 2026 *(optional)* | XAML designer, debugging, MSIX | workload **.NET desktop development** + **Windows App SDK C# templates** |
| Inno Setup 6.3+ *(optional)* | `.exe` installer | `winget install JRSoftware.InnoSetup` |
| Git | clone | `winget install Git.Git` |

Windows App SDK packages are restored from NuGet automatically – nothing to install. The default
build is **self-contained**, so the handheld does not need the Windows App SDK runtime or .NET.

Tip: build on a desktop PC and copy the installer to the handheld; building on the handheld works
too but is slower.

## 2. Build and test

```powershell
git clone <your repo> Lumen
cd Lumen
./scripts/build.ps1            # unit tests + Debug build of Recovery and the app
```

Or in Visual Studio: open `Lumen.sln`, select **Debug | x64**, set **Lumen.App** as startup project,
pick the launch profile **Lumen (windowed)** and press F5.

If restore fails with "version not found" for a package, edit `Directory.Packages.props` and use the
latest stable version from nuget.org (only stable, public APIs are used).

## 3. Run

```powershell
# windowed - best while developing
& src/Lumen.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/Lumen.exe --windowed

# exactly like at sign-in (fullscreen console mode)
& .../Lumen.exe --autostart

# safe mode
& .../Lumen.exe --safe-mode
```
(The exact bin path is printed at the end of `build.ps1`.)

Keyboard/mouse/touch work everywhere; a controller is not required to try it out.
**Ctrl + Alt + Home** brings Lumen back from anywhere. **Alt + Tab** and **Ctrl + Alt + Del** always work.

## 4. Install on the OneXPlayer 3

```powershell
./scripts/publish.ps1                 # -> artifacts/installer/Lumen-Setup-0.1.0-x64.exe
```

1. Copy `Lumen-Setup-…-x64.exe` to the handheld and run it. It installs per-user (no admin prompt)
   to `%LOCALAPPDATA%\Programs\Lumen` and adds Start menu entries:
   **Lumen**, **Lumen (safe mode)**, **Lumen Recovery**, **Restore normal Windows startup**.
2. Leave "Start Lumen when I sign in" unchecked for the first run. Open Lumen, check that your
   games appear and the controller works (see [TESTING.md](TESTING.md)).
3. Then follow [CONSOLE-MODE.md](CONSOLE-MODE.md) to turn on console mode step by step.

Unsigned builds trigger SmartScreen ("Windows protected your PC" → More info → Run anyway).
Signing is covered in [PACKAGING.md](PACKAGING.md).

## 5. Recommended OneXPlayer 3 settings

- **Display scaling:** 150 % (1920×1200 → 1280×800 effective). Tiles are sized for this; 175 % also works.
- **Controller mode:** keep the built-in controller in **XInput/Xbox mode** in OneXConsole. Lumen
  reads standard XInput; keyboard/mouse emulation modes will not drive Lumen's controller navigation.
- **Vendor buttons:** if OneXConsole lets you map a hardware button to a key combination, map one
  to **Ctrl + Alt + Home** for a dedicated "back to Lumen" button.
- **TDP / fan:** keep using OneXConsole. Lumen intentionally does not touch them (see [HARDWARE.md](HARDWARE.md)).
- **Detachable controllers:** attached, detached or paired over Bluetooth – Lumen merges all
  connected XInput controllers, so either half or both drive the UI.

## 6. Uninstall

Settings → Apps → Installed apps → Lumen → Uninstall. The uninstaller removes the autostart entry
and asks whether to delete `%LOCALAPPDATA%\Lumen` (library, settings, logs). Automatic sign-in is a
Windows setting and is **not** changed by uninstalling – turn it off first (Lumen Settings or Lumen
Recovery) if you enabled it.
