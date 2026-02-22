@echo off
:: VPNRouter Update Helper
:: Run this if auto-update failed. It will copy files from
:: the staging directory to the application directory and relaunch.
::
:: Usage: Just double-click this file (run as Administrator)

setlocal

set "STAGING=%ProgramData%\VPNRouter\update-staging\extracted"
set "APPDIR=%~dp0"

:: Check if we have extracted files to apply
if not exist "%STAGING%\VPNRouter.GUI.exe" (
    echo.
    echo No pending update found in staging directory.
    echo.
    echo If you want to install manually, just extract the ZIP
    echo and replace all files in your VPNRouter folder.
    echo.
    pause
    exit /b 1
)

echo.
echo === VPNRouter Update Helper ===
echo.
echo Source:      %STAGING%
echo Destination: %APPDIR%
echo.

:: Kill running VPNRouter processes
taskkill /f /im VPNRouter.GUI.exe 2>NUL
taskkill /f /im VPNRouter.CLI.exe 2>NUL
timeout /t 2 /nobreak >NUL

:: Copy all files
echo Copying files...
xcopy /s /y /q "%STAGING%\*" "%APPDIR%" >NUL 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Failed to copy files. Make sure you're running as Administrator.
    pause
    exit /b 1
)

echo Files copied successfully.
echo.

:: Clean up staging
rd /s /q "%ProgramData%\VPNRouter\update-staging" 2>NUL

:: Relaunch
echo Launching updated VPNRouter...
start "" "%APPDIR%VPNRouter.GUI.exe"

echo Done!
timeout /t 3 /nobreak >NUL
exit /b 0
