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
    [string]$InteractiveUser,
    [string]$ExpectedMachineName
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Fail($msg)       { Write-Host "[!] $msg" -ForegroundColor Red; exit 1 }

if (-not $Version) {
    $verFile = Join-Path $Root "VPNRouter.Core\AppVersion.cs"
    if (-not (Test-Path $verFile)) { Fail "AppVersion.cs not found; pass -Version explicitly." }
    $m = Select-String -Path $verFile -Pattern 'Version\s*=\s*"([^"]+)"' | Select-Object -First 1
    if (-not $m) { Fail "Could not parse Version from AppVersion.cs; pass -Version explicitly." }
    $Version = $m.Matches[0].Groups[1].Value
}
Write-Step "Target version: $Version  ->  $TestHost"

if ($Build) {
    Write-Step "Building on host (build.ps1 -Version $Version)"
    & powershell -ExecutionPolicy Bypass -File (Join-Path $Root "build.ps1") -Version $Version
    if ($LASTEXITCODE -ne 0) { Fail "build.ps1 failed (exit $LASTEXITCODE)." }
}

$zipName = "VPNRouter-v$Version-win.zip"
$zipPath = Join-Path $Root $zipName
if (-not (Test-Path $zipPath)) {
    Fail "Artifact not found: $zipPath`n    Build it first:  .\build.ps1 -Version $Version   (or pass -Build)"
}
$zipSizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Step "Artifact: $zipName ($zipSizeMb MB)"

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
    if ($ExpectedMachineName) {
        $actualMachineName = Invoke-Command -Session $session -ScriptBlock { [Environment]::MachineName }
        if ($actualMachineName -ine $ExpectedMachineName) {
            throw "Identity check failed: $TestHost reported MachineName '$actualMachineName', expected '$ExpectedMachineName'. Refusing to deploy."
        }
        Write-Step "Verified target identity: $actualMachineName"
    }

    Write-Step "Stopping running VPNRouter on $TestHost (if any)"
    Invoke-Command -Session $session -ArgumentList $InstallDir -ScriptBlock {
        param($InstallDir)
        $canonicalInstall = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
        if ((Split-Path $canonicalInstall -Leaf) -ine 'app' -or
            (Split-Path (Split-Path $canonicalInstall -Parent) -Leaf) -ine 'VPNRouter') {
            throw "InstallDir must be an exact ...\VPNRouter\app directory."
        }

        $servicePaths = @(
            (Join-Path $canonicalInstall 'VPNRouter.Service.exe'),
            (Join-Path $canonicalInstall 'service\VPNRouter.Service.exe'))
        $service = Get-CimInstance Win32_Service -Filter "Name='VPNRouter'" -ErrorAction Stop
        if ($service) {
            $serviceExe = if ([string]$service.PathName -match '^\s*"(?<exe>[^"]+)"') {
                $Matches.exe
            } else {
                ([string]$service.PathName -split '\s+--service(?:\s|$)', 2)[0].Trim()
            }
            if ($servicePaths -notcontains ([IO.Path]::GetFullPath($serviceExe))) {
                throw 'The VPNRouter service name is owned by a non-canonical executable path.'
            }
            if ([string]$service.State -ine 'Stopped') {
                Stop-Service -Name 'VPNRouter' -Force -ErrorAction Stop
            }
        }

        $ownedPaths = @(
            (Join-Path $canonicalInstall 'VPNRouter.App.exe'),
            (Join-Path $canonicalInstall 'VPNRouter.GUI.exe'),
            $servicePaths[0],
            $servicePaths[1],
            'C:\ProgramData\VPNRouter\bin\sing-box.exe')
        $owned = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
            $path = [string]$_.ExecutablePath
            $path -and $ownedPaths -icontains ([IO.Path]::GetFullPath($path))
        })
        foreach ($process in $owned) {
            Stop-Process -Id ([int]$process.ProcessId) -Force -ErrorAction Stop
        }
        if ($owned.Count -gt 0) { Start-Sleep -Seconds 2 }
    }

    $remoteZip = "C:\Windows\Temp\$zipName"
    Write-Step "Copying artifact to $TestHost`:$remoteZip"
    Copy-Item -Path $zipPath -Destination $remoteZip -ToSession $session -Force

    $modeLabel = if ($FirstInstall) { 'fresh install' } else { 'update' }
    Write-Step "Installing on $TestHost (mode: $modeLabel)"
    $installResult = Invoke-Command -Session $session -ArgumentList $remoteZip, $InstallDir, [bool]$FirstInstall -ScriptBlock {
        param($RemoteZip, $InstallDir, $Fresh)
        $ErrorActionPreference = "Stop"
        $runId = [guid]::NewGuid().ToString('N')
        $extract = "C:\Windows\Temp\vpnr-extract-$runId"
        New-Item -ItemType Directory -Path $extract -Force | Out-Null
        Expand-Archive -Path $RemoteZip -DestinationPath $extract -Force

        $srcApp = Join-Path $extract "app"
        if (-not (Test-Path $srcApp)) { throw "ZIP has no app\ subdir; contents: $((Get-ChildItem $extract).Name -join ', ')" }

        $canonicalInstall = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
        $installParent = Split-Path $canonicalInstall -Parent
        if ((Split-Path $canonicalInstall -Leaf) -ine 'app' -or
            (Split-Path $installParent -Leaf) -ine 'VPNRouter') {
            throw "InstallDir must be an exact ...\VPNRouter\app directory."
        }
        if ($Fresh) {
            New-Item -ItemType Directory -Path $installParent -Force | Out-Null
            Get-ChildItem $extract -Filter "*.cmd" -File -ErrorAction SilentlyContinue |
                ForEach-Object { Copy-Item $_.FullName $installParent -Force }
        }
        if (-not (Test-Path $canonicalInstall) -and -not $Fresh) {
            throw "Install dir $InstallDir does not exist. Re-run with -FirstInstall."
        }

        $stage = Join-Path $installParent ".app-stage-$runId"
        $backup = Join-Path $installParent ".app-backup-$runId"
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        Copy-Item -Path (Join-Path $srcApp '*') -Destination $stage -Recurse -Force

        $sourceFiles = @(Get-ChildItem $srcApp -Recurse -File | ForEach-Object {
            $relative = $_.FullName.Substring($srcApp.Length).TrimStart('\')
            "$relative|$((Get-FileHash $_.FullName -Algorithm SHA256).Hash)"
        } | Sort-Object)
        $stageFiles = @(Get-ChildItem $stage -Recurse -File | ForEach-Object {
            $relative = $_.FullName.Substring($stage.Length).TrimStart('\')
            "$relative|$((Get-FileHash $_.FullName -Algorithm SHA256).Hash)"
        } | Sort-Object)
        if (($sourceFiles -join "`n") -cne ($stageFiles -join "`n")) {
            throw 'Staged install does not exactly match the release archive.'
        }

        $movedOld = $false
        try {
            if (Test-Path $canonicalInstall) {
                Move-Item -LiteralPath $canonicalInstall -Destination $backup
                $movedOld = $true
            }
            Move-Item -LiteralPath $stage -Destination $canonicalInstall
        }
        catch {
            if ($movedOld -and -not (Test-Path $canonicalInstall) -and (Test-Path $backup)) {
                Move-Item -LiteralPath $backup -Destination $canonicalInstall
            }
            throw
        }
        if (Test-Path $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }

        $appDll = Join-Path $canonicalInstall "VPNRouter.App.dll"
        $stamp = if (Test-Path $appDll) { (Get-Item $appDll).LastWriteTime } else { $null }
        [pscustomobject]@{ InstalledDll = $appDll; LastWrite = $stamp }
    }
    Write-Host "    VPNRouter.App.dll @ $($installResult.LastWrite)" -ForegroundColor Green

    Invoke-Command -Session $session -ScriptBlock {
        Get-ChildItem 'C:\Windows\Temp' -Directory -Filter 'vpnr-extract-*' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^vpnr-extract-[0-9a-f]{32}$' } |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (-not $NoLaunch) {
        Write-Step "Launching GUI on $TestHost interactive desktop (as $InteractiveUser)"
        $launchResult = Invoke-Command -Session $session -ArgumentList $InstallDir, $InteractiveUser -ScriptBlock {
            param($InstallDir, $User)
            $exe = Join-Path $InstallDir "VPNRouter.GUI.exe"
            if (-not (Test-Path $exe)) { throw "$exe missing after install." }

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
            throw "VPNRouter.App.exe is not running 6s after launch on $TestHost. Check %ProgramData%\VPNRouter\logs\; interactive launch requires $InteractiveUser to be logged in."
        }
    }

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
