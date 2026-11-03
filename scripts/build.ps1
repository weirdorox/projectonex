<#
.SYNOPSIS
  Restore, test and build Lumen.

.EXAMPLE
  ./scripts/build.ps1                 # Debug build + tests
  ./scripts/build.ps1 -Configuration Release
  ./scripts/build.ps1 -SkipTests
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [ValidateSet('x64', 'ARM64')] [string] $Platform = 'x64',
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host "== dotnet --info ==" -ForegroundColor Cyan
    dotnet --version
    if ($LASTEXITCODE -ne 0) { throw ".NET SDK not found. Install the .NET 10 SDK: https://dotnet.microsoft.com/download" }

    if (-not $SkipTests) {
        Write-Host "== Unit tests ==" -ForegroundColor Cyan
        dotnet test tests/Lumen.Core.Tests/Lumen.Core.Tests.csproj -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Core tests failed" }
        dotnet test tests/Lumen.Data.Tests/Lumen.Data.Tests.csproj -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Data tests failed" }
    }

    Write-Host "== Recovery tool ==" -ForegroundColor Cyan
    dotnet build src/Lumen.Recovery/Lumen.Recovery.csproj -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Recovery build failed" }

    Write-Host "== WinUI app ($Platform) ==" -ForegroundColor Cyan
    dotnet build src/Lumen.App/Lumen.App.csproj -c $Configuration -p:Platform=$Platform
    if ($LASTEXITCODE -ne 0) { throw "App build failed" }

    $rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    $exe = Get-ChildItem -Path "src/Lumen.App/bin" -Recurse -Filter Lumen.exe |
        Where-Object { $_.FullName -match [regex]::Escape($Configuration) -and $_.FullName -match $rid } |
        Select-Object -First 1
    if ($exe) {
        Write-Host ""
        Write-Host "Built: $($exe.FullName)" -ForegroundColor Green
        Write-Host "Run windowed:  & '$($exe.FullName)' --windowed"
    }
}
finally {
    Pop-Location
}
