using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Downloads Flowseal tg-ws-proxy from GitHub releases.
/// Single exe file (~20 MB) — local MTProto proxy for Telegram.
/// </summary>
public class TgWsProxyUpdater
{
    private const string FlowsealRepo = "Flowseal/tg-ws-proxy";
    private const string GitHubApiBase = "https://api.github.com/repos";
    private const string ExeName = "TgWsProxy_windows.exe";

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

    public static string ProxyDir => Path.Combine(_dataDir, "tg-ws-proxy");
    public static string ExePath => Path.Combine(ProxyDir, ExeName);
    public static string VersionFilePath => Path.Combine(ProxyDir, "version.txt");

    public event Action<string>? StatusChanged;

    public TgWsProxyUpdater(ILogger logger) => _logger = logger;

    public static bool IsInstalled() => File.Exists(ExePath);

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

    /// <summary>Download the latest TgWsProxy exe from GitHub releases.</summary>
    public async Task DownloadAndExtractAsync(CancellationToken ct)
    {
        StatusChanged?.Invoke("Fetching release info...");
        _logger.Information("[TgWsProxy] Checking latest release");

        var url = $"{GitHubApiBase}/{FlowsealRepo}/releases/latest";
        var resp = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(resp);
        var root = doc.RootElement;

        var tagName = root.GetProperty("tag_name").GetString() ?? "unknown";
        _logger.Information("[TgWsProxy] Latest release: {Tag}", tagName);

        // Find Windows exe asset
        string? exeUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
            {
                exeUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }

        if (exeUrl == null)
            throw new Exception($"Asset '{ExeName}' not found in release {tagName}");

        // Download exe
        StatusChanged?.Invoke($"Downloading {tagName}...");
        _logger.Information("[TgWsProxy] Downloading: {Url}", exeUrl);

        Directory.CreateDirectory(ProxyDir);
        var tempPath = ExePath + ".tmp";

        try
        {
            using (var stream = await _http.GetStreamAsync(exeUrl, ct))
            using (var file = File.Create(tempPath))
            {
                await stream.CopyToAsync(file, ct);
            }

            var size = new FileInfo(tempPath).Length;
            _logger.Information("[TgWsProxy] Downloaded {Size} KB", size / 1024);

            // Replace existing exe
            if (File.Exists(ExePath))
                File.Delete(ExePath);
            File.Move(tempPath, ExePath);

            // Write version
            File.WriteAllText(VersionFilePath, tagName);
            _logger.Information("[TgWsProxy] Installed version {Version}", tagName);

            StatusChanged?.Invoke($"Installed {tagName}");
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }
}
