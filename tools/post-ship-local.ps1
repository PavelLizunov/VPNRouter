<#
.SYNOPSIS
  Post-ship verification of a published candidate, run ON the verification VM itself (WINBRAT, the windows-worker).

.DESCRIPTION
  tools/post-ship-verify.ps1 is written for a maintainer PC: it needs the WinRM credential file and a local .NET SDK, and
  it runs the PageScreenshotTests gate first. This script covers the part that is about the shipped binary and runs it
  directly on WINBRAT, with the unchanged tools/brat-verify.ps1 and tools/brat-stability.ps1 behind
  tools/brat-local-shim.ps1 (no WinRM, no credential):

    1. resolve the release tag commit and check out exactly that commit (the verifier scripts of the release itself);
    2. download the published Windows install zip and its sidecar, check the hash;
    3. identity check -> state -> clean deploy (replaces the installed app and relaunches it on the interactive desktop)
       -> N cold connect/disconnect cycles driven through the UI -> lifecycle events -> final state.

  It does NOT cover, and the report must say so: the screenshot gate, the commit CI gate and the release inventory and
  hash checks of the other platforms. Run `tools/verify-last-commit-ci.ps1 -Strict` and the integrity workflow separately.
  Run it in a Windows PowerShell 5.1 session of the user that owns the interactive desktop (it starts the GUI and the
  UI-automation helper in that session through scheduled tasks). It changes the VM: the installed VPNRouter is replaced.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\post-ship-local.ps1 -Version 2.50.0-r13
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-r[1-9][0-9]*)?$')]
    [string]$Version,

    [ValidateRange(2, 10)]
    [int]$Cycles = 2,

    [string]$WorkRoot = 'C:\android-build',

    [string]$Repo = 'PavelLizunov/VPNRouter'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Shim = Join-Path $PSScriptRoot 'brat-local-shim.ps1'
if (-not (Test-Path $Shim)) { throw "Missing $Shim." }
$tag = "v$Version"
$checkout = Join-Path $WorkRoot "post-$Version"
$logPath = Join-Path $WorkRoot "post-$Version.log"
$zipName = "VPNRouter-v$Version-win.zip"

Start-Transcript -Path $logPath -Force | Out-Null
try {
    $remote = "https://github.com/$Repo.git"
    $sha = ''
    foreach ($ref in @("refs/tags/$tag^{}", "refs/tags/$tag")) {
        $line = @(& git ls-remote $remote $ref) | Select-Object -First 1
        if ($line) { $sha = ([string]$line -split '\s+')[0]; break }
    }
    if ($sha -notmatch '^[0-9a-f]{40}$') { throw "The tag $tag could not be resolved to a commit." }
    "Release commit: $sha"

    if (Test-Path $checkout) { Remove-Item -Recurse -Force $checkout }
    & git init -q $checkout
    & git -C $checkout remote add origin $remote
    & git -C $checkout fetch -q --depth 1 origin $sha
    & git -C $checkout checkout -q --force FETCH_HEAD
    if ((& git -C $checkout rev-parse HEAD).Trim() -ne $sha) { throw 'The checkout does not match the release commit.' }

    # Windows PowerShell 5.1 reads a script without a BOM as ANSI, and tools/brat-verify.ps1 of older tags holds Cyrillic
    # literals inside the UI-automation helper text: give the checkout copy a BOM (content unchanged).
    $verifyScript = Join-Path $checkout 'tools\brat-verify.ps1'
    $bytes = [IO.File]::ReadAllBytes($verifyScript)
    if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF) {
        [IO.File]::WriteAllBytes($verifyScript, ([byte[]](0xEF, 0xBB, 0xBF) + $bytes))
    }

    $base = "https://github.com/$Repo/releases/download/$tag"
    foreach ($name in @($zipName, "$zipName.sha256")) {
        Invoke-WebRequest -UseBasicParsing -Uri "$base/$name" -OutFile (Join-Path $checkout $name)
    }
    $expected = (Get-Content (Join-Path $checkout "$zipName.sha256") -Raw).Trim().ToLowerInvariant()
    $actual = (Get-FileHash -Algorithm SHA256 (Join-Path $checkout $zipName)).Hash.ToLowerInvariant()
    if ($expected -notmatch '^[0-9a-f]{64}$' -or $actual -ne $expected) { throw "The downloaded $zipName does not match its sidecar." }
    "Downloaded $zipName, sha256 $actual"

    # The verifier scripts expect the maintainer credential file only to name the interactive user: a dummy one is enough.
    $user = "$env:USERDOMAIN\$env:USERNAME"
    New-Object System.Management.Automation.PSCredential($user, (ConvertTo-SecureString 'unused' -AsPlainText -Force)) |
        Export-Clixml (Join-Path $checkout '.testpc-cred-192.168.0.106.xml')

    . $Shim
    Set-Location $checkout
    $verify = Join-Path $checkout 'tools\brat-verify.ps1'
    $stability = Join-Path $checkout 'tools\brat-stability.ps1'
    $since = [DateTimeOffset]::UtcNow

    '=== identity'; & $verify -Action identity
    '=== state before'; & $verify -Action state
    '=== deploy'; & $verify -Action deploy -Version $Version
    '=== state after deploy'; & $verify -Action state
    '=== cold cycles'; & $stability -Mode ColdCycles -Version $Version -Cycles $Cycles -RunSinceUtc $since.ToString('o')
    '=== state after cycles'; & $verify -Action state
    '=== lifecycle'; & $verify -Action lifecycle -SinceUtc $since.ToString('o')
    "POSTSHIP-LOCAL: PASS (version $Version, commit $sha, $Cycles cold cycles)"
    $exitCode = 0
}
catch {
    "POSTSHIP-LOCAL: FAILED - $($_.Exception.Message)"
    $_.ScriptStackTrace
    $exitCode = 1
}
finally { Stop-Transcript | Out-Null }
exit $exitCode
