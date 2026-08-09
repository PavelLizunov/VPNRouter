# Source-build the fixed browser probe. The archive remains local until its
# SHA-256 is added to the source-reviewed WINBRAT allowlist.
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$Output = [IO.Path]::GetFullPath((Join-Path $Root 'artifacts\brat-browser-probe-payload'))
$Publish = Join-Path $Output 'publish'
$Archive = Join-Path $Output 'WinbratBrowserProbe-win-x64.zip'
$Project = Join-Path $Root 'VPNRouter.Tools\WinbratBrowserProbe\VPNRouter.Tools.WinbratBrowserProbe.csproj'

$rootPrefix = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $Output.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to clean a browser payload path outside the checkout.'
}
if (-not (Test-Path -LiteralPath $Project -PathType Leaf)) {
    throw 'Fixed browser probe project is missing.'
}

if (Test-Path -LiteralPath $Output) {
    Remove-Item -LiteralPath $Output -Recurse -Force
}
New-Item -ItemType Directory -Path $Publish -Force | Out-Null

dotnet publish $Project -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:DebugType=None -o $Publish
if ($LASTEXITCODE) { throw 'Fixed WINBRAT browser probe build failed.' }

Compress-Archive -Path (Join-Path $Publish '*') -DestinationPath $Archive -Force
(Get-FileHash -Algorithm SHA256 $Archive).Hash.ToLowerInvariant() |
    Set-Content -LiteralPath "$Archive.sha256" -NoNewline
Write-Output 'Fixed browser probe built; remote use still requires a source-reviewed hash allowlist.'
