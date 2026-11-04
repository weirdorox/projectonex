<#
.SYNOPSIS
  Produces a self-contained, installable build of Lumen and (optionally) the Inno Setup installer.

.DESCRIPTION
  Output:
    artifacts/publish/Lumen/          everything the installer ships (Lumen.exe, Lumen.Recovery.exe, RestoreWindows.cmd, ...)
    artifacts/installer/*.exe         if Inno Setup 6 (iscc.exe) is installed

  Code signing (recommended for a smooth SmartScreen experience):
    ./scripts/publish.ps1 -CertificateThumbprint <thumbprint>
  signs Lumen.exe, Lumen.Recovery.exe and the installer with signtool from the Windows SDK.

.EXAMPLE
  ./scripts/publish.ps1
  ./scripts/publish.ps1 -Platform ARM64 -NoInstaller
#>
param(
    [ValidateSet('x64', 'ARM64')] [string] $Platform = 'x64',
    [string] $CertificateThumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [switch] $NoInstaller,
    # Intel PresentMon console exe (MIT) for real FPS telemetry; optional. CI sets LUMEN_PRESENTMON.
    [string] $PresentMon = $env:LUMEN_PRESENTMON,
    # Release version (e.g. 0.4.0 or 0.4.0-beta.1). Default: <Version> from Directory.Build.props.
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$out = Join-Path $root 'artifacts/publish/Lumen'
$versionArgs = @()
if ($Version) { $versionArgs = @("-p:Version=$Version") }

Push-Location $root
try {
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    Write-Host "== Publishing Lumen ($rid, self-contained) ==" -ForegroundColor Cyan
    dotnet publish src/Lumen.App/Lumen.App.csproj -c Release -r $rid -p:Platform=$Platform @versionArgs `
        --self-contained true -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=true -o $out
    if ($LASTEXITCODE -ne 0) { throw "App publish failed" }

    Write-Host "== Publishing Lumen.PowerHelper ==" -ForegroundColor Cyan
    dotnet publish src/Lumen.PowerHelper/Lumen.PowerHelper.csproj -c Release -r $rid --self-contained true @versionArgs -o $out
    if ($LASTEXITCODE -ne 0) { throw "PowerHelper publish failed" }

    if ($PresentMon -and (Test-Path $PresentMon)) {
        $tools = Join-Path $out 'tools'
        New-Item -ItemType Directory -Force $tools | Out-Null
        Copy-Item $PresentMon (Join-Path $tools 'PresentMon.exe') -Force
        $license = Join-Path (Split-Path -Parent $PresentMon) 'LICENSE.txt'
        if (Test-Path $license) { Copy-Item $license (Join-Path $tools 'PresentMon-LICENSE.txt') -Force }
        Write-Host "Included PresentMon from $PresentMon" -ForegroundColor Cyan
    }
    else {
        Write-Warning "PresentMon not provided - FPS telemetry will report 'not included in this build'."
    }

    Write-Host "== Publishing Lumen.Recovery ==" -ForegroundColor Cyan
    dotnet publish src/Lumen.Recovery/Lumen.Recovery.csproj -c Release -r $rid --self-contained true @versionArgs -o $out
    if ($LASTEXITCODE -ne 0) { throw "Recovery publish failed" }

    if ($CertificateThumbprint) {
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $signtool) { throw "signtool.exe not found - install the Windows SDK." }
        foreach ($f in @("$out/Lumen.exe", "$out/Lumen.Recovery.exe", "$out/Lumen.PowerHelper.exe")) {
            & $signtool.FullName sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $f
            if ($LASTEXITCODE -ne 0) { throw "Signing $f failed" }
        }
    }

    Write-Host "Published to $out" -ForegroundColor Green

    if (-not $NoInstaller) {
        $iscc = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1

        if (-not $iscc) {
            Write-Warning "Inno Setup 6 not found - skipping installer. Install it (winget install JRSoftware.InnoSetup) and re-run."
        }
        else {
            Write-Host "== Building installer ==" -ForegroundColor Cyan
            $version = if ($Version) { $Version } else { ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Select-Object -First 1 }
            & $iscc "/DAppVersion=$version" "/DSourceDir=$out" installer/inno/Lumen.iss
            if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }

            if ($CertificateThumbprint) {
                Get-ChildItem artifacts/installer -Filter *.exe | ForEach-Object {
                    & $signtool.FullName sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $_.FullName
                }
            }
            Write-Host "Installer: $(Resolve-Path artifacts/installer)" -ForegroundColor Green
        }
    }
}
finally {
    Pop-Location
}
