#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$DataDir = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'VPNRouterBusiness'),
    [string]$DestinationDir = [Environment]::GetFolderPath('DesktopDirectory'),
    [string]$LegacyDataDir = (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'VPNRouter'),
    [switch]$NoOpen
)
$ErrorActionPreference = 'Stop'

# This fallback cannot load the application's .NET 10 redactor into Windows
# PowerShell 5.1. Keep its input smaller: logs only, never configuration files.
function Protect-SupportText([string]$Text) {
    $timeout = [TimeSpan]::FromSeconds(2)
    $options = [Text.RegularExpressions.RegexOptions]::IgnoreCase
    $secretKey = '[a-z0-9_-]*(?:pass(?:word|wd)?|secret|token|credential|authorization|auth|uuid|short[_-]?id|sid|private[_-]?key|api[_-]?key|psk|seed)[a-z0-9_-]*'
    # Mask quoted values across line breaks, YAML blocks, and incomplete values.
    # For unquoted fields discard the rest of that line rather than guessing its end.
    $pattern = '(?is)["'']?\b' + $secretKey + '\b["'']?\s*[=:]\s*(?:"(?:\\.|[^"\\])*(?:"|\z)|''(?:''''|[^''])*(?:''|\z)|[|>][-+]?[^\r\n]*(?:\r?\n[ \t]+[^\r\n]*)*|[^\r\n]+|(?:\r?\n[ \t]+[^\r\n]*)*)'
    $value = [regex]::Replace($Text, $pattern, '[secret redacted]', $options, $timeout)
    $value = [regex]::Replace($value, '(?is)-----BEGIN [^\r\n]*PRIVATE KEY-----.*?(?:-----END [^\r\n]*PRIVATE KEY-----|\z)', '[private key redacted]', $options, $timeout)
    $value = [regex]::Replace($value, '\b[A-Za-z][A-Za-z0-9+.-]*://[^\s"''<>]+', '[URL redacted]', $options, $timeout)
    $value = [regex]::Replace($value, '\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b', '[UUID redacted]', $options, $timeout)
    $value = [regex]::Replace($value, '\b[A-Za-z0-9+/_-]{32,}={0,2}', '[opaque value redacted]', $options, $timeout)
    $value = [regex]::Replace($value, '[A-Z]:[\\/]Users[\\/][^\\/\r\n]+', 'C:\Users\[user]', $options, $timeout)
    return $value
}

function Test-SupportInputPath([string]$Path) {
    $item = [IO.FileInfo]::new([IO.Path]::GetFullPath($Path))
    while ($null -ne $item) {
        if (([IO.File]::GetAttributes($item.FullName) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
        if ($item -is [IO.FileInfo]) { $item = $item.Directory } else { $item = $item.Parent }
    }
    return $true
}

function Read-SupportTail([string]$Path) {
    $limit = 1MB
    $stream = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    try {
        $truncated = $stream.Length -gt $limit
        if ($truncated) { [void]$stream.Seek(-$limit, 'End') }
        $buffer = New-Object byte[] ([int][Math]::Min($stream.Length, $limit))
        $count = 0
        while ($count -lt $buffer.Length) {
            $read = $stream.Read($buffer, $count, $buffer.Length - $count)
            if ($read -eq 0) { break }
            $count += $read
        }
        $start = 0
        if ($truncated) {
            while ($start -lt [Math]::Min(3, $count) -and ($buffer[$start] -band 0xC0) -eq 0x80) { $start++ }
        }
        $value = [Text.UTF8Encoding]::new($false, $true).GetString($buffer, $start, $count - $start)
        if ($value.IndexOf([char]0) -ge 0) { return '[non-UTF8 log omitted]' }
        if ($truncated) {
            # A tail can start inside a multiline credential. Begin at a complete
            # new timestamped event, or omit the tail if no boundary is available.
            $boundary = [regex]::Match($value, '(?m)^(?:\[?(?:\d{4}-\d{2}-\d{2}[ T]|\d{2}:\d{2}:\d{2})|[+-]\d{4} \d{4}-\d{2}-\d{2})')
            if (!$boundary.Success) { return '[oversized log entry omitted]' }
            $value = "[earlier events omitted]`r`n" + $value.Substring($boundary.Index)
        }
        return Protect-SupportText $value
    } finally { $stream.Dispose() }
}

function New-SupportBundle([string]$Source, [string]$Destination, [string]$LegacySource) {
    if ([string]::IsNullOrWhiteSpace($Destination)) { throw 'Choose an output folder.' }
    $warnings = [Collections.Generic.List[string]]::new()
    $manifest = [Collections.Generic.List[object]]::new()
    $stage = Join-Path ([IO.Path]::GetTempPath()) ('vpnrouter-business-support-' + [guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($stage)
    try {
        $specs = @(
            @{ Root = $Source; Folder = 'logs'; Pattern = '^vpnrouter(?:\d{8}(?:_\d+)?)?\.log$'; Prefix = 'app'; Count = 5 },
            @{ Root = $Source; Folder = 'logs'; Pattern = '^singbox(?:\.old)?\.log$'; Prefix = 'core'; Count = 2 },
            @{ Root = $Source; Folder = 'logs'; Pattern = '^vpnrouter-launch-error\.log$'; Prefix = 'launch'; Count = 1 },
            @{ Root = $Source; Folder = 'crashes'; Pattern = '^crash-\d{8}-\d{6}(?:-\d{3})?\.txt$'; Prefix = 'crash'; Count = 5 },
            # The first pilot wrote failed UAC relaunches to this legacy location.
            @{ Root = $LegacySource; Folder = 'logs'; Pattern = '^vpnrouter-launch-error\.log$'; Prefix = 'legacy-launch'; Count = 1 }
        )
        foreach ($spec in $specs) {
            try {
                $folder = Join-Path $spec.Root $spec.Folder
                if (!(Test-Path -LiteralPath $folder -PathType Container)) {
                    $warnings.Add($spec.Prefix + ': folder missing; app may never have started.')
                    continue
                }
                if (!(Test-SupportInputPath $folder)) { throw 'Linked input folder refused.' }
                $files = @(Get-ChildItem -LiteralPath $folder -File |
                    Where-Object { $_.Name -match $spec.Pattern } |
                    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First $spec.Count)
                if ($files.Count -eq 0) { $warnings.Add($spec.Prefix + ': no matching files.') }
                $index = 0
                foreach ($file in $files) {
                    $index++
                    try {
                        if (!(Test-SupportInputPath $file.FullName)) { throw 'Linked input file refused.' }
                        $name = '{0}-{1:D2}.txt' -f $spec.Prefix, $index
                        $content = Read-SupportTail $file.FullName
                        [IO.File]::WriteAllText((Join-Path $stage $name), $content, [Text.Encoding]::UTF8)
                        $manifest.Add([ordered]@{ entry = $name; modifiedUtc = $file.LastWriteTimeUtc.ToString('o'); sourceBytes = $file.Length })
                    } catch { $warnings.Add($spec.Prefix + ': one file omitted (unreadable or unsafe).') }
                }
            } catch { $warnings.Add($spec.Prefix + ': input unavailable or linked; omitted.') }
        }
        $summary = [ordered]@{
            format = 'vpnrouter-business-support/1'
            generatedUtc = [DateTime]::UtcNow.ToString('o')
            os = [Environment]::OSVersion.VersionString
            is64Bit = [Environment]::Is64BitOperatingSystem
            powershell = $PSVersionTable.PSVersion.ToString()
            collection = 'offline logs only; VPN state was not queried or changed'
            entries = @($manifest.ToArray())
            warnings = @($warnings.ToArray())
        }
        [IO.File]::WriteAllText((Join-Path $stage 'summary.json'), ($summary | ConvertTo-Json -Depth 5), [Text.Encoding]::UTF8)
        [IO.File]::WriteAllText((Join-Path $stage 'README.txt'), @'
VPNRouter Business support report
Generated locally without starting the application or the VPN. Nothing uploaded.
Contains bounded app/core/launch log tails and crash reports, with automatic
secret masking. No raw configuration, subscription files or binaries are copied.
Masking is best effort: review the ZIP before sending it privately to your admin.
Logs may contain visited domains, IP addresses and application names.
Missing/unreadable inputs are listed in summary.json. This is not a VPN health test.
'@, [Text.Encoding]::UTF8)
        [void][IO.Directory]::CreateDirectory($Destination)
        $zip = Join-Path $Destination ('VPNRouterBusiness-diagnostics-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.zip')
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
        return $zip
    } finally { [IO.Directory]::Delete($stage, $true) }
}

if ($MyInvocation.InvocationName -eq '.') { return }
try {
    $zip = New-SupportBundle $DataDir $DestinationDir $LegacyDataDir
    Write-Host "Saved diagnostics: $zip"
    Write-Host 'Review the ZIP, then send it privately to your administrator.'
    Write-Host 'See summary.json for missing files. If access was denied, run as the same Windows user with administrator rights.'
    if (!$NoOpen) {
        $explorer = Join-Path ([Environment]::GetFolderPath('Windows')) 'explorer.exe'
        try { Start-Process -FilePath $explorer -ArgumentList ('/select,"' + $zip + '"') -ErrorAction Stop } catch { }
    }
    exit 0
} catch {
    Write-Host 'Could not save diagnostics. Choose a writable output folder and try again.'
    exit 1
}
