using System.IO.Compression;
using VPNRouter.Core.Models;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.Core.Services;

public partial class UpdateChecker
{
    Task<string> IDesktopInstaller.DownloadAndStageAsync(
        UpdateSourceInfo info,
        IProgress<UpdateSources.DownloadProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(info);

        var legacy = new UpdateInfo
        {
            CurrentVersion = _currentVersion,
            LatestVersion = info.Version,
            DownloadUrl = info.DownloadUrl,
            SizeBytes = info.AssetSize,
            ReleaseNotes = info.ReleaseNotes,
            HtmlUrl = info.ReleaseUrl,
            IsNewer = true,
            HasLiteUpdate = false,
            FullChecksumUrl = null,
            FullChecksumSha256 = info.AssetSha256,
        };

        Action<int>? handler = null;
        if (progress != null)
        {
            handler = pct => progress.Report(new UpdateSources.DownloadProgress(
                BytesReceived: info.AssetSize > 0 ? info.AssetSize * pct / 100 : 0,
                TotalBytes: info.AssetSize > 0 ? info.AssetSize : null));
            DownloadProgress += handler;
        }
        return Run();

        async Task<string> Run()
        {
            try
            {
                return await DownloadAndStageAsync(legacy, ct).ConfigureAwait(false);
            }
            finally
            {
                if (handler != null)
                    DownloadProgress -= handler;
            }
        }
    }

    Task<bool> IDesktopInstaller.ApplyStagedAsync(
        UpdateSourceInfo info,
        string stagedPath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (string.IsNullOrWhiteSpace(stagedPath))
            throw new ArgumentException("Staged path must be non-empty.", nameof(stagedPath));
        var isDowngrade = IsVersionDowngrade(_currentVersion, info.Version);
        if (isDowngrade)
        {
            var backup = BackupConfigForDowngrade(info.Version);
            DeleteInstallReceiptForDowngrade();
            StatusChanged?.Invoke(backup == null
                ? Strings.DowngradeNoConfigBackup
                : string.Format(Strings.DowngradeConfigBackupCreated, backup));
        }
        ApplyUpdate(
            stagedPath,
            writeInstallReceipt: ShouldWriteInstallReceipt(_currentVersion, info.Version));
        return Task.FromResult(true);
    }

    public async Task<string> DownloadAndStageAsync(UpdateInfo info, CancellationToken ct = default)
    {
        var useLite = info.HasLiteUpdate && !string.IsNullOrEmpty(info.LiteDownloadUrl);
        var downloadUrl = useLite ? info.LiteDownloadUrl! : info.DownloadUrl;
        var expectedSize = useLite ? info.LiteSizeBytes : info.SizeBytes;
        var checksumUrl = useLite ? info.LiteChecksumUrl : info.FullChecksumUrl;

        if (string.IsNullOrWhiteSpace(downloadUrl) ||
            !Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri) ||
            (downloadUri.Scheme != Uri.UriSchemeHttp && downloadUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"Invalid or non-http(s) update download URL: '{downloadUrl}'.");
        }

        Uri? checksumUri = null;
        if (!string.IsNullOrEmpty(checksumUrl))
        {
            if (!Uri.TryCreate(checksumUrl, UriKind.Absolute, out checksumUri) ||
                (checksumUri.Scheme != Uri.UriSchemeHttp && checksumUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException($"Invalid or non-http(s) update checksum URL: '{checksumUrl}'.");
            }
        }

        var label = useLite ? "lite update" : "full update";

        StatusChanged?.Invoke($"Downloading {label}...");

        TrySweepStaleStagingDirs();
        Directory.CreateDirectory(_stagingDir);
        var stagingDir = Path.Combine(
            _stagingDir, Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(stagingDir);

        string downloadExt =
            downloadUrl.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz"
            : downloadUrl.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)  ? ".tgz"
            : ".zip";
        var zipPath = Path.Combine(stagingDir, $"VPNRouter-v{info.LatestVersion}{downloadExt}");

        long totalBytes;
        await using (var response = await _http.SendStreamingAsync(
            new HttpRequest(HttpMethod.Get, downloadUri),
            ct).ConfigureAwait(false))
        {
            if (!response.IsSuccess())
                throw new HttpRequestException(
                    $"HTTP {response.StatusCode} downloading update from {downloadUrl}");

            totalBytes = response.ContentLength ?? expectedSize;

            using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await response.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                totalRead += bytesRead;

                if (totalBytes > 0)
                    DownloadProgress?.Invoke((int)(totalRead * 100 / totalBytes));
            }

            fileStream.Close();
        }

        var downloadedSize = new FileInfo(zipPath).Length;
        if (expectedSize > 0 && downloadedSize < expectedSize * 0.9)
            throw new InvalidOperationException(
                $"Downloaded file is too small ({downloadedSize / 1024 / 1024} MB vs expected {expectedSize / 1024 / 1024} MB). Download may be corrupted.");

        string? expectedSha = info.FullChecksumSha256;
        if (string.IsNullOrEmpty(expectedSha) && checksumUri != null)
        {
            var shaResponse = await _http.SendAsync(
                new HttpRequest(HttpMethod.Get, checksumUri),
                ct);
            if (!shaResponse.IsSuccess())
                throw new InvalidOperationException(
                    $"Checksum download failed: HTTP {shaResponse.StatusCode}");
            expectedSha = shaResponse.AsString().Trim().ToLowerInvariant();

            if (expectedSha.Contains(' '))
                expectedSha = expectedSha.Split(' ', 2)[0].Trim();
        }

        if (string.IsNullOrEmpty(expectedSha))
        {
            try { File.Delete(zipPath); } catch { }
            throw new InvalidOperationException(
                "Update checksum is missing — refusing to extract an unverified package.");
        }

        StatusChanged?.Invoke("Verifying checksum...");

        if (expectedSha.Length != 64)
            throw new InvalidOperationException(
                $"Checksum is not a valid SHA256 (got {expectedSha.Length} hex chars, expected 64).");

        string actualSha;
        await using (var fs = File.OpenRead(zipPath))
        {
            var hashBytes = await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct);
            actualSha = Convert.ToHexStringLower(hashBytes);
        }

        if (!string.Equals(actualSha, expectedSha, StringComparison.Ordinal))
        {
            try { File.Delete(zipPath); } catch { }
            throw new InvalidOperationException(
                $"Checksum mismatch — download is corrupted.\r\n" +
                $"Expected: {expectedSha}\r\n" +
                $"Got:      {actualSha}\r\n" +
                $"File has been deleted. Click 'Update' again to retry.");
        }

        StatusChanged?.Invoke("Extracting update...");

        var extractDir = Path.Combine(stagingDir, "extracted");
        if (zipPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            zipPath.EndsWith(".tgz",    StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(extractDir);
            var tarArgs = new[] { "-xzf", zipPath, "-C", extractDir };
            var (tarExit, tarOut, tarErr) = RunWithCapture("tar", tarArgs, 120_000);
            if (tarExit != 0)
            {
                if (tarExit == -1)
                    throw new InvalidOperationException(
                        "tar extraction timed out after 120 s — archive may be corrupt. " +
                        $"Source: {zipPath}");
                throw new InvalidOperationException(
                    $"tar extraction failed (exit {tarExit}): {Truncate(tarErr, 200)}".Trim());
            }
        }
        else
        {
            ZipFile.ExtractToDirectory(zipPath, extractDir);
        }

        ValidateExtractedContent(extractDir);

        StatusChanged?.Invoke("Update ready to apply.");
        return extractDir;
    }

    public void CleanupStagingDir()
    {
        TrySweepStaleStagingDirs();

        try
        {
            var appDir = AppContext.BaseDirectory;
            foreach (var bak in Directory.GetFiles(appDir, "*.bak", SearchOption.AllDirectories))
            {
                try { File.Delete(bak); } catch { }
            }
        }
        catch { }
    }

    private void TrySweepStaleStagingDirs()
    {
        try
        {
            if (!Directory.Exists(_stagingDir)) return;
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(2);
            foreach (var dir in Directory.GetDirectories(_stagingDir))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                        Directory.Delete(dir, true);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void ValidateExtractedContent(string extractDir)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (Directory.GetDirectories(extractDir, "*.app", SearchOption.TopDirectoryOnly).Length > 0)
                return;
            if (Directory.Exists(Path.Combine(extractDir, "Contents")))
                return;
            if (File.Exists(Path.Combine(extractDir, "VPNRouter.Mac.dll")))
                return;
            throw new InvalidOperationException(
                "Invalid update package: no .app bundle or VPNRouter.Mac.dll found.");
        }

        if (OperatingSystem.IsLinux())
        {
            var linuxSubDir = Path.Combine(extractDir, "VPNRouter");
            if (File.Exists(Path.Combine(linuxSubDir, "VPNRouter.App")) ||
                File.Exists(Path.Combine(linuxSubDir, "VPNRouter.App.dll")))
                return;
            if (File.Exists(Path.Combine(extractDir, "VPNRouter.App")) ||
                File.Exists(Path.Combine(extractDir, "VPNRouter.App.dll")))
                return;
            throw new InvalidOperationException(
                "Invalid update package: VPNRouter.App not found in extracted tarball.");
        }

        var checkDir = extractDir;
        var appSubDir = Path.Combine(extractDir, "app");
        if (Directory.Exists(appSubDir) &&
            (File.Exists(Path.Combine(appSubDir, "VPNRouter.GUI.exe")) ||
             File.Exists(Path.Combine(appSubDir, "VPNRouter.GUI.dll"))))
        {
            checkDir = appSubDir;
        }

        if (!File.Exists(Path.Combine(checkDir, "VPNRouter.GUI.exe")) &&
            !File.Exists(Path.Combine(checkDir, "VPNRouter.GUI.dll")))
            throw new InvalidOperationException(
                "Invalid update package: VPNRouter.GUI.exe/dll not found.");
    }
}
