# Requires (all already on this box as of 2026-06-20):
[CmdletBinding()]
param(
    [string]$SingBoxVer = "v1.14.0-alpha.24",
    [string]$GomobileForkVer = "v0.1.12",
    [string]$BuildDir = "C:\tmp\android-build"
)
$ErrorActionPreference = "Stop"

$GoRoot   = "$env:USERPROFILE\sdk\go1.25.9"
$GoBin    = "$GoRoot\bin"
$GoPathBin = "$env:USERPROFILE\go\bin"           # GOBIN: gomobile/gobind land here
$env:PATH = "$GoBin;$GoPathBin;$env:PATH"
$env:GOTOOLCHAIN = "local"                        # CRITICAL: do not auto-upgrade to 1.26
$env:GOROOT = $GoRoot
$env:ANDROID_HOME      = "$env:LOCALAPPDATA\Android\Sdk"
$env:ANDROID_SDK_ROOT  = $env:ANDROID_HOME
$env:ANDROID_NDK_HOME  = "$env:LOCALAPPDATA\Android\Sdk\ndk\28.0.13004108"
if (-not $env:JAVA_HOME -or -not (Test-Path "$env:JAVA_HOME\bin\java.exe")) {
    $env:JAVA_HOME = "C:\Program Files\Android\Android Studio\jbr"
}
$env:PATH = "$env:JAVA_HOME\bin;$env:PATH"

$SingBoxDir = "$BuildDir\sing-box"
$OutDir     = "$BuildDir\libbox-out"
$Tags = "with_gvisor,with_quic,with_utls,with_wireguard,with_clash_api,badlinkname,tfogo_checklinkname0"

Write-Host "== toolchain ==" -ForegroundColor Cyan
& "$GoBin\go.exe" version
Write-Host "GOTOOLCHAIN=$env:GOTOOLCHAIN  NDK=$env:ANDROID_NDK_HOME"
Write-Host "JAVA_HOME=$env:JAVA_HOME"

Write-Host "== install sagernet gomobile/gobind fork $GomobileForkVer ==" -ForegroundColor Cyan
& "$GoBin\go.exe" install "github.com/sagernet/gomobile/cmd/gomobile@$GomobileForkVer"
if ($LASTEXITCODE -ne 0) { throw "go install gomobile failed ($LASTEXITCODE)" }
& "$GoBin\go.exe" install "github.com/sagernet/gomobile/cmd/gobind@$GomobileForkVer"
if ($LASTEXITCODE -ne 0) { throw "go install gobind failed ($LASTEXITCODE)" }

Write-Host "== sing-box source ($SingBoxVer) ==" -ForegroundColor Cyan
New-Item -ItemType Directory -Force $BuildDir | Out-Null
if (Test-Path "$SingBoxDir\.git") {
    git -C $SingBoxDir fetch --tags --quiet
} else {
    git clone --quiet https://github.com/SagerNet/sing-box.git $SingBoxDir
}
git -C $SingBoxDir checkout --quiet $SingBoxVer
$actual = (git -C $SingBoxDir describe --tags 2>$null)
Write-Host "sing-box @ $actual"

New-Item -ItemType Directory -Force $OutDir | Out-Null
Push-Location "$SingBoxDir\experimental\libbox"
try {
    Write-Host "== gomobile bind (curated tags, ~3-8 min) ==" -ForegroundColor Cyan
    & "$GoPathBin\gomobile.exe" bind -v `
        "-target=android/arm64" `
        "-androidapi=26" `
        "-javapkg=io.nekohasekai" `
        "-ldflags=-s -w -checklinkname=0 -X github.com/sagernet/sing-box/constant.Version=$SingBoxVer" `
        "-tags=$Tags" `
        "-o=$OutDir\libbox.aar" `
        ./
    if ($LASTEXITCODE -ne 0) { throw "gomobile bind failed ($LASTEXITCODE)" }
} finally {
    Pop-Location
}

if (-not (Test-Path "$OutDir\libbox.aar")) { throw "libbox.aar not produced" }
$size = (Get-Item "$OutDir\libbox.aar").Length
$sha  = (Get-FileHash "$OutDir\libbox.aar" -Algorithm SHA256).Hash
Write-Host ("== OK: libbox.aar {0:N0} bytes  sha256={1} ==" -f $size, $sha) -ForegroundColor Green
Write-Host "Next: copy to VPNRouter.Android\Lib\libbox.aar"
