# One executable post-ship gate. Visual screenshots run headlessly in the
# isolated test process; every app install, live UI action and network check is
# delegated to the fixed-identity WINBRAT verifier.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-r[1-9][0-9]*)?$')]
    [string]$Version,

    [ValidateRange(2, 10)]
    [int]$Cycles = 2
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path $PSScriptRoot -Parent
$BratVerify = Join-Path $PSScriptRoot 'brat-verify.ps1'
$BratStability = Join-Path $PSScriptRoot 'brat-stability.ps1'
$CiGate = Join-Path $PSScriptRoot 'verify-last-commit-ci.ps1'
$PowerShellHost = (Get-Process -Id $PID).Path
$EvidenceRoot = Join-Path $Root "artifacts\post-ship\$Version"
$ReleaseRoot = Join-Path $EvidenceRoot 'release'
$Repo = 'PavelLizunov/VPNRouter'
$ZipName = "VPNRouter-v$Version-win.zip"
$HashName = "$ZipName.sha256"
$UpdateZipName = "VPNRouter-update-v$Version-win.zip"
$UpdateHashName = "$UpdateZipName.sha256"
$ZipPath = Join-Path $Root $ZipName
$HashPath = Join-Path $Root $HashName
$FreshZipPath = Join-Path $ReleaseRoot $ZipName
$FreshHashPath = Join-Path $ReleaseRoot $HashName
$FreshUpdateZipPath = Join-Path $ReleaseRoot $UpdateZipName
$FreshUpdateHashPath = Join-Path $ReleaseRoot $UpdateHashName
$RemoteMutationStarted = $false
$RunFailure = $null
$CleanupFailure = $null
$CurrentStep = 'Initialize'
$RemoteVerificationStartedUtc = $null
$GateMutex = New-Object System.Threading.Mutex($false, 'Local\VPNRouterBratStability')
$GateMutexHeld = $false

foreach ($path in @($BratVerify, $BratStability, $CiGate)) {
    if (-not (Test-Path $path)) { throw "Required post-ship tool is missing: $path" }
}

function Invoke-CheckedNative {
    param(
        [Parameter(Mandatory = $true)] [string]$FilePath,
        [Parameter(Mandatory = $true)] [string[]]$Arguments,
        [Parameter(Mandatory = $true)] [string]$Step
    )
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Step failed with exit code $LASTEXITCODE." }
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)] [string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
        finally { $algorithm.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Assert-TrueSplitBundle {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$ArchivePrefix
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | Where-Object { $_.Length -gt 0 } | ForEach-Object {
            $_.FullName.Replace('\', '/')
        })
        $prefix = $ArchivePrefix.TrimEnd('/')
        $required = @(
            "$prefix/driver/mullvad-split-tunnel.sys",
            "$prefix/driver/mullvad-split-tunnel.cat",
            "$prefix/driver/mullvad-split-tunnel.inf",
            "$prefix/driver/checksums.sha256",
            "$prefix/LICENSE.split-tunnel"
        )
        $missing = @($required | Where-Object { $entries -notcontains $_ })
        if ($missing.Count -gt 0) {
            throw "The published Windows ZIP is missing the required True Split driver bundle under $prefix."
        }
    }
    finally { $archive.Dispose() }
}

function Get-VerifiedArtifactHash {
    param(
        [Parameter(Mandatory = $true)] [string]$ArtifactPath,
        [Parameter(Mandatory = $true)] [string]$SidecarPath
    )

    if (-not (Test-Path $ArtifactPath) -or -not (Test-Path $SidecarPath)) {
        throw 'A published Windows ZIP and its SHA256 sidecar were not both downloaded.'
    }
    $expected = (Get-Content $SidecarPath -Raw).Trim().ToLowerInvariant()
    if ($expected -notmatch '^[0-9a-f]{64}$') {
        throw "The published SHA256 sidecar is malformed: $SidecarPath"
    }
    $actual = Get-Sha256Hex $ArtifactPath
    if ($actual -ne $expected) {
        throw "The freshly downloaded release artifact does not match its SHA256 sidecar: $ArtifactPath"
    }
    return [ordered]@{ Expected = $expected; Actual = $actual }
}

function Assert-ExactReleaseAssets {
    $expected = @(
        "VPNRouter-v$Version-win.zip",
        "VPNRouter-v$Version-win.zip.sha256",
        "VPNRouter-update-v$Version-win.zip",
        "VPNRouter-update-v$Version-win.zip.sha256",
        "VPNRouter-v$Version-mac.dmg",
        "VPNRouter-v$Version-mac.dmg.sha256",
        "VPNRouter-v$Version-mac.zip",
        "VPNRouter-v$Version-mac.zip.sha256",
        "VPNRouter-v$Version-linux.tar.gz",
        "VPNRouter-v$Version-linux.tar.gz.sha256",
        "VPNRouter-v$Version-linux-amd64.deb",
        "VPNRouter-v$Version-linux-amd64.deb.sha256",
        "VPNRouter-v$Version-linux-x86_64.AppImage",
        "VPNRouter-v$Version-linux-x86_64.AppImage.sha256",
        "VPNRouter-v$Version-android-arm64.apk",
        "VPNRouter-v$Version-android-arm64.apk.sha256"
    )
    $releaseJson = (& gh release view "v$Version" --repo $Repo --json assets 2>&1 | Out-String)
    $releaseViewExitCode = $LASTEXITCODE
    if ($releaseViewExitCode -ne 0) { throw 'The release asset inventory could not be read.' }
    $release = $releaseJson | ConvertFrom-Json
    $actual = @($release.assets | ForEach-Object { [string]$_.name })
    $missing = @($expected | Where-Object { $actual -notcontains $_ })
    $unexpected = @($actual | Where-Object { $expected -notcontains $_ })
    if ($actual.Count -ne $expected.Count -or $missing.Count -gt 0 -or $unexpected.Count -gt 0) {
        throw 'The published release does not contain the exact expected 16 assets.'
    }
}

function Resolve-ProjectDotNet {
    $required = ((Get-Content (Join-Path $Root 'global.json') -Raw | ConvertFrom-Json).sdk.version).ToString()
    $pathDotNet = Get-Command dotnet -ErrorAction SilentlyContinue
    $candidates = @(
        $env:DOTNET_HOST_PATH,
        $(if ($env:USERPROFILE) { Join-Path $env:USERPROFILE '.dotnet\dotnet.exe' }),
        $(if ($pathDotNet) { $pathDotNet.Source })
    ) | Where-Object { $_ } | Select-Object -Unique

    foreach ($candidate in $candidates) {
        if (-not (Test-Path $candidate)) { continue }
        $version = $null
        $previousPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $version = (& $candidate --version 2>$null | Select-Object -First 1)
        }
        finally { $ErrorActionPreference = $previousPreference }
        if ($version -eq $required) { return $candidate }
    }
    throw "Required .NET SDK $required was not found."
}

function Resolve-ReleaseCommit {
    $output = & gh api "repos/$Repo/commits/v$Version" --jq '.sha' 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'The published release tag commit could not be resolved.' }
    $releaseCommit = ($output | Select-Object -Last 1).Trim().ToLowerInvariant()
    if ($releaseCommit -notmatch '^[0-9a-f]{40}$') { throw 'The release tag resolved to an invalid commit identity.' }

    $headCommit = (& git -C $Root rev-parse HEAD 2>$null | Select-Object -Last 1).Trim().ToLowerInvariant()
    if ($LASTEXITCODE -ne 0 -or $headCommit -notmatch '^[0-9a-f]{40}$') {
        throw 'The current checkout commit could not be resolved.'
    }
    if ($headCommit -ne $releaseCommit) {
        throw 'The current checkout does not match the published release tag commit.'
    }

    $trackedChanges = @(& git -C $Root status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'The current checkout status could not be read.' }
    if ($trackedChanges.Count -gt 0) { throw 'The release verification checkout has tracked changes.' }
    return $releaseCommit
}

try {
    if (-not (Test-Path $EvidenceRoot)) {
        New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
    }

    $CurrentStep = 'SourceIdentity'
    $releaseCommit = Resolve-ReleaseCommit

    $CurrentStep = 'VisualGate'
    $dotnet = Resolve-ProjectDotNet
    # Existing PageScreenshotTests walk the pages at fixed dimensions;
    # VisualDiffTests enforce baseline dimensions and pixel tolerance.
    Invoke-CheckedNative -FilePath $dotnet -Arguments @(
        'test', (Join-Path $Root 'VPNRouter.Tests\VPNRouter.Tests.csproj'),
        '-c', 'Release',
        '--filter', 'FullyQualifiedName~PageScreenshotTests|FullyQualifiedName~VisualDiffTests'
    ) -Step 'Page screenshot and visual-diff gate'

    $CurrentStep = 'CommitCi'
    # The CI helper deliberately uses exit codes, so isolate it in a child host.
    Push-Location $Root
    try {
        Invoke-CheckedNative -FilePath $PowerShellHost -Arguments @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', $CiGate,
            '-Commit', $releaseCommit,
            '-Repo', $Repo,
            '-IgnoreSkipped', 'characterization-windows',
            '-RequiredSuccess', 'publish=1,verify=1,test-update=1,test=1,go-test-windows=1,characterization-windows=1',
            '-RequiredWorkflows', 'Build macOS DMG,Build Android APK,Build Linux AppImage + .deb,Publish APT Repository,Verify Release Integrity,Auto-Update Integration Test (Windows)',
            '-Strict'
        ) -Step 'Commit CI gate'
    }
    finally { Pop-Location }

    $CurrentStep = 'WinbratIdentity'
    & $BratVerify -Action identity

    $CurrentStep = 'ReleaseArtifact'
    if (-not (Test-Path $ReleaseRoot)) {
        New-Item -ItemType Directory -Path $ReleaseRoot -Force | Out-Null
    }
    Assert-ExactReleaseAssets
    Invoke-CheckedNative -FilePath 'gh' -Arguments @(
        'release', 'download', "v$Version",
        '--repo', 'PavelLizunov/VPNRouter',
        '--pattern', $ZipName,
        '--pattern', $HashName,
        '--pattern', $UpdateZipName,
        '--pattern', $UpdateHashName,
        '--dir', $ReleaseRoot,
        '--clobber'
    ) -Step 'Fresh release artifact download'

    $installHash = Get-VerifiedArtifactHash -ArtifactPath $FreshZipPath -SidecarPath $FreshHashPath
    $updateHash = Get-VerifiedArtifactHash -ArtifactPath $FreshUpdateZipPath -SidecarPath $FreshUpdateHashPath
    $freshExpected = $installHash.Expected
    $freshActual = $installHash.Actual
    Assert-TrueSplitBundle -Path $FreshZipPath -ArchivePrefix 'app'
    Assert-TrueSplitBundle -Path $FreshUpdateZipPath -ArchivePrefix '_bootstrap'

    $zipExists = Test-Path $ZipPath
    $hashExists = Test-Path $HashPath
    if ($zipExists -xor $hashExists) {
        throw 'Release ZIP and SHA256 sidecar must either both exist or both be absent.'
    }
    if (-not $zipExists) {
        Copy-Item -LiteralPath $FreshZipPath -Destination $ZipPath
        Copy-Item -LiteralPath $FreshHashPath -Destination $HashPath
    }
    else {
        $rootExpected = (Get-Content $HashPath -Raw).Trim().ToLowerInvariant()
        $rootActual = Get-Sha256Hex $ZipPath
        if ($rootExpected -ne $freshExpected -or $rootActual -ne $freshActual) {
            throw 'The repo-root deploy artifact differs from the freshly downloaded release asset.'
        }
    }

    $CurrentStep = 'Deploy'
    try { $GateMutexHeld = $GateMutex.WaitOne(0) }
    catch [System.Threading.AbandonedMutexException] { $GateMutexHeld = $true }
    if (-not $GateMutexHeld) { throw 'Another WINBRAT verification run is active.' }
    $remoteClock = (& $BratVerify -Action state | Out-String).Trim() | ConvertFrom-Json
    $RemoteVerificationStartedUtc = [DateTimeOffset]::ParseExact(
        [string]$remoteClock.AtUtc,
        'o',
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind)
    $RemoteMutationStarted = $true
    & $BratVerify -Action deploy -Version $Version

    # ColdCycles validates clean -> connected/TUN/tunnel -> held -> clean twice.
    # It also runs fixed HTTPS/UDP probes and sanitized lifecycle classification.
    $CurrentStep = 'ColdCycles'
    & $BratStability -Mode ColdCycles -Version $Version -Cycles $Cycles -RunSinceUtc $RemoteVerificationStartedUtc.ToString('o')
}
catch {
    $RunFailure = $_
}
finally {
    if ($RemoteMutationStarted) {
        try { & $BratStability -Mode Cleanup -RunSinceUtc $RemoteVerificationStartedUtc.ToString('o') }
        catch { $CleanupFailure = $_ }
    }
    if ($GateMutexHeld) { [void]$GateMutex.ReleaseMutex() }
    $GateMutex.Dispose()
}

$status = if ($RunFailure -or $CleanupFailure) { 'FAIL' } else { 'PASS' }
$summary = [ordered]@{
    Status = $status
    Version = $Version
    Target = 'WINBRAT'
    VisualGate = 'PageScreenshotTests+VisualDiffTests'
    ColdCycles = $Cycles
    Evidence = $EvidenceRoot.Substring($Root.Length).TrimStart('\')
    FailedStep = if ($RunFailure) { $CurrentStep } else { $null }
    FailureClass = if ($RunFailure) { $RunFailure.Exception.GetType().Name } else { $null }
    CleanupFailureClass = if ($CleanupFailure) { $CleanupFailure.Exception.GetType().Name } else { $null }
}
Write-Output ($summary | ConvertTo-Json -Compress)

if ($CleanupFailure) { throw 'Post-ship cleanup failed; WINBRAT state requires inspection.' }
if ($RunFailure) { throw 'Post-ship verification failed; inspect the sanitized evidence artifacts.' }
