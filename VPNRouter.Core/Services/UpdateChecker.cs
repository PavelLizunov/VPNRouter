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

    // ── Platform-specific asset naming ──
    // v2.0+: VPNRouter-v2.0.0-win.zip / VPNRouter-v2.0.0-mac.zip
    // Legacy: VPNRouter-install-v1.24.6.zip (old Windows naming, still supported)
    private static readonly string PlatformSuffix =
        OperatingSystem.IsMacOS() ? "-mac" : "-win";

    static UpdateChecker()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "VPNRouter");
    }

    public UpdateChecker(UpdateSettings settings, string currentVersion)
    {
        _settings = settings;
        _currentVersion = currentVersion;
        _stagingDir = Path.Combine(AppPaths.DataDir, "update-staging");
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.GitHubRepo))
            return null;

        if (!Version.TryParse(_currentVersion, out var current))
            return null;

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

        // Skip platform-specific tags (e.g. v1.0.0-mac) — they don't parse as Version
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

        // ── Find platform-specific assets ──
        var fullAsset = FindFullAsset(latestRelease.Release.assets);
        var liteAsset = FindLiteAsset(latestRelease.Release.assets);

        bool canUseLite = liteAsset != null && IsSharedRuntimeInstall();

        if (fullAsset == null && !canUseLite)
            return null;

        var allNotes = newerReleases
            .Where(r => !string.IsNullOrWhiteSpace(r.Release.body))
            .Select(r => r.Release.body!.Trim())
            .ToList();

        var info = new UpdateInfo
        {
            CurrentVersion = _currentVersion,
            LatestVersion = latestRelease.Tag,
            DownloadUrl = fullAsset?.browser_download_url
                          ?? liteAsset?.browser_download_url ?? string.Empty,
            ReleaseNotes = string.Join("\n\n", allNotes),
            HtmlUrl = latestRelease.Release.html_url ?? string.Empty,
            SizeBytes = fullAsset?.size ?? liteAsset?.size ?? 0,
            IsNewer = true,
            LiteDownloadUrl = liteAsset?.browser_download_url,
            LiteSizeBytes = liteAsset?.size ?? 0,
            HasLiteUpdate = canUseLite
        };

        UpdateAvailable?.Invoke(info);
        return info;
    }

    public async Task<string> DownloadAndStageAsync(UpdateInfo info, CancellationToken ct = default)
    {
        var useLite = info.HasLiteUpdate && !string.IsNullOrEmpty(info.LiteDownloadUrl);
        var downloadUrl = useLite ? info.LiteDownloadUrl! : info.DownloadUrl;
        var expectedSize = useLite ? info.LiteSizeBytes : info.SizeBytes;
        var label = useLite ? "lite update" : "full update";

        StatusChanged?.Invoke($"Downloading {label}...");

        if (Directory.Exists(_stagingDir))
            Directory.Delete(_stagingDir, true);
        Directory.CreateDirectory(_stagingDir);

        var zipPath = Path.Combine(_stagingDir, $"VPNRouter-v{info.LatestVersion}.zip");

        using var response = await _http.GetAsync(downloadUrl,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;

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

        var downloadedSize = new FileInfo(zipPath).Length;
        if (expectedSize > 0 && downloadedSize < expectedSize * 0.9)
            throw new InvalidOperationException(
                $"Downloaded file is too small ({downloadedSize / 1024 / 1024} MB vs expected {expectedSize / 1024 / 1024} MB). Download may be corrupted.");

        StatusChanged?.Invoke("Extracting update...");

        var extractDir = Path.Combine(_stagingDir, "extracted");
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        ValidateExtractedContent(extractDir);

        StatusChanged?.Invoke("Update ready to apply.");
        return extractDir;
    }

    public void CleanupStagingDir()
    {
        try
        {
            if (Directory.Exists(_stagingDir))
                Directory.Delete(_stagingDir, true);
        }
        catch { }

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
    /// Apply update. Platform-specific:
    /// - Windows: copy files, rename locked executables (.bak), relaunch .exe
    /// - macOS: replace .app bundle contents, relaunch via open(1)
    /// </summary>
    public void ApplyUpdate(string extractedDir)
    {
        if (OperatingSystem.IsMacOS())
            ApplyUpdateMac(extractedDir);
        else
            ApplyUpdateWindows(extractedDir);
    }

    // ─── Windows ─────────────────────────────────────────────────────────────

    private static void ApplyUpdateWindows(string extractedDir)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd('\\');

        // Strip app/ wrapper from install ZIP layout
        var appSubDir = Path.Combine(extractedDir, "app");
        if (Directory.Exists(appSubDir) &&
            (File.Exists(Path.Combine(appSubDir, "VPNRouter.GUI.exe")) ||
             File.Exists(Path.Combine(appSubDir, "VPNRouter.GUI.dll"))))
        {
            extractedDir = appSubDir;
        }

        var guiExe = Path.Combine(appDir, "VPNRouter.GUI.exe");

        // Kill sing-box before copying — it's a running process that locks
        // its exe file. Without this, sing-box.exe silently fails to update.
        foreach (var proc in Process.GetProcessesByName("sing-box"))
        {
            try { proc.Kill(entireProcessTree: true); proc.WaitForExit(3000); }
            catch { }
            finally { proc.Dispose(); }
        }

        foreach (var srcFile in Directory.GetFiles(extractedDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(extractedDir, srcFile);
            var destPath = Path.Combine(appDir, relativePath);

            var destDir = Path.GetDirectoryName(destPath);
            if (destDir != null)
                Directory.CreateDirectory(destDir);

            try
            {
                File.Copy(srcFile, destPath, overwrite: true);
            }
            catch (IOException)
            {
                var bakPath = destPath + ".bak";
                try { File.Delete(bakPath); } catch { }
                try { File.Move(destPath, bakPath); } catch { }
                try { File.Copy(srcFile, destPath); } catch { }
            }
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = guiExe,
            WorkingDirectory = Path.GetDirectoryName(guiExe)!,
            UseShellExecute = false
        });
    }

    // ─── macOS ───────────────────────────────────────────────────────────────

    private static void ApplyUpdateMac(string extractedDir)
    {
        // Find .app bundle in extracted content
        var appBundles = Directory.GetDirectories(extractedDir, "*.app", SearchOption.TopDirectoryOnly);

        string sourceDir;
        if (appBundles.Length > 0)
        {
            // ZIP contains .app bundle — use its Contents/
            sourceDir = Path.Combine(appBundles[0], "Contents");
        }
        else if (Directory.Exists(Path.Combine(extractedDir, "Contents")))
        {
            // ZIP contains Contents/ directly
            sourceDir = Path.Combine(extractedDir, "Contents");
        }
        else
        {
            // Flat layout — just DLLs, copy to Contents/MacOS/
            var currentAppBundle = FindCurrentAppBundle()
                ?? throw new InvalidOperationException("Cannot locate current .app bundle.");
            var targetMacOS = Path.Combine(currentAppBundle, "Contents", "MacOS");
            CopyDirectoryRecursive(extractedDir, targetMacOS);

            Process.Start(new ProcessStartInfo("/usr/bin/open", $"-n \"{currentAppBundle}\"")
                { UseShellExecute = false });
            return;
        }

        // Full bundle update — replace entire Contents/
        var appBundle = FindCurrentAppBundle()
            ?? throw new InvalidOperationException("Cannot locate current .app bundle.");
        var targetContents = Path.Combine(appBundle, "Contents");

        CopyDirectoryRecursive(sourceDir, targetContents);

        // Make binaries executable
        var macosDir = Path.Combine(targetContents, "MacOS");
        if (Directory.Exists(macosDir))
        {
            foreach (var file in Directory.GetFiles(macosDir))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("/bin/chmod", $"+x \"{file}\"")
                        { UseShellExecute = false })?.WaitForExit(3000);
                }
                catch { }
            }
        }

        Process.Start(new ProcessStartInfo("/usr/bin/open", $"-n \"{appBundle}\"")
            { UseShellExecute = false });
    }

    /// <summary>
    /// Walk up from AppContext.BaseDirectory to find the .app bundle.
    /// e.g. /Applications/VPNRouter.app/Contents/MacOS/ → /Applications/VPNRouter.app
    /// </summary>
    private static string? FindCurrentAppBundle()
    {
        var dir = AppContext.BaseDirectory.TrimEnd('/');
        while (!string.IsNullOrEmpty(dir))
        {
            if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static void CopyDirectoryRecursive(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var subdir in Directory.GetDirectories(source))
            CopyDirectoryRecursive(subdir, Path.Combine(target, Path.GetFileName(subdir)));
    }

    // ─── Asset matching ──────────────────────────────────────────────────────

    /// <summary>
    /// Find the full install asset for the current platform.
    /// v2.0+: VPNRouter-v2.0.0-win.zip / VPNRouter-v2.0.0-mac.zip
    /// Legacy: VPNRouter-install-v*.zip (Windows only, no platform suffix)
    /// </summary>
    private static dynamic? FindFullAsset(dynamic[]? assets)
    {
        if (assets == null) return null;

        // New naming: VPNRouter-v*-{platform}.zip (not containing "update")
        var newFormat = ((IEnumerable<dynamic>)assets).FirstOrDefault(a =>
        {
            string name = a.name;
            return name.StartsWith("VPNRouter-v", StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith($"{PlatformSuffix}.zip", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("update", StringComparison.OrdinalIgnoreCase);
        });
        if (newFormat != null) return newFormat;

        // Legacy (Windows only): VPNRouter-install-v*.zip
        if (OperatingSystem.IsWindows())
        {
            return ((IEnumerable<dynamic>)assets).FirstOrDefault(a =>
            {
                string name = a.name;
                return name.StartsWith("VPNRouter-install-v", StringComparison.OrdinalIgnoreCase) &&
                       name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            });
        }

        return null;
    }

    /// <summary>
    /// Find the lite update asset (Windows only).
    /// v2.0+: VPNRouter-update-v2.0.0-win.zip
    /// Legacy: VPNRouter-update-v*.zip
    /// </summary>
    private static dynamic? FindLiteAsset(dynamic[]? assets)
    {
        if (assets == null || !OperatingSystem.IsWindows()) return null;

        var newFormat = ((IEnumerable<dynamic>)assets).FirstOrDefault(a =>
        {
            string name = a.name;
            return name.StartsWith("VPNRouter-update-v", StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith($"{PlatformSuffix}.zip", StringComparison.OrdinalIgnoreCase);
        });
        if (newFormat != null) return newFormat;

        return ((IEnumerable<dynamic>)assets).FirstOrDefault(a =>
        {
            string name = a.name;
            return name.StartsWith("VPNRouter-update-v", StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Validate extracted content based on platform.
    /// </summary>
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

        // Windows: support flat and app/ layout
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

    /// <summary>
    /// Shared runtime detection. Only meaningful on Windows — macOS has no lite update.
    /// </summary>
    private static bool IsSharedRuntimeInstall()
    {
        if (OperatingSystem.IsMacOS()) return false;
        var appDir = AppContext.BaseDirectory;
        return File.Exists(Path.Combine(appDir, "hostfxr.dll"));
    }
}
