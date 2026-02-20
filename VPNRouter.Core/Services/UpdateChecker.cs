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

        var url = $"https://api.github.com/repos/{_settings.GitHubRepo}/releases/latest";
        var json = await _http.GetStringAsync(url, ct);

        var release = JsonConvert.DeserializeAnonymousType(json, new
        {
            tag_name = "",
            body = "",
            html_url = "",
            assets = new[] { new { browser_download_url = "", size = 0L, name = "" } }
        });

        if (release == null) return null;

        var latestTag = release.tag_name.TrimStart('v');

        var asset = release.assets?.FirstOrDefault(a =>
            a.name.StartsWith("VPNRouter-v", StringComparison.OrdinalIgnoreCase) &&
            a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        if (asset == null) return null;

        var isNewer = Version.TryParse(latestTag, out var latest)
                   && Version.TryParse(_currentVersion, out var current)
                   && latest > current;

        var info = new UpdateInfo
        {
            CurrentVersion = _currentVersion,
            LatestVersion = latestTag,
            DownloadUrl = asset.browser_download_url,
            ReleaseNotes = release.body ?? string.Empty,
            HtmlUrl = release.html_url ?? string.Empty,
            SizeBytes = asset.size,
            IsNewer = isNewer
        };

        if (isNewer)
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

        StatusChanged?.Invoke("Extracting update...");

        var extractDir = Path.Combine(_stagingDir, "extracted");
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        if (!File.Exists(Path.Combine(extractDir, "VPNRouter.GUI.exe")))
            throw new InvalidOperationException("Invalid update package: VPNRouter.GUI.exe not found.");

        StatusChanged?.Invoke("Update ready to apply.");
        return extractDir;
    }

    public void ApplyUpdate(string extractedDir)
    {
        var appDir = AppContext.BaseDirectory;
        var batchPath = Path.Combine(_stagingDir, "apply-update.cmd");
        var guiExe = Path.Combine(appDir, "VPNRouter.GUI.exe");
        var currentPid = Environment.ProcessId;

        var script = $"""
            @echo off
            echo [VPNRouter Update] Waiting for process {currentPid} to exit...
            :waitloop
            tasklist /fi "PID eq {currentPid}" 2>NUL | find "{currentPid}" >NUL
            if %ERRORLEVEL%==0 (
                timeout /t 1 /nobreak >NUL
                goto waitloop
            )
            echo [VPNRouter Update] Applying update...
            xcopy /s /y /q "{extractedDir}\*" "{appDir}"
            echo [VPNRouter Update] Cleaning up...
            rd /s /q "{_stagingDir}" 2>NUL
            echo [VPNRouter Update] Launching updated VPNRouter...
            start "" "{guiExe}"
            exit /b 0
            """;

        File.WriteAllText(batchPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{batchPath}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }
}
