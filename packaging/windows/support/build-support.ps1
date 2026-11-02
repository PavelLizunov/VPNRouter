#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SettingsFile,
    [Parameter(Mandatory)][string]$OutputDir
)
$ErrorActionPreference = 'Stop'
try { $settingsObject = Get-Content -LiteralPath $SettingsFile -Raw | ConvertFrom-Json }
catch { throw 'Private support settings could not be read.' }
$Endpoint = [string]$settingsObject.endpoint
$uri = $null
if (![Uri]::TryCreate($Endpoint, [UriKind]::Absolute, [ref]$uri) -or
    $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Fragment) { throw 'Valid HTTPS endpoint required.' }
if ([string]$settingsObject.token -notmatch '^[A-Za-z0-9_-]{32,128}$') { throw 'Individual upload identity required.' }
$compiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Windows Framework compiler missing; do not install an SDK automatically.' }
[void][IO.Directory]::CreateDirectory($OutputDir)
$settings = Join-Path $OutputDir ('support-settings-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($settings, (@{ endpoint = $uri.AbsoluteUri; token = [string]$settingsObject.token } | ConvertTo-Json), [Text.Encoding]::UTF8)
    $exe = Join-Path $OutputDir 'VPNRouterBusiness-Diagnostics.exe'
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /langversion:5 /codepage:65001 `
        /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll `
        /reference:System.Drawing.dll /reference:System.Web.Extensions.dll `
        "/resource:$PSScriptRoot\Collect-VPNRouterBusiness-Diagnostics.ps1,Collect-VPNRouterBusiness-Diagnostics.ps1" `
        "/resource:$PSScriptRoot\Send-VPNRouterBusiness-Diagnostics.ps1,Send-VPNRouterBusiness-Diagnostics.ps1" `
        "/resource:$settings,support-settings.json" "/out:$exe" "$PSScriptRoot\SupportApp.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Support executable build failed.' }
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  VPNRouterBusiness-Diagnostics.exe" | Set-Content "$exe.sha256" -Encoding ascii
} finally { Remove-Item -LiteralPath $settings -Force -ErrorAction SilentlyContinue }
