# Packaging and signing

Two supported outputs. The **conventional installer is the recommended default** for a shell-style
app on a single handheld.

## A. Conventional installer (default) – Inno Setup

```powershell
./scripts/publish.ps1                                   # unsigned
./scripts/publish.ps1 -CertificateThumbprint <sha1>     # signed (cert in CurrentUser\My or LocalMachine\My)
```

- Self-contained: .NET 10 runtime and Windows App SDK are bundled (`WindowsAppSDKSelfContained`),
  so the handheld needs no prerequisites. Size ≈ 150–200 MB installed.
- Per-user install, no admin prompt. Uninstall removes the autostart entry.
- ReadyToRun is enabled for faster cold start.

**Signing:** any Authenticode code-signing certificate works with `signtool`. A self-signed
certificate is fine for your own device if you import it into *Trusted Root* and *Trusted
Publishers* – but SmartScreen reputation only comes from a CA-issued certificate (OV/EV) or
Microsoft Trusted Signing.

## B. MSIX

```powershell
# Visual Studio: right-click Lumen.App → Package and Publish → Create App Packages (set LumenPackaging=msix)
# or command line (needs MSBuild from Visual Studio for MSIX packaging):
msbuild src/Lumen.App/Lumen.App.csproj /restore /p:Configuration=Release /p:Platform=x64 `
  /p:LumenPackaging=msix /p:GenerateAppxPackageOnBuild=true `
  /p:AppxPackageSigningEnabled=true /p:PackageCertificateThumbprint=<sha1>
```

Before building: set `Publisher` in `src/Lumen.App/Package.appxmanifest` to your certificate's
subject exactly.

### MSIX limitations (why it is not the default)

| Topic | Effect on Lumen |
|---|---|
| Certificate must be trusted on the device | Self-signed MSIX requires importing the cert to *Trusted People* / *Trusted Root* first |
| Windows App SDK runtime | MSIX builds are framework-dependent; the Windows App SDK runtime package must be installed (VS deploys it; on the handheld install the runtime from Microsoft) |
| Startup | Uses the `windows.startupTask` extension. Windows only lets the **user** enable it (Lumen calls `RequestEnableAsync`); if the user disabled it in Settings, apps cannot re-enable it |
| Command-line arguments at startup | Startup tasks cannot pass `--autostart`; Lumen detects `ExtendedActivationKind.StartupTask` instead |
| File/registry virtualization | Writes to `%LOCALAPPDATA%` may be redirected to the package's private store; Lumen's data/logs then live under `%LOCALAPPDATA%\Packages\Lumen.HandheldShell_…\LocalCache\Local\Lumen`. HKCU writes are virtualized, which is why MSIX uses the startup task rather than the Run key |
| Recovery tool | `Lumen.Recovery.exe` is not included in the MSIX in this version; use `RestoreWindows.cmd` from the unpackaged build, or Settings → Apps → Startup |
| Uninstall | Removing the package removes the startup task automatically |

## Checklist before giving the installer to the handheld

- [ ] `./scripts/build.ps1 -Configuration Release` passes (tests included)
- [ ] `artifacts/publish/Lumen` contains `Lumen.exe`, `Lumen.Recovery.exe`, `RestoreWindows.cmd`, `Assets/`
- [ ] Installer runs on a clean Windows 11 user without admin
- [ ] Manual checklist in [TESTING.md](TESTING.md)
