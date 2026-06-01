#requires -Version 5.1
<#
.SYNOPSIS
  Local signed Android release build (the Android analogue of build.ps1).

.DESCRIPTION
  GitHub-hosted Android CI is upstream-blocked: .NET 10 withdrew the host Mono
  runtime pack for EVERY host OS (NU1102), so a clean runner can't restore the
  Android project. This Windows dev VM builds only because of a warm obj/ NuGet
  restore cache. So — exactly like the Windows desktop (build.ps1) — Android
  releases are built locally here and uploaded to the GitHub release. The
  in-app updater (AndroidApp.AutoUpdate.cs / SideloadSource) then delivers the
  APK to installed users, and F-Droid (if configured) mirrors it.

  Steps: provision libbox.aar -> signed `dotnet publish` with a monotonic
  versionCode derived from -Version -> name VPNRouter-v<Version>-android.apk +
  .sha256 -> optional upload to release v<Version>.

.NOTES
  SIGNING — you provide the keystore; this script NEVER sees the literal
  password. The password is passed to MSBuild via its `env:` reference form, so
  it is read by MSBuild directly from the environment, not from the command line
  or this script. Set before running:

    $env:ANDROID_KEYSTORE_PATH = "C:\path\to\vpnrouter.keystore"   # or pass -KeyStore
    $env:ANDROID_KEYSTORE_PASS = "<keystore password>"
    # optional, only if the key password differs from the store password:
    $env:ANDROID_KEY_PASS = "<key password>"

  Key alias is "vpnrouter" (from VPNRouter.Android.csproj). Keep the keystore
  backed up offline — losing it means installed users can never update again
  (a new signature = a new, separate app).

.EXAMPLE
  $env:ANDROID_KEYSTORE_PATH = "D:\keys\vpnrouter.keystore"
  $env:ANDROID_KEYSTORE_PASS = "..."
  powershell -ExecutionPolicy Bypass -File build-android.ps1 -Version 2.38.3 -Upload
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$KeyStore = $env:ANDROID_KEYSTORE_PATH,
    [switch]$Upload
)

$ErrorActionPreference = "Stop"
$repo       = "PavelLizunov/VPNRouter"
$proj       = "VPNRouter.Android/VPNRouter.Android.csproj"
$libboxTag  = "tooling-libbox-singbox-1.13.10"
$libboxPath = "VPNRouter.Android/Lib/libbox.aar"

# --- preflight: keystore file + password env var ---
if (-not $KeyStore -or -not (Test-Path $KeyStore)) {
    throw "Keystore not found. Set `$env:ANDROID_KEYSTORE_PATH (or pass -KeyStore) to your vpnrouter.keystore."
}
if (-not $env:ANDROID_KEYSTORE_PASS) {
    throw "Set `$env:ANDROID_KEYSTORE_PASS to the keystore password (read by MSBuild via env: ref; never echoed)."
}
$ksAbs      = (Resolve-Path $KeyStore).Path
$keyPassRef = if ($env:ANDROID_KEY_PASS) { "env:ANDROID_KEY_PASS" } else { "env:ANDROID_KEYSTORE_PASS" }

# --- provision libbox.aar (sing-box gomobile binding) if missing ---
if (-not (Test-Path $libboxPath)) {
    Write-Host "Provisioning libbox.aar from $libboxTag ..."
    New-Item -ItemType Directory -Force (Split-Path $libboxPath) | Out-Null
    gh release download $libboxTag --repo $repo --pattern "libbox.aar" --output $libboxPath
    if ($LASTEXITCODE -ne 0) { throw "Failed to fetch libbox.aar from $libboxTag." }
}
if ((Get-Item $libboxPath).Length -lt 1000000) { throw "libbox.aar looks too small / corrupt." }

# --- build signed release APK (versionCode derives from -Version in the csproj) ---
Write-Host "Building signed Android APK for v$Version ..."
dotnet publish $proj -c Release `
    -p:VpnRouterVersion=$Version `
    -p:AndroidKeyStore=true `
    -p:AndroidSigningKeyStore="$ksAbs" `
    -p:AndroidSigningKeyAlias=vpnrouter `
    -p:AndroidSigningStorePass=env:ANDROID_KEYSTORE_PASS `
    -p:AndroidSigningKeyPass=$keyPassRef
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# --- locate the signed APK, give it the release name + sha256 sidecar ---
$built = Get-ChildItem "VPNRouter.Android/bin/Release" -Recurse -Filter "com.ninitux.vpnrouter-Signed.apk" -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $built) { throw "Signed APK (com.ninitux.vpnrouter-Signed.apk) not found under bin/Release." }

$outDir = "publish/android"
New-Item -ItemType Directory -Force $outDir | Out-Null
$apkName = "VPNRouter-v$Version-android.apk"
$apk     = Join-Path $outDir $apkName
Copy-Item $built.FullName $apk -Force
$sha = (Get-FileHash -Algorithm SHA256 $apk).Hash.ToLower()
"$sha  $apkName" | Out-File "$apk.sha256" -Encoding ascii
$sizeMb = [math]::Round((Get-Item $apk).Length / 1MB, 1)
Write-Host "APK    : $apk ($sizeMb MB)"
Write-Host "SHA256 : $sha"

# --- optional: attach to the GitHub release ---
if ($Upload) {
    Write-Host "Uploading $apkName + .sha256 to release v$Version ..."
    gh release upload "v$Version" $apk "$apk.sha256" --repo $repo --clobber
    if ($LASTEXITCODE -ne 0) { throw "gh release upload failed (is release v$Version published?)." }
    Write-Host "Uploaded to https://github.com/$repo/releases/tag/v$Version"
}
Write-Host "Done."
