$ErrorActionPreference = 'Stop'
try {
    if ($env:COMPUTERNAME -ne 'WINBRAT') { throw 'Identity' }
    $root = 'C:\Temp\vpnrouter-pictograms-20260907'
    $backup = 'C:\ProgramData\VPNRouter-pictograms-20260907-backup-392ffe3d184440f7a4558dfb28cad913'
    $inputPath = 'C:\ProgramData\VPNRouter\config.yaml'
    $outputPath = Join-Path $backup 'config.preview.yaml'
    if (!(Test-Path -LiteralPath $backup -PathType Container)) { throw 'Backup' }
    if (!(Get-Acl -LiteralPath $backup).AreAccessRulesProtected) { throw 'Acl' }
    if ((Get-Item -LiteralPath $backup).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse' }
    $before = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash
    Set-Location $root
    & C:\dotnet\dotnet.exe plans\preview-settings-helper\bin\Debug\net10.0\PreviewSettingsHelper.dll $inputPath $outputPath
    if ($LASTEXITCODE -ne 0) { throw 'Helper' }
    $after = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash
    if ($before -ne $after) { throw 'ChangedInput' }
    & C:\dotnet\dotnet.exe plans\preview-settings-helper\bin\Debug\net10.0\PreviewSettingsHelper.dll --inspect $outputPath
    if ($LASTEXITCODE -ne 0) { throw 'Inspect' }
    Write-Output 'source_unchanged=true protected_preview_exists=true preview_valid=true'
} catch {
    Write-Output 'preview_verification=false'
    exit 1
}
