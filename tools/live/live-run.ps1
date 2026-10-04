# Starts live-harness.ps1 in the interactive session of the test user (a scheduled task) and waits for it.
# usage (over ssh):  powershell -File live-run.ps1 -Scenario tabs [-Count 3] [-MaxServers 20] [-TimeoutSec 900]
param(
    [Parameter(Mandatory = $true)][string]$Scenario,
    [int]$Count = 3,
    [int]$MaxServers = 20,
    [string]$Tab = 'settings',
    [int]$TimeoutSec = 900,
    [string]$Harness = 'C:\android-build\live-harness.ps1',
    [string]$OutDir = 'C:\android-build\live'
)
$principal = New-ScheduledTaskPrincipal -UserId 'winbrat\tester' -LogonType Interactive -RunLevel Highest
$arg = '-NoProfile -ExecutionPolicy Bypass -Command "& ' + $Harness + ' -Scenario ' + $Scenario + ' -Count ' + $Count + ' -MaxServers ' + $MaxServers + ' -Tab ' + $Tab + ' -OutDir ' + $OutDir + ' *> ' + $OutDir + '\last-error.txt"'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$before = @(Get-ChildItem $OutDir -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
$a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arg
Register-ScheduledTask -TaskName LiveHarness -Action $a -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName LiveHarness
$deadline = (Get-Date).AddSeconds($TimeoutSec)
do { Start-Sleep -Seconds 3; $st = (Get-ScheduledTask -TaskName LiveHarness).State } while ($st -eq 'Running' -and (Get-Date) -lt $deadline)
Unregister-ScheduledTask -TaskName LiveHarness -Confirm:$false
$run = Get-ChildItem $OutDir -Directory | Where-Object { $before -notcontains $_.Name } | Sort-Object Name | Select-Object -Last 1
if ($run) {
    'RUN-DIR ' + $run.FullName
    Get-Content (Join-Path $run.FullName 'steps.log') -ErrorAction SilentlyContinue
    Get-Content (Join-Path $run.FullName 'done.txt') -ErrorAction SilentlyContinue
} else {
    'NO RUN DIRECTORY'
    Get-Content (Join-Path $OutDir 'last-error.txt') -ErrorAction SilentlyContinue | Select-Object -First 12
}
