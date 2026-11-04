# Start-up + UI smoke test of the published build (run by CI on a Windows runner).
# 1. Lumen starts and survives; 2. every tab and Quick settings section opens;
# 3. the keyboard shortcut opens Quick settings over another app without bringing the dashboard
#    forward and without taking focus; repeated presses are debounced; 4. it closes again.
$ErrorActionPreference = 'Stop'
$exe = Resolve-Path artifacts/publish/Lumen/Lumen.exe
$logs = Join-Path $env:LOCALAPPDATA 'Lumen/logs'

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Smoke {
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  public static string ForegroundTitle() { var sb = new StringBuilder(256); GetWindowText(GetForegroundWindow(), sb, 256); return sb.ToString(); }
  public static void CtrlAltQ() {
    keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x51, 0, 0, UIntPtr.Zero);
    keybd_event(0x51, 0, 2, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero);
  }
}
'@

function LogText { (Get-ChildItem $logs | Sort-Object LastWriteTime | Select-Object -Last 1 | Get-Content -Raw) }
function Count($pattern) { ([regex]::Matches((LogText), $pattern)).Count }

$env:LUMEN_SMOKE_UI = '1'
$p = Start-Process $exe -ArgumentList '--windowed' -PassThru
Start-Sleep -Seconds 30
$alive = -not $p.HasExited
Write-Host "Lumen alive after 30 s: $alive"

$hotkey = 'not run'
if ($alive) {
  # Another app in front, as a game would be.
  $other = Start-Process notepad.exe -PassThru
  Start-Sleep -Seconds 3
  [void][Smoke]::SetForegroundWindow($other.MainWindowHandle)
  Start-Sleep -Milliseconds 500
  $before = [Smoke]::ForegroundTitle()
  $opened0 = Count 'toggled by keyboard shortcut -> open'
  [Smoke]::CtrlAltQ(); Start-Sleep -Seconds 2
  $afterOpen = [Smoke]::ForegroundTitle()
  $openedOk = (Count 'toggled by keyboard shortcut -> open') -gt $opened0
  $noDashboard = (LogText) -match 'game keeps focus\) in \d+ ms; dashboard in front: False'
  # Rapid presses: only the first may toggle (closes), the rest are debounced.
  $toggles0 = Count 'toggled by keyboard shortcut'
  for ($i = 0; $i -lt 5; $i++) { [Smoke]::CtrlAltQ(); Start-Sleep -Milliseconds 40 }
  Start-Sleep -Seconds 2
  $rapid = (Count 'toggled by keyboard shortcut') - $toggles0
  $closed = (LogText) -match 'Quick settings closed'
  $afterClose = [Smoke]::ForegroundTitle()
  Write-Host "Foreground before: '$before' | after open: '$afterOpen' | after close: '$afterClose'"
  Write-Host "Opened by shortcut: $openedOk | dashboard stayed hidden: $noDashboard | toggles from 5 rapid presses: $rapid | closed: $closed"
  $hotkey = if ($openedOk -and $noDashboard -and $rapid -eq 1 -and $closed -and $afterOpen -eq $before) { 'pass' } else { 'FAIL' }
  Stop-Process -Id $other.Id -Force -ErrorAction SilentlyContinue
}

if (Test-Path $logs) { Get-ChildItem $logs | Sort-Object LastWriteTime | ForEach-Object { Write-Host "---- $($_.Name)"; Get-Content $_.FullName -Tail 120 } }
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-3) } -ErrorAction SilentlyContinue |
  Where-Object { $_.ProviderName -in 'Application Error', '.NET Runtime', 'Windows Error Reporting' } |
  ForEach-Object { Write-Host "==== $($_.ProviderName) $($_.TimeCreated)"; Write-Host $_.Message }
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }

if (-not $alive) { throw "Lumen exited during start-up" }
$text = LogText
if ($text -match 'Unhandled UI exception|Quick access failed') { throw "UI exception during the smoke test" }
if ($text -notmatch '\[smoke\] UI walk finished') { throw "UI smoke walk did not finish" }
Write-Host "Hotkey/overlay test: $hotkey"
if ($hotkey -ne 'pass') { throw "Quick settings shortcut test failed" }
