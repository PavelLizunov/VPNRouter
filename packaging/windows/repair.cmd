@echo off
setlocal
title VPNRouter Self-Repair
rem The official web installer performs elevation and verifies the release in protected staging.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "$ErrorActionPreference='Stop'; $env:PSModulePath=[IO.Path]::Combine($PSHOME,'Modules'); [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; $reply=Invoke-WebRequest -Uri 'https://vpn.ninitux.com/install.ps1' -UseBasicParsing; $body=$reply.Content; if ($body -is [byte[]]) { $body=[Text.Encoding]::UTF8.GetString($body) }; & ([ScriptBlock]::Create([string]$body)) -Repair"
set "result=%errorlevel%"
pause
exit /b %result%
