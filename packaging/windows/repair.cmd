@echo off
:: VPNRouter Self-Repair Tool — v2.31.8-r8
::
:: Hosted at: https://vpn.ninitux.com/repair.cmd
::
:: Use cases:
::   - VPNRouter.App.exe crashes at launch (mixed-version DLLs)
::   - "Last update did not take effect" banner persists
::   - Service stuck or won't start
::   - Any state where the in-app Update button doesn't recover
::
:: User flow:
::   1. Download repair.cmd to Desktop
::   2. Right-click → "Run as administrator"
::   3. Wait ~60 seconds — script does everything
::
:: What it does:
::   1. Disables Service failure recovery (so SCM can't auto-restart
::      Service mid-update, locking DLLs).
::   2. Stops Service + kills all VPNRouter processes.
::   3. Downloads latest stable Windows install ZIP from GitHub.
::   4. Extracts over Program Files\VPNRouter (replaces ALL files).
::   5. Restores Service failure recovery to default (3 retries / 60s).
::   6. Starts Service if it was installed.
::   7. Verifies install via VPNRouter.CLI.exe doctor.
::
:: This is what install.ps1 does PLUS the failure-recovery disable
:: that v2.31.8-r7 added to the in-app helper, packaged as a single
:: file users can double-click instead of typing PowerShell commands.

setlocal enableextensions enabledelayedexpansion
title VPNRouter Self-Repair

:: == Self-elevate via UAC if needed ==================================
>nul 2>&1 net session
if %errorlevel% neq 0 (
    echo Need administrator rights. Re-launching with UAC prompt...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b 0
)

echo.
echo === VPNRouter Self-Repair Tool ===
echo.

:: == Disable Service failure recovery ================================
echo [1/7] Disabling Service failure recovery...
sc failure VPNRouter reset= 0 actions= "" >nul 2>&1

:: == Stop Service ====================================================
echo [2/7] Stopping VPNRouter Service...
sc stop VPNRouter >nul 2>&1
:: Wait up to 15 seconds for STOPPED state.
set /a tries=0
:waitstop
sc query VPNRouter | find "STOPPED" >nul && goto stopdone
set /a tries+=1
if %tries% gtr 30 (
    echo   Service did not stop in 15s — proceeding anyway
    goto stopdone
)
ping -n 1 -w 500 127.0.0.1 >nul
goto waitstop
:stopdone

:: == Kill leftover processes =========================================
echo [3/7] Killing leftover processes...
taskkill /F /IM VPNRouter.App.exe >nul 2>&1
taskkill /F /IM VPNRouter.GUI.exe >nul 2>&1
taskkill /F /IM VPNRouter.Service.exe >nul 2>&1
taskkill /F /IM VPNRouter.CLI.exe >nul 2>&1
taskkill /F /IM sing-box.exe >nul 2>&1
ping -n 3 127.0.0.1 >nul

:: == Download latest stable ==========================================
echo [4/7] Downloading latest VPNRouter (this may take 30-60 seconds)...
set "TMPZIP=%TEMP%\vpnr-repair.zip"
del /Q "%TMPZIP%" >nul 2>&1
powershell -ExecutionPolicy Bypass -Command "$ProgressPreference = 'SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; $r = Invoke-RestMethod 'https://api.github.com/repos/PavelLizunov/VPNRouter/releases?per_page=10'; $stable = $r | Where-Object { -not $_.prerelease -and -not $_.draft } | Select-Object -First 1; $asset = $stable.assets | Where-Object { $_.name -like 'VPNRouter-v*-win.zip' -and $_.name -notlike '*update*' } | Select-Object -First 1; if (-not $asset) { exit 1 }; Invoke-WebRequest -Uri $asset.browser_download_url -OutFile '%TMPZIP%' -UseBasicParsing"
if errorlevel 1 (
    echo.
    echo [ERROR] Failed to download VPNRouter. Check internet connection.
    pause
    exit /b 2
)

:: == Extract over install dir ========================================
echo [5/7] Extracting to %ProgramFiles%\VPNRouter...
powershell -ExecutionPolicy Bypass -Command "Expand-Archive -Path '%TMPZIP%' -DestinationPath '%ProgramFiles%\VPNRouter' -Force"
if errorlevel 1 (
    echo.
    echo [ERROR] Extract failed.
    pause
    exit /b 3
)
del /Q "%TMPZIP%" >nul 2>&1

:: == Restore Service failure recovery ================================
echo [6/7] Restoring Service failure recovery...
sc failure VPNRouter reset= 86400 actions= restart/60000/restart/60000/restart/60000 >nul 2>&1

:: == Start Service if installed ======================================
sc query VPNRouter >nul 2>&1
if %errorlevel% equ 0 (
    echo [7/7] Starting VPNRouter Service...
    sc start VPNRouter >nul 2>&1
) else (
    echo [7/7] Service not installed — skipping start
)

:: == Verify ==========================================================
echo.
echo === Verification ===
"%ProgramFiles%\VPNRouter\app\VPNRouter.CLI.exe" doctor 2>nul | findstr /R "Version Service sing-box"

echo.
echo === Repair complete ===
echo You can now launch VPNRouter from the Start Menu.
echo.
pause
