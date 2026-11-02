@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Collect-VPNRouterBusiness-Diagnostics.ps1"
set "support_exit=%errorlevel%"
pause
exit /b %support_exit%
