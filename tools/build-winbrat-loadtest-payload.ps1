# Source-build the one fixed WINBRAT client payload. The resulting archive is
# deliberately ignored; a separate source-reviewed hash allowlist is required
# before brat-verify can ever copy or execute it remotely.
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path $PSScriptRoot -Parent
$Output = Join-Path $Root 'artifacts\brat-loadtest-payload'
$Publish = Join-Path $Output 'publish'
$Archive = Join-Path $Output 'WinbratLoadGen-win-x64.zip'
if (Test-Path $Output) { Remove-Item -LiteralPath $Output -Recurse -Force }
New-Item -ItemType Directory -Path $Publish -Force | Out-Null

dotnet publish (Join-Path $Root 'VPNRouter.Tools\WinbratLoadGen\VPNRouter.Tools.WinbratLoadGen.csproj') `
    -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=None -o $Publish
if ($LASTEXITCODE) { throw 'Fixed WINBRAT load payload build failed.' }

Compress-Archive -Path (Join-Path $Publish '*') -DestinationPath $Archive -Force
(Get-FileHash -Algorithm SHA256 $Archive).Hash.ToLower() | Set-Content -LiteralPath "$Archive.sha256" -NoNewline
Write-Output 'Fixed payload built; add its SHA-256 through a source-reviewed allowlist change before remote use.'
