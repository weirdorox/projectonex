@echo off
rem ---------------------------------------------------------------------------
rem  Lumen - emergency restore. Plain batch so it works even if .NET or the app
rem  itself is broken. Safe to run any number of times. Changes per-user state only.
rem ---------------------------------------------------------------------------
echo Restoring normal Windows startup...

taskkill /IM Lumen.exe /F >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v Lumen /f >nul 2>&1

if not exist "%LOCALAPPDATA%\Lumen" mkdir "%LOCALAPPDATA%\Lumen"
echo requested by RestoreWindows.cmd > "%LOCALAPPDATA%\Lumen\recovery.flag"

tasklist /FI "IMAGENAME eq explorer.exe" | find /I "explorer.exe" >nul || start "" explorer.exe

echo.
echo Done:
echo   - Lumen was closed
echo   - Lumen will no longer start when you sign in
echo   - Next time you open Lumen it starts in safe mode (windowed)
echo.
echo Automatic sign-in was NOT changed. To turn it off, run "Lumen Recovery"
echo as administrator, or use Sysinternals Autologon / netplwiz.
echo.
pause
