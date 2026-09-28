@echo off

setlocal enableextensions enabledelayedexpansion
title VPNRouter Self-Repair

>nul 2>&1 net session
if %errorlevel% neq 0 (
    echo Need administrator rights. Re-launching with UAC prompt...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b 0
)

echo.
echo === VPNRouter Self-Repair Tool ===
echo.

echo [1/7] Disabling Service failure recovery...
sc failure VPNRouter reset= 0 actions= "" >nul 2>&1

echo [2/7] Stopping VPNRouter Service...
sc stop VPNRouter >nul 2>&1
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

echo [3/7] Killing leftover processes...
taskkill /F /IM VPNRouter.App.exe >nul 2>&1
taskkill /F /IM VPNRouter.GUI.exe >nul 2>&1
taskkill /F /IM VPNRouter.Service.exe >nul 2>&1
taskkill /F /IM VPNRouter.CLI.exe >nul 2>&1
taskkill /F /IM sing-box.exe >nul 2>&1
ping -n 3 127.0.0.1 >nul

echo [4/7] Downloading latest VPNRouter + verifying SHA256 (this may take 30-60 seconds)...
set "TMPZIP=%TEMP%\vpnr-repair.zip"
del /Q "%TMPZIP%" >nul 2>&1
powershell -ExecutionPolicy Bypass -Command "$ProgressPreference = 'SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; $r = Invoke-RestMethod 'https://api.github.com/repos/PavelLizunov/VPNRouter/releases?per_page=10'; $stable = $r | Where-Object { -not $_.prerelease -and -not $_.draft } | Select-Object -First 1; $asset = $stable.assets | Where-Object { $_.name -like 'VPNRouter-v*-win.zip' -and $_.name -notlike '*update*' } | Select-Object -First 1; if (-not $asset) { Write-Host 'install ZIP asset not found'; exit 1 }; Invoke-WebRequest -Uri $asset.browser_download_url -OutFile '%TMPZIP%' -UseBasicParsing; $shaAsset = $stable.assets | Where-Object { $_.name -eq ($asset.name + '.sha256') } | Select-Object -First 1; if (-not $shaAsset) { Write-Host 'SHA256 sidecar asset not found - fail closed'; Remove-Item '%TMPZIP%' -Force -ErrorAction SilentlyContinue; exit 1 }; $shaTmp = Join-Path $env:TEMP 'vpnr-repair-sha.txt'; Invoke-WebRequest -Uri $shaAsset.browser_download_url -OutFile $shaTmp -UseBasicParsing; $expectedSha = (Get-Content -Raw $shaTmp).Trim().Split()[0].ToLower(); Remove-Item $shaTmp -Force -ErrorAction SilentlyContinue; if ($expectedSha -notmatch '^[0-9a-f]{64}$') { Write-Host 'sidecar is not a strict 64-hex SHA256 - fail closed'; Remove-Item '%TMPZIP%' -Force -ErrorAction SilentlyContinue; exit 1 }; $actualSha = (Get-FileHash -Algorithm SHA256 '%TMPZIP%').Hash.ToLower(); if ($actualSha -ne $expectedSha) { Write-Host ('SHA256 mismatch expected=' + $expectedSha + ' actual=' + $actualSha); Remove-Item '%TMPZIP%' -Force -ErrorAction SilentlyContinue; exit 1 }; Write-Host ('SHA256 verified: ' + $actualSha)"
if errorlevel 1 (
    echo.
    echo [ERROR] Download or SHA256 verification failed. Check internet connection or release assets.
    pause
    exit /b 2
)

echo [5/7] Extracting to %ProgramFiles%\VPNRouter...
powershell -ExecutionPolicy Bypass -Command "Expand-Archive -Path '%TMPZIP%' -DestinationPath '%ProgramFiles%\VPNRouter' -Force"
if errorlevel 1 (
    echo.
    echo [ERROR] Extract failed.
    pause
    exit /b 3
)
del /Q "%TMPZIP%" >nul 2>&1

echo [6/7] Restoring Service failure recovery...
sc failure VPNRouter reset= 86400 actions= restart/60000/restart/60000/restart/60000 >nul 2>&1

sc query VPNRouter >nul 2>&1
if %errorlevel% equ 0 (
    echo [7/7] Starting VPNRouter Service...
    sc start VPNRouter >nul 2>&1
) else (
    echo [7/7] Service not installed — skipping start
)

echo.
echo === Verification ===
"%ProgramFiles%\VPNRouter\app\VPNRouter.CLI.exe" doctor 2>nul | findstr /R "Version Service sing-box"

echo.
echo === Repair complete ===
echo You can now launch VPNRouter from the Start Menu.
echo.
pause
