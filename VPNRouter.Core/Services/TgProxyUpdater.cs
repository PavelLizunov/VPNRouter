using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Downloads Python embeddable + tg-ws-proxy source from GitHub.
/// Runs headless (no tray icon) via: python.exe -m proxy.tg_ws_proxy --port X --secret Y
/// </summary>
public class TgProxyUpdater
{
    private const string ProxyRepo = "Flowseal/tg-ws-proxy";
    private const string GitHubApiBase = "https://api.github.com/repos";
    private const string PythonVersion = "3.12.7";
    private const string PythonZipUrl = $"https://www.python.org/ftp/python/{PythonVersion}/python-{PythonVersion}-embed-amd64.zip";

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
    public static string PythonDir => Path.Combine(TgProxyDir, "python");
    public static string PythonExePath => Path.Combine(PythonDir, "python.exe");
    public static string ProxySourceDir => Path.Combine(TgProxyDir, "proxy");
    public static string VersionFilePath => Path.Combine(TgProxyDir, "version.txt");

    public event Action<string>? StatusChanged;

    public TgProxyUpdater(ILogger logger) => _logger = logger;

    /// <summary>Check if both Python and proxy source are installed.</summary>
    public static bool IsInstalled() => IsInstalled(logger: null);

    /// <summary>
    /// v2.31.10 — Logger-aware overload. When <paramref name="logger"/> is supplied
    /// emits one structured line per probe so a missing autostart can be
    /// diagnosed from logs without re-running with extra instrumentation.
    /// </summary>
    public static bool IsInstalled(ILogger? logger)
    {
        var pythonExePath = PythonExePath;
        var proxySourceDir = ProxySourceDir;
        var pythonExeExists = File.Exists(pythonExePath);
        var proxySourceExists = Directory.Exists(proxySourceDir);
        var overall = pythonExeExists && proxySourceExists;

        logger?.Information(
            "[TgProxy] IsInstalled: PythonExe at {PythonExePath} -> {PythonExeExists}, ProxySourceDir at {ProxySourceDir} -> {ProxySourceExists}, overall = {Overall}",
            pythonExePath, pythonExeExists, proxySourceDir, proxySourceExists, overall);

        return overall;
    }

    /// <summary>Read locally installed proxy version.</summary>
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

    /// <summary>Download Python embeddable + cryptography + proxy source.</summary>
    public async Task DownloadAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(TgProxyDir);

        // Step 1: Python embeddable (one-time, ~11 MB)
        if (!File.Exists(PythonExePath))
        {
            await DownloadPythonAsync(ct);
        }

        // Step 2: Python dependencies — cryptography + cffi + pycparser (one-time)
        var cryptoMarker = Path.Combine(PythonDir, "Lib", "cryptography", "__init__.py");
        var cffiMarker = Path.Combine(PythonDir, "Lib", "cffi", "__init__.py");
        if (!File.Exists(cryptoMarker) || !File.Exists(cffiMarker))
        {
            await DownloadDependenciesAsync(ct);
        }

        // Step 3: Proxy source from GitHub (updated each time)
        await DownloadProxySourceAsync(ct);
    }

    /// <summary>Download and extract Python embeddable distribution.</summary>
    private async Task DownloadPythonAsync(CancellationToken ct)
    {
        StatusChanged?.Invoke($"Downloading Python {PythonVersion}...");
        _logger.Information("[TgProxy] Downloading Python embeddable: {Url}", PythonZipUrl);

        var tempZip = Path.GetTempFileName() + ".zip";
        try
        {
            using (var stream = await _http.GetStreamAsync(PythonZipUrl, ct))
            using (var file = File.Create(tempZip))
                await stream.CopyToAsync(file, ct);

            StatusChanged?.Invoke("Extracting Python...");
            if (Directory.Exists(PythonDir))
                Directory.Delete(PythonDir, recursive: true);

            ZipFile.ExtractToDirectory(tempZip, PythonDir, overwriteFiles: true);

            // Modify ._pth file to add parent dir (for proxy package) and Lib (for cryptography)
            PatchPythonPath();

            _logger.Information("[TgProxy] Python {Version} installed", PythonVersion);
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }
    }

    /// <summary>
    /// Modify python3XX._pth to add parent dir (..) and Lib directory.
    /// Without this, Python can't find the proxy package or installed wheels.
    /// </summary>
    private void PatchPythonPath()
    {
        var pthFiles = Directory.GetFiles(PythonDir, "python*._pth");
        foreach (var pthFile in pthFiles)
        {
            var lines = File.ReadAllLines(pthFile).ToList();

            // Add parent dir for proxy package
            if (!lines.Contains(".."))
                lines.Add("..");

            // Add Lib for cryptography
            if (!lines.Contains("Lib"))
                lines.Add("Lib");

            File.WriteAllLines(pthFile, lines);
            _logger.Information("[TgProxy] Patched {File}", Path.GetFileName(pthFile));
        }
    }

    /// <summary>Download Python wheels from PyPI and extract to Lib/.</summary>
    private async Task DownloadDependenciesAsync(CancellationToken ct)
    {
        using var pypiHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        pypiHttp.DefaultRequestHeaders.Add("User-Agent", "VPNRouter");

        var libDir = Path.Combine(PythonDir, "Lib");
        Directory.CreateDirectory(libDir);

        // cryptography needs cffi, cffi needs pycparser — install all three
        var packages = new[]
        {
            ("pycparser", "py3-none-any"),       // pure Python, any platform
            ("cffi", "cp312-cp312-win_amd64"),   // compiled C extension
            ("cryptography", "cp39-abi3-win_amd64"), // Rust-based, ABI3 compatible
        };

        foreach (var (pkgName, wheelPattern) in packages)
        {
            StatusChanged?.Invoke($"Installing {pkgName}...");
            _logger.Information("[TgProxy] Downloading {Package}...", pkgName);

            var pypiUrl = $"https://pypi.org/pypi/{pkgName}/json";
            var resp = await pypiHttp.GetStringAsync(pypiUrl, ct);
            using var doc = JsonDocument.Parse(resp);

            // Find matching wheel
            string? wheelUrl = null;
            foreach (var urlEntry in doc.RootElement.GetProperty("urls").EnumerateArray())
            {
                var filename = urlEntry.GetProperty("filename").GetString() ?? "";
                if (filename.Contains(wheelPattern) && filename.EndsWith(".whl"))
                {
                    wheelUrl = urlEntry.GetProperty("url").GetString();
                    break;
                }
            }

            // Fallback: try any win_amd64 wheel
            if (wheelUrl == null)
            {
                foreach (var urlEntry in doc.RootElement.GetProperty("urls").EnumerateArray())
                {
                    var filename = urlEntry.GetProperty("filename").GetString() ?? "";
                    if (filename.Contains("win_amd64") && filename.EndsWith(".whl"))
                    {
                        wheelUrl = urlEntry.GetProperty("url").GetString();
                        break;
                    }
                    // Pure Python wheel
                    if (filename.Contains("py3-none-any") && filename.EndsWith(".whl"))
                    {
                        wheelUrl = urlEntry.GetProperty("url").GetString();
                        break;
                    }
                }
            }

            if (wheelUrl == null)
                throw new Exception($"Could not find wheel for {pkgName}");

            // Download and extract (wheel = ZIP)
            var tempWhl = Path.GetTempFileName() + ".whl";
            try
            {
                using (var stream = await pypiHttp.GetStreamAsync(wheelUrl, ct))
                using (var file = File.Create(tempWhl))
                    await stream.CopyToAsync(file, ct);

                ZipFile.ExtractToDirectory(tempWhl, libDir, overwriteFiles: true);
                _logger.Information("[TgProxy] {Package} installed", pkgName);
            }
            finally
            {
                try { File.Delete(tempWhl); } catch { }
            }
        }
    }

    /// <summary>Download proxy source from GitHub (latest release tag).</summary>
    private async Task DownloadProxySourceAsync(CancellationToken ct)
    {
        StatusChanged?.Invoke("Fetching proxy source...");

        // Get latest release tag
        var url = $"{GitHubApiBase}/{ProxyRepo}/releases/latest";
        var resp = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(resp);
        var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "unknown";

        _logger.Information("[TgProxy] Latest release: {Tag}", tagName);
        StatusChanged?.Invoke($"Downloading {tagName}...");

        // Download source zipball
        var zipballUrl = doc.RootElement.GetProperty("zipball_url").GetString()
            ?? throw new Exception("No zipball_url in release");

        var tempZip = Path.GetTempFileName() + ".zip";
        try
        {
            using (var stream = await _http.GetStreamAsync(zipballUrl, ct))
            using (var file = File.Create(tempZip))
                await stream.CopyToAsync(file, ct);

            StatusChanged?.Invoke("Extracting proxy source...");

            // Extract to temp dir
            var tempDir = Path.Combine(Path.GetTempPath(), $"tgproxy-{Guid.NewGuid():N}");
            try
            {
                ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

                // Find the proxy/ directory inside extracted source
                // ZIP has a wrapper folder: Flowseal-tg-ws-proxy-{hash}/proxy/
                var extractedRoot = tempDir;
                var subdirs = Directory.GetDirectories(tempDir);
                if (subdirs.Length == 1)
                    extractedRoot = subdirs[0];

                var sourceProxyDir = Path.Combine(extractedRoot, "proxy");
                if (!Directory.Exists(sourceProxyDir))
                    throw new Exception("proxy/ directory not found in source");

                // Replace existing proxy source
                if (Directory.Exists(ProxySourceDir))
                    Directory.Delete(ProxySourceDir, recursive: true);

                CopyDirectory(sourceProxyDir, ProxySourceDir);

                // Write version
                File.WriteAllText(VersionFilePath, tagName);
                _logger.Information("[TgProxy] Proxy source {Version} installed", tagName);
                StatusChanged?.Invoke($"Installed {tagName}");
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }
}
