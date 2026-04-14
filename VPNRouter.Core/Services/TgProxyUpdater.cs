using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Downloads tg-ws-proxy (Flowseal) releases from GitHub.
/// Asset: TgWsProxy_windows.exe (~21 MB PyInstaller binary).
/// </summary>
public class TgProxyUpdater
{
    private const string Repo = "Flowseal/tg-ws-proxy";
    private const string GitHubApiBase = "https://api.github.com/repos";
    private const string WindowsAssetName = "TgWsProxy_windows.exe";

    private static readonly string _dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VPNRouter");

    private readonly ILogger _logger;
    private static readonly HttpClient _http = new()
    {
        DefaultRequestHeaders =
        {
            { "User-Agent", "VPNRouter" },
            { "Accept", "application/vnd.github.v3+json" }
        },
        Timeout = TimeSpan.FromMinutes(10)
    };

    public static string TgProxyDir => Path.Combine(_dataDir, "tg-proxy");
    public static string ExePath => Path.Combine(TgProxyDir, "tg-ws-proxy.exe");
    public static string VersionFilePath => Path.Combine(TgProxyDir, "version.txt");

    public event Action<string>? StatusChanged;

    public TgProxyUpdater(ILogger logger) => _logger = logger;

    /// <summary>Check if tg-ws-proxy exe is installed.</summary>
    public static bool IsInstalled() => File.Exists(ExePath);

    /// <summary>Read locally installed version from version.txt.</summary>
    public static string? GetLocalVersion()
    {
        try
        {
            if (File.Exists(VersionFilePath))
                return File.ReadAllText(VersionFilePath).Trim();
        }
        catch { }
        return null;
    }

    /// <summary>Download the latest tg-ws-proxy Windows exe from GitHub Releases.</summary>
    public async Task DownloadAsync(CancellationToken ct = default)
    {
        StatusChanged?.Invoke("Fetching release info...");
        _logger.Information("[TgProxy] Checking latest release");

        // Get latest release from GitHub API
        var url = $"{GitHubApiBase}/{Repo}/releases/latest";
        var resp = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(resp);
        var root = doc.RootElement;

        var tagName = root.GetProperty("tag_name").GetString() ?? "unknown";
        _logger.Information("[TgProxy] Latest release: {Tag}", tagName);

        // Find Windows exe asset
        string? exeUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.Equals(WindowsAssetName, StringComparison.OrdinalIgnoreCase))
            {
                exeUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }

        if (exeUrl == null)
            throw new Exception($"Asset '{WindowsAssetName}' not found in release {tagName}");

        // Download exe
        StatusChanged?.Invoke($"Downloading {tagName}...");
        _logger.Information("[TgProxy] Downloading: {Url}", exeUrl);

        var tempFile = Path.GetTempFileName();
        try
        {
            using (var stream = await _http.GetStreamAsync(exeUrl, ct))
            using (var file = File.Create(tempFile))
            {
                await stream.CopyToAsync(file, ct);
            }

            var fileSize = new FileInfo(tempFile).Length;
            _logger.Information("[TgProxy] Downloaded {Size} MB", fileSize / (1024 * 1024));

            // Install: create directory, move exe
            StatusChanged?.Invoke("Installing...");
            Directory.CreateDirectory(TgProxyDir);

            // Replace existing exe
            if (File.Exists(ExePath))
                File.Delete(ExePath);

            File.Move(tempFile, ExePath);

            // Write version file
            File.WriteAllText(VersionFilePath, tagName);
            _logger.Information("[TgProxy] Installed {Version}", tagName);

            StatusChanged?.Invoke($"Installed {tagName}");
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch { }
        }
    }
}
