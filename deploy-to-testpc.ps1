<#
.SYNOPSIS
    Push a VPNRouter build from this dev host to a separate physical/virtual
    Windows test machine over the LAN (WinRM), install it, and (optionally)
    relaunch the GUI on the test machine's interactive desktop.

.DESCRIPTION
    Workflow: develop + build on THIS host, test on a separate machine.

      [dev host] build.ps1 -> VPNRouter-v<ver>-win.zip
           |  WinRM (Copy-Item -ToSession + Invoke-Command)
           v
      [test machine] stop old -> extract app\ over install dir -> relaunch

    Mirrors the install/launch logic the project already uses in
    .claude/skills/post-ship-mcp-verify/scripts/post-ship-install-launch.ps1,
    but performed remotely against -TestHost instead of locally.

    Artifact source: the Install ZIP produced by build.ps1 at the repo root,
    named "VPNRouter-v<Version>-win.zip" (its archive root contains app\ +
    "Start VPN.cmd"). The script copies app\* into the target install dir.

.PARAMETER TestHost
    Hostname or IP of the test machine (must have WinRM enabled — see PREREQS).

.PARAMETER Version
    Build version. Defaults to the literal in VPNRouter.Core\AppVersion.cs.
    With -Build, this value is passed to build.ps1 (must match AppVersion.cs,
    build.ps1 aborts on mismatch).

.PARAMETER Build
    Run build.ps1 -Version <Version> on this host first to (re)produce the ZIP.

.PARAMETER FirstInstall
    Lay down a full fresh install (creates the install dir, copies app\ +
    "Start VPN.cmd"). Use the first time a machine has no VPNRouter installed.
    Without it, the script does an in-place update (overwrite app binaries).

.PARAMETER InstallDir
    Target install directory on the test machine.
    Default: C:\Program Files\VPNRouter\app

.PARAMETER NoLaunch
    Deploy only; do not relaunch the GUI afterwards.

.PARAMETER TailLog
    After launch, pull back the last 40 lines of the newest
    %ProgramData%\VPNRouter\logs\ file from the test machine.

.PARAMETER Credential
    PSCredential for the test machine. If omitted you are prompted.

.PARAMETER InteractiveUser
    The user account that is logged in interactively on the test machine —
    used to launch the GUI on the visible desktop via a transient scheduled
    task. Defaults to the connecting credential's user name.

.EXAMPLE
    # First-time install to a fresh test box, build fresh, then launch
    .\deploy-to-testpc.ps1 -TestHost 192.168.0.50 -Build -FirstInstall

.EXAMPLE
    # Fast iteration: rebuild + push binaries over the existing install
    .\deploy-to-testpc.ps1 -TestHost test-vm -Build

.EXAMPLE
    # Deploy an already-built ZIP without rebuilding, and tail the log
    .\deploy-to-testpc.ps1 -TestHost test-vm -TailLog

.NOTES
    PREREQS (one-time):
      On the TEST machine (elevated PowerShell):
          Enable-PSRemoting -Force
      On THIS host, if the test machine is NOT domain-joined (workgroup):
          Set-Item WSMan:\localhost\Client\TrustedHosts -Value "<ip-or-name>" -Concatenate -Force
      The connecting account must be a local Administrator on the test machine
      (VPNRouter needs admin for TUN / firewall / ETW).

    NETWORK NOTE: VPNRouter creates its own TUN adapter on the test machine.
    If the test machine reaches the LAN *through* the host's VPN, expect the
    usual VPN-in-VPN routing quirks — test with the test machine on a plain
    LAN/NAT path first.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$TestHost,
    [string]$Version,
    [switch]$Build,
    [switch]$FirstInstall,
    [string]$InstallDir = "C:\Program Files\VPNRouter\app",
    [switch]$NoLaunch,
    [switch]$TailLog,
    [System.Management.Automation.PSCredential]$Credential,
    [switch]$ForgetCredential,
    [string]$InteractiveUser
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Fail($msg)       { Write-Host "[!] $msg" -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------------------
# 1. Resolve version
# ---------------------------------------------------------------------------
if (-not $Version) {
    $verFile = Join-Path $Root "VPNRouter.Core\AppVersion.cs"
    if (-not (Test-Path $verFile)) { Fail "AppVersion.cs not found; pass -Version explicitly." }
    $m = Select-String -Path $verFile -Pattern 'Version\s*=\s*"([^"]+)"' | Select-Object -First 1
    if (-not $m) { Fail "Could not parse Version from AppVersion.cs; pass -Version explicitly." }
    $Version = $m.Matches[0].Groups[1].Value
}
Write-Step "Target version: $Version  ->  $TestHost"

# ---------------------------------------------------------------------------
# 2. Optionally build on this host
# ---------------------------------------------------------------------------
if ($Build) {
    Write-Step "Building on host (build.ps1 -Version $Version)"
    & powershell -ExecutionPolicy Bypass -File (Join-Path $Root "build.ps1") -Version $Version
    if ($LASTEXITCODE -ne 0) { Fail "build.ps1 failed (exit $LASTEXITCODE)." }
}

# ---------------------------------------------------------------------------
# 3. Locate the Install ZIP
# ---------------------------------------------------------------------------
$zipName = "VPNRouter-v$Version-win.zip"
$zipPath = Join-Path $Root $zipName
if (-not (Test-Path $zipPath)) {
    Fail "Artifact not found: $zipPath`n    Build it first:  .\build.ps1 -Version $Version   (or pass -Build)"
}
$zipSizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Step "Artifact: $zipName ($zipSizeMb MB)"

# ---------------------------------------------------------------------------
# 4. Connect (WinRM)
# ---------------------------------------------------------------------------
# Credential: prompt once, then cache it DPAPI-encrypted (Export-Clixml protects
# the password so it is decryptable ONLY by this user on this machine). Subsequent
# runs load it silently. Delete the cache file (or pass -ForgetCredential) to
# re-prompt — e.g. after the target password changes.
$credCache = Join-Path $Root (".testpc-cred-{0}.xml" -f ($TestHost -replace '[^\w.-]', '_'))
if ($ForgetCredential -and (Test-Path $credCache)) {
    Remove-Item $credCache -Force
    Write-Step "Forgot saved credential ($credCache)"
}
if (-not $Credential) {
    if (Test-Path $credCache) {
        $Credential = Import-Clixml $credCache
        Write-Step "Using saved credential for $TestHost ($($Credential.UserName)) - delete the cache file to re-prompt"
    } else {
        $Credential = Get-Credential -Message "Local admin on $TestHost (VPNRouter needs admin on the target)"
        $Credential | Export-Clixml $credCache
        Write-Step "Saved credential (DPAPI-encrypted, this user+machine only) to $credCache"
    }
}
if (-not $InteractiveUser) { $InteractiveUser = $Credential.UserName }

Write-Step "Opening WinRM session to $TestHost"
try {
    $session = New-PSSession -ComputerName $TestHost -Credential $Credential -ErrorAction Stop
} catch {
    Write-Host "[!] WinRM connect failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "    Check PREREQS in the script header:" -ForegroundColor Yellow
    Write-Host "      - On target (elevated):  Enable-PSRemoting -Force" -ForegroundColor Yellow
    Write-Host "      - On host (if workgroup): Set-Item WSMan:\localhost\Client\TrustedHosts -Value '$TestHost' -Concatenate -Force" -ForegroundColor Yellow
    exit 1
}

try {
    # -----------------------------------------------------------------------
    # 5. Stop any running VPNRouter on the target
    # -----------------------------------------------------------------------
    Write-Step "Stopping running VPNRouter on $TestHost (if any)"
    Invoke-Command -Session $session -ScriptBlock {
        $p = Get-Process VPNRouter* -ErrorAction SilentlyContinue
        if ($p) { $p | ForEach-Object { try { $_.Kill() } catch {} }; Start-Sleep -Seconds 2 }
    }

    # -----------------------------------------------------------------------
    # 6. Copy the ZIP to the target
    # -----------------------------------------------------------------------
    $remoteZip = "C:\Windows\Temp\$zipName"
    Write-Step "Copying artifact to $TestHost`:$remoteZip"
    Copy-Item -Path $zipPath -Destination $remoteZip -ToSession $session -Force

    # -----------------------------------------------------------------------
    # 7. Extract + install on the target
    # -----------------------------------------------------------------------
    $modeLabel = if ($FirstInstall) { 'fresh install' } else { 'update' }
    Write-Step "Installing on $TestHost (mode: $modeLabel)"
    $installResult = Invoke-Command -Session $session -ArgumentList $remoteZip, $InstallDir, [bool]$FirstInstall -ScriptBlock {
        param($RemoteZip, $InstallDir, $Fresh)
        $ErrorActionPreference = "Stop"
        $extract = "C:\Windows\Temp\vpnr-extract"
        if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
        New-Item -ItemType Directory -Path $extract -Force | Out-Null
        Expand-Archive -Path $RemoteZip -DestinationPath $extract -Force

        $srcApp = Join-Path $extract "app"
        if (-not (Test-Path $srcApp)) { throw "ZIP has no app\ subdir; contents: $((Get-ChildItem $extract).Name -join ', ')" }

        if ($Fresh) {
            New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
            # Copy the launcher cmd that sits next to app\ in the package, if present.
            Get-ChildItem $extract -Filter "*.cmd" -File -ErrorAction SilentlyContinue |
                ForEach-Object { Copy-Item $_.FullName (Split-Path $InstallDir -Parent) -Force }
        }
        if (-not (Test-Path $InstallDir)) {
            throw "Install dir $InstallDir does not exist. Re-run with -FirstInstall."
        }
        Copy-Item -Path (Join-Path $srcApp '*') -Destination $InstallDir -Recurse -Force

        $appDll = Join-Path $InstallDir "VPNRouter.App.dll"
        $stamp = if (Test-Path $appDll) { (Get-Item $appDll).LastWriteTime } else { $null }
        [pscustomobject]@{ InstalledDll = $appDll; LastWrite = $stamp }
    }
    Write-Host "    VPNRouter.App.dll @ $($installResult.LastWrite)" -ForegroundColor Green

    Invoke-Command -Session $session -ScriptBlock { Remove-Item "C:\Windows\Temp\vpnr-extract" -Recurse -Force -ErrorAction SilentlyContinue }

    # -----------------------------------------------------------------------
    # 8. Launch on the interactive desktop (transient scheduled task)
    # -----------------------------------------------------------------------
    if (-not $NoLaunch) {
        Write-Step "Launching GUI on $TestHost interactive desktop (as $InteractiveUser)"
        $launchResult = Invoke-Command -Session $session -ArgumentList $InstallDir, $InteractiveUser -ScriptBlock {
            param($InstallDir, $User)
            $exe = Join-Path $InstallDir "VPNRouter.GUI.exe"
            if (-not (Test-Path $exe)) { throw "$exe missing after install." }

            # A WinRM session runs in session 0 (no visible desktop). To put the
            # GUI on the user's screen, register a one-shot scheduled task that
            # runs interactively + elevated, start it, then remove it.
            $taskName = "VPNRouterDeployLaunch"
            $action    = New-ScheduledTaskAction -Execute $exe
            $principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Highest
            Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null
            try {
                Start-ScheduledTask -TaskName $taskName
                Start-Sleep -Seconds 6
            } finally {
                Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
            }

            $app = Get-Process -Name VPNRouter.App -ErrorAction SilentlyContinue | Select-Object -First 1
            [pscustomobject]@{ Running = [bool]$app; Pid = $(if ($app) { $app.Id } else { $null }) }
        }
        if ($launchResult.Running) {
            Write-Host "    LAUNCHED: VPNRouter.App.exe PID $($launchResult.Pid) on $TestHost" -ForegroundColor Green
        } else {
            Write-Host "[!] VPNRouter.App.exe not running 6s after launch." -ForegroundColor Red
            Write-Host "    Check %ProgramData%\VPNRouter\logs\ on $TestHost (use -TailLog)." -ForegroundColor Yellow
            Write-Host "    Note: interactive launch needs $InteractiveUser actually logged in on the target." -ForegroundColor Yellow
        }
    }

    # -----------------------------------------------------------------------
    # 9. Optional: tail the newest log back to the host
    # -----------------------------------------------------------------------
    if ($TailLog) {
        Write-Step "Tailing newest log from $TestHost"
        $log = Invoke-Command -Session $session -ScriptBlock {
            $dir = Join-Path $env:ProgramData "VPNRouter\logs"
            if (-not (Test-Path $dir)) { return "(no log dir at $dir yet)" }
            $f = Get-ChildItem $dir -Filter "*.log" -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if (-not $f) { return "(no .log files in $dir yet)" }
            "----- $($f.Name) (last 40 lines) -----`n" + ((Get-Content $f.FullName -Tail 40) -join "`n")
        }
        Write-Host $log -ForegroundColor Gray
    }

    Write-Host ""
    Write-Host "Done. $zipName ($Version) deployed to $TestHost." -ForegroundColor Green
}
finally {
    if ($session) { Remove-PSSession $session }
}
