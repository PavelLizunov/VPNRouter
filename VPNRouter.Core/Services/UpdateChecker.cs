using System.Diagnostics;
using System.IO.Compression;
using Newtonsoft.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public class UpdateChecker
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly UpdateSettings _settings;
    private readonly string _currentVersion;
    private readonly string _stagingDir;

    public event Action<UpdateInfo>? UpdateAvailable;
    public event Action<int>? DownloadProgress;        // 0-100
    public event Action<string>? StatusChanged;

    static UpdateChecker()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "VPNRouter");
    }

    public UpdateChecker(UpdateSettings settings, string currentVersion)
    {
        _settings = settings;
        _currentVersion = currentVersion;
        _stagingDir = Environment.ExpandEnvironmentVariables(
            @"%ProgramData%\VPNRouter\update-staging");
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.GitHubRepo))
            return null;

        if (!Version.TryParse(_currentVersion, out var current))
            return null;

        // Fetch all releases (up to 30) to collect changelogs for skipped versions
        var url = $"https://api.github.com/repos/{_settings.GitHubRepo}/releases?per_page=30";
        var json = await _http.GetStringAsync(url, ct);

        var releases = JsonConvert.DeserializeAnonymousType(json, new[]
        {
            new
            {
                tag_name = "",
                body = "",
                html_url = "",
                draft = false,
                prerelease = false,
                assets = new[] { new { browser_download_url = "", size = 0L, name = "" } }
            }
        });

        if (releases == null || releases.Length == 0)
            return null;

        // Find all non-draft releases newer than current version, sorted newest-first
        // Stable channel: skip pre-releases. Experimental: include all.
        var newerReleases = releases
            .Where(r => !r.draft && (_settings.IsExperimental || !r.prerelease))
            .Select(r => new
            {
                Release = r,
                Tag = r.tag_name.TrimStart('v'),
                Parsed = Version.TryParse(r.tag_name.TrimStart('v'), out var v) ? v : null
            })
            .Where(r => r.Parsed != null && r.Parsed > current)
            .OrderByDescending(r => r.Parsed)
            .ToList();

        if (newerReleases.Count == 0)
            return null;

        var latestRelease = newerReleases[0];

        var asset = latestRelease.Release.assets?.FirstOrDefault(a =>
            a.name.StartsWith("VPNRouter-v", StringComparison.OrdinalIgnoreCase) &&
            a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        if (asset == null) return null;

        // Collect release notes from ALL skipped versions (newest first)
        var allNotes = newerReleases
            .Where(r => !string.IsNullOrWhiteSpace(r.Release.body))
            .Select(r => r.Release.body!.Trim())
            .ToList();

        var combinedNotes = string.Join("\n\n", allNotes);

        var info = new UpdateInfo
        {
            CurrentVersion = _currentVersion,
            LatestVersion = latestRelease.Tag,
            DownloadUrl = asset.browser_download_url,
            ReleaseNotes = combinedNotes,
            HtmlUrl = latestRelease.Release.html_url ?? string.Empty,
            SizeBytes = asset.size,
            IsNewer = true
        };

        UpdateAvailable?.Invoke(info);
        return info;
    }

    public async Task<string> DownloadAndStageAsync(UpdateInfo info, CancellationToken ct = default)
    {
        StatusChanged?.Invoke("Downloading update...");

        if (Directory.Exists(_stagingDir))
            Directory.Delete(_stagingDir, true);
        Directory.CreateDirectory(_stagingDir);

        var zipPath = Path.Combine(_stagingDir, $"VPNRouter-v{info.LatestVersion}.zip");

        using var response = await _http.GetAsync(info.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? info.SizeBytes;

        using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;

            if (totalBytes > 0)
                DownloadProgress?.Invoke((int)(totalRead * 100 / totalBytes));
        }

        fileStream.Close();

        // Validate downloaded file — catch truncated downloads
        var downloadedSize = new FileInfo(zipPath).Length;
        if (info.SizeBytes > 0 && downloadedSize < info.SizeBytes * 0.9)
            throw new InvalidOperationException(
                $"Downloaded file is too small ({downloadedSize / 1024 / 1024} MB vs expected {info.SizeBytes / 1024 / 1024} MB). Download may be corrupted.");

        StatusChanged?.Invoke("Extracting update...");

        var extractDir = Path.Combine(_stagingDir, "extracted");
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        if (!File.Exists(Path.Combine(extractDir, "VPNRouter.GUI.exe")))
            throw new InvalidOperationException("Invalid update package: VPNRouter.GUI.exe not found.");

        StatusChanged?.Invoke("Update ready to apply.");
        return extractDir;
    }

    /// <summary>
    /// Clean up leftover files from a previous update:
    /// - staging directory (downloaded/extracted ZIP)
    /// - .bak files (renamed locked executables during in-process update)
    /// Call on app startup.
    /// </summary>
    public void CleanupStagingDir()
    {
        try
        {
            if (Directory.Exists(_stagingDir))
                Directory.Delete(_stagingDir, true);
        }
        catch { /* Non-critical — will be cleaned on next update */ }

        // Clean up .bak files left from in-process update (renamed locked exes)
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

    /// <summary>
    /// Apply update in-process: copy files directly, rename locked executables.
    /// No external batch script needed — eliminates the chicken-and-egg problem
    /// where old versions had buggy batch scripts that couldn't self-update.
    ///
    /// On Windows, a running .exe can be renamed (but not deleted/overwritten).
    /// So we: rename locked file → copy new file → start new exe → exit.
    /// The .bak files are cleaned up on next startup via CleanupStagingDir().
    /// </summary>
    public void ApplyUpdate(string extractedDir)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd('\\');
        var guiExe = Path.Combine(appDir, "VPNRouter.GUI.exe");
        int copied = 0, renamed = 0;

        foreach (var srcFile in Directory.GetFiles(extractedDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(extractedDir, srcFile);
            var destPath = Path.Combine(appDir, relativePath);

            // Ensure target subdirectory exists (e.g. profiles\, service\)
            var destDir = Path.GetDirectoryName(destPath);
            if (destDir != null)
                Directory.CreateDirectory(destDir);

            try
            {
                File.Copy(srcFile, destPath, overwrite: true);
                copied++;
            }
            catch (IOException)
            {
                // File is locked (running exe) — rename old file, then copy new
                var bakPath = destPath + ".bak";
                try { File.Delete(bakPath); } catch { }
                File.Move(destPath, bakPath);
                File.Copy(srcFile, destPath);
                copied++;
                renamed++;
            }
        }

        // Launch updated GUI (inherits admin token from current process)
        Process.Start(new ProcessStartInfo
        {
            FileName = guiExe,
            WorkingDirectory = appDir,
            UseShellExecute = false
        });
    }
}
