using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.Core.Services;

public partial class UpdateChecker
{
    private readonly ConcurrentDictionary<string, BusinessStagedIdentity> _businessStages =
        new(StringComparer.OrdinalIgnoreCase);

    private void RememberBusinessStage(string directory, string version, string sha, long size, string archivePath)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(sha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Business archive changed during staging.");
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var files = zip.Entries.Where(entry => !entry.FullName.EndsWith('/')).Select(entry =>
        {
            using var data = entry.Open();
            return new BusinessUpdateFile(entry.FullName[4..], Convert.ToHexString(SHA256.HashData(data)));
        }).OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        foreach (var key in _businessStages.Keys)
            if (!Directory.Exists(key)) _businessStages.TryRemove(key, out _);
        _businessStages[Path.GetFullPath(directory)] = new(version, sha, size, JsonSerializer.SerializeToUtf8Bytes(files));
    }

    internal byte[] ValidateBusinessStageForApply(string directory, UpdateSourceInfo info)
    {
        if (!_businessStages.TryGetValue(Path.GetFullPath(directory), out var identity) ||
            identity.Version != info.Version || identity.Size != info.AssetSize ||
            !string.Equals(identity.Sha256, info.AssetSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Business update must be downloaded and verified in this application session.");
        var app = BusinessUpdatePackage.ValidateStaged(directory, info.Version);
        if (!CreateBusinessFileManifest(app).AsSpan().SequenceEqual(identity.Manifest))
            throw new InvalidOperationException("Business update files changed after verification.");
        return identity.Manifest.ToArray();
    }

    // Tests replace only the process-launch boundary; production uses the Windows helper.
    internal void ApplyBusinessUpdate(string extractedDirectory, UpdateSourceInfo info,
        Action<ProcessStartInfo>? launchHelper = null)
    {
        if (!OperatingSystem.IsWindows() && launchHelper == null)
            throw new PlatformNotSupportedException("Business desktop updates require Windows.");
        var manifest = ValidateBusinessStageForApply(extractedDirectory, info);
        if (IsVersionDowngrade(_currentVersion, info.Version))
        {
            var backup = BackupConfigForDowngrade(info.Version);
            DeleteInstallReceiptForDowngrade();
            StatusChanged?.Invoke(backup == null
                ? Strings.DowngradeNoConfigBackup
                : string.Format(Strings.DowngradeConfigBackupCreated, backup));
        }
        var source = Path.Combine(Path.GetFullPath(extractedDirectory), "app");
        var destination = Path.GetFullPath(AppContext.BaseDirectory);
        var manifestPath = Path.Combine(extractedDirectory, "business-update-files-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(manifestPath, manifest);
        try
        {
            using var current = Process.GetCurrentProcess();
            var script = BuildBusinessApplyScript(source, destination, manifestPath,
                Convert.ToHexString(SHA256.HashData(manifest)),
                Path.Combine(AppPaths.LogsDir, "business-update.log"), current.Id,
                current.StartTime.ToUniversalTime().Ticks, 45_000,
                string.Equals(Environment.GetEnvironmentVariable("VPNROUTER_CI"), "1", StringComparison.Ordinal));
            var startInfo = CreateBusinessHelperStartInfo(script, NeedsBusinessUpdateElevation(destination));
            if (launchHelper != null)
            {
                launchHelper(startInfo);
                return;
            }
            using var helper = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The Business update helper could not start.");
            if (helper.WaitForExit(300) && helper.ExitCode != 0)
                throw new InvalidOperationException("The Business update helper failed before application exit.");
        }
        catch
        {
            try { File.Delete(manifestPath); } catch { }
            throw;
        }
    }

    internal static byte[] CreateBusinessFileManifest(string sourceDirectory)
    {
        var root = Path.GetFullPath(sourceDirectory);
        var entries = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                using var stream = File.OpenRead(path);
                return new BusinessUpdateFile(
                    Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                    Convert.ToHexString(SHA256.HashData(stream)));
            })
            .OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(entries);
    }

    private static bool NeedsBusinessUpdateElevation(string destination)
    {
        var probe = Path.Combine(destination, ".business-write-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            return false;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    internal static ProcessStartInfo CreateBusinessHelperStartInfo(string script, bool elevate)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = elevate ? "runas" : string.Empty,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        info.ArgumentList.Add("-NoLogo");
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return info;
    }

    internal static string BuildBusinessApplyScript(string sourceDirectory, string destinationDirectory,
        string manifestPath, string manifestSha256, string logPath, int parentPid,
        long parentStartedTicks, int waitMilliseconds, bool suppressRelaunch)
    {
        if (parentPid <= 0 || parentStartedTicks < 0 || waitMilliseconds < 0 ||
            waitMilliseconds > 45_000 || manifestSha256.Length != 64 || !manifestSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid Business helper parameters.");
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        var source = Literal(Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar));
        var destination = Literal(Path.GetFullPath(destinationDirectory).TrimEnd(Path.DirectorySeparatorChar));
        return $$"""
            $env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')
            $ErrorActionPreference = 'Stop'
            $source = {{source}}
            $destination = {{destination}}
            $manifestPath = {{Literal(Path.GetFullPath(manifestPath))}}
            $manifestHash = {{Literal(manifestSha256)}}
            $logPath = {{Literal(Path.GetFullPath(logPath))}}
            $temporary = $null
            $phase = 'WAIT_PARENT'
            function Assert-OrdinaryPath([string]$path) {
                $current = $path
                while ($current) {
                    if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
                        if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                            throw 'REPARSE_POINT_REFUSED'
                        }
                    }
                    $current = [IO.Path]::GetDirectoryName($current)
                }
            }
            try {
                $deadline = [DateTime]::UtcNow.AddMilliseconds({{waitMilliseconds}})
                while ($true) {
                    $owner = Get-Process -Id {{parentPid}} -ErrorAction SilentlyContinue
                    if ($null -eq $owner) { break }
                    try { $sameOwner = $owner.StartTime.ToUniversalTime().Ticks -eq {{parentStartedTicks}} }
                    catch { $sameOwner = $false }
                    if (-not $sameOwner) { break }
                    if ([DateTime]::UtcNow -ge $deadline) { throw 'APP_EXIT_TIMEOUT' }
                    Start-Sleep -Milliseconds 100
                }
                $phase = 'VALIDATE_PATHS'
                Assert-OrdinaryPath $source
                Assert-OrdinaryPath $destination
                Assert-OrdinaryPath $manifestPath
                $phase = 'READ_MANIFEST'
                $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
                $phase = 'VERIFY_MANIFEST'
                $hasher = [Security.Cryptography.SHA256]::Create()
                try { $actualManifestHash = [BitConverter]::ToString($hasher.ComputeHash($manifestBytes)).Replace('-', '') }
                finally { $hasher.Dispose() }
                if ($actualManifestHash -ne $manifestHash) {
                    throw 'MANIFEST_CHANGED'
                }
                $phase = 'PARSE_MANIFEST'
                $files = ConvertFrom-Json -InputObject ([Text.Encoding]::UTF8.GetString($manifestBytes))
                if ($files.Count -eq 0) { throw 'EMPTY_MANIFEST' }
                $phase = 'VERIFY_SOURCE'
                foreach ($file in $files) {
                    $relative = [string]$file.Path
                    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\/])\.\.([\/]|$)' -or $relative.Contains(':')) {
                        throw 'INVALID_MANIFEST_PATH'
                    }
                    $from = [IO.Path]::GetFullPath((Join-Path $source $relative))
                    $to = [IO.Path]::GetFullPath((Join-Path $destination $relative))
                    if (-not $from.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
                        -not $to.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                        throw 'INVALID_MANIFEST_PATH'
                    }
                    Assert-OrdinaryPath $from
                    Assert-OrdinaryPath $to
                    if ((Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash -ne $file.Sha256) { throw 'SOURCE_CHANGED' }
                }
                $phase = 'COPY'
                foreach ($file in $files) {
                    $from = Join-Path $source $file.Path
                    $to = Join-Path $destination $file.Path
                    $folder = [IO.Path]::GetDirectoryName($to)
                    Assert-OrdinaryPath $from
                    Assert-OrdinaryPath $to
                    [IO.Directory]::CreateDirectory($folder) | Out-Null
                    $temporary = Join-Path $folder ('.business-copy-' + [Guid]::NewGuid().ToString('N'))
                    [IO.File]::Copy($from, $temporary, $false)
                    if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $file.Sha256) { throw 'COPY_CHANGED' }
                    if ([IO.File]::Exists($to)) {
                        $phase = 'REPLACE'
                        # Preserve a true null string instead of PowerShell's empty-string conversion.
                        [IO.File]::Replace($temporary, $to, [NullString]::Value)
                    } else {
                        $phase = 'MOVE'
                        [IO.File]::Move($temporary, $to)
                    }
                    $temporary = $null
                }
                $phase = 'RELAUNCH'
                {{(suppressRelaunch ? "# Relaunch suppressed for isolated verification." : "Start-Process -FilePath (Join-Path $destination 'VPNRouter.App.exe') -WorkingDirectory $destination | Out-Null")}}
                exit 0
            } catch {
                $exceptionType = $_.Exception.GetType().Name
                $hresult = $_.Exception.HResult.ToString('X8')
                $reason = if ($_.Exception.Message -in @('APP_EXIT_TIMEOUT','MANIFEST_CHANGED','SOURCE_CHANGED','COPY_CHANGED','REPARSE_POINT_REFUSED','INVALID_MANIFEST_PATH','EMPTY_MANIFEST')) {
                    $_.Exception.Message
                } else { 'FILE_COPY_FAILED' }
                try {
                    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($logPath)) | Out-Null
                    [IO.File]::AppendAllText($logPath, [DateTime]::UtcNow.ToString('O') + ' BUSINESS_UPDATE_FAILED ' + $reason + ' PHASE=' + $phase + ' TYPE=' + $exceptionType + ' HRESULT=' + $hresult + [Environment]::NewLine)
                } catch {}
                exit 1
            } finally {
                if ($temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
                Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue
            }
            """;
    }

    private sealed record BusinessUpdateFile(string Path, string Sha256);
    private sealed record BusinessStagedIdentity(string Version, string Sha256, long Size, byte[] Manifest);
}
