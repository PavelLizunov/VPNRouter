#!/usr/bin/env pwsh
# Build side-by-side composites from desktop/ + android/ captures.
# Uses System.Drawing (no ImageMagick dependency) to mirror the
# `magick +append` layout that the original audit ran on a Mac with
# brew imagemagick.
#
# Layout: desktop image on the left, android image on the right, both
# top-aligned, white background. Composite height = max(des.H, and.H).

param(
    [string]$Root = "C:\Project\VPNRouter\parity-audit"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

# Desktop file -> Android file -> Composite name (without ".png")
$pairs = @(
    @{ Desk = "page-simple.png";       Andr = "page-simple.png";              Out = "simple" },
    @{ Desk = "page-subscribe.png";    Andr = "page-subscribe.png";           Out = "subscribe" },
    @{ Desk = "page-servers.png";      Andr = "page-servers.png";             Out = "servers" },
    @{ Desk = "page-network.png";      Andr = "page-network-settings.png";    Out = "network" },
    @{ Desk = "page-applications.png"; Andr = "page-applications-mode.png";   Out = "applications" },
    @{ Desk = "page-tools.png";        Andr = "page-tools-log.png";           Out = "tools" },
    @{ Desk = "page-dpi-bypass.png";   Andr = "page-dpi-bypass-settings.png"; Out = "dpi-bypass" },
    @{ Desk = "page-free-configs.png"; Andr = "page-free-configs.png";        Out = "free-configs" }
)

$desktopDir = Join-Path $Root "desktop"
$androidDir = Join-Path $Root "android"
$compositeDir = Join-Path $Root "composite"
if (-not (Test-Path $compositeDir)) { New-Item -ItemType Directory -Path $compositeDir | Out-Null }

foreach ($p in $pairs) {
    $deskPath = Join-Path $desktopDir $p.Desk
    $andrPath = Join-Path $androidDir $p.Andr
    $outPath  = Join-Path $compositeDir ($p.Out + ".png")

    if (-not (Test-Path $deskPath)) {
        Write-Warning "missing desktop: $($p.Desk) — skipping"
        continue
    }
    if (-not (Test-Path $andrPath)) {
        Write-Warning "missing android: $($p.Andr) — skipping"
        continue
    }

    $desk = [System.Drawing.Bitmap]::FromFile($deskPath)
    $andr = [System.Drawing.Bitmap]::FromFile($andrPath)

    $width  = $desk.Width + $andr.Width
    $height = [Math]::Max($desk.Height, $andr.Height)

    $bmp = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::White)
    $g.DrawImage($desk, 0, 0, $desk.Width, $desk.Height)
    $g.DrawImage($andr, $desk.Width, 0, $andr.Width, $andr.Height)
    $g.Dispose()

    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $desk.Dispose()
    $andr.Dispose()

    $size = (Get-Item $outPath).Length
    Write-Host ("ok:   composite/{0}.png ({1} bytes) [{2}x{3}]" -f $p.Out, $size, $width, $height)
}
Write-Host "DONE"
