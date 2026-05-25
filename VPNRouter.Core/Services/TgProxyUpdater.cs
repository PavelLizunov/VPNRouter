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

    /// <summary>
    /// r37 — exposed for the start-flow auto-update check. The MVM's
    /// ToggleTgProxyAsync uses <see cref="RemoteVersionChecker.GetLatestTagAsync"/>
    /// with this repo string to detect newer upstream releases.
    /// </summary>
    public const string ProxyRepoPublic = ProxyRepo;

    private const string GitHubApiBase = "https://api.github.com/repos";
    private const string PythonVersion = "3.12.7";
    private const string PythonZipUrl = $"https://www.python.org/ftp/python/{PythonVersion}/python-{PythonVersion}-embed-amd64.zip";

    private static readonly string _dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VPNRouter");

    private readonly ILogger _logger;

    // v3.0 Phase 4 (2026-05-18): IHttpClient seam. All HTTP traffic
    // (GitHub API for release info, python.org for the embeddable ZIP,
    // pypi.org for cryptography/cffi/pycparser wheels, GitHub for the
    // proxy source zipball) routes through the shared client so retry
    // policy + connection pool are uniform.
    private readonly IHttpClient _http;

    // Wheels can be 10+ MB and python.org can be slow — extend the
    // per-request timeout for download paths via the envelope.
    private static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Headers GitHub's REST API expects. Sent on every release-list
    /// fetch; python.org / pypi.org don't need them but the policy
    /// client tolerates extras.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> GitHubApiHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/vnd.github.v3+json",
        };

    public static string TgProxyDir => Path.Combine(_dataDir, "tg-proxy");
    public static string PythonDir => Path.Combine(TgProxyDir, "python");
    public static string PythonExePath => Path.Combine(PythonDir, "python.exe");
    public static string ProxySourceDir => Path.Combine(TgProxyDir, "proxy");
    public static string VersionFilePath => Path.Combine(TgProxyDir, "version.txt");

    public event Action<string>? StatusChanged;

    /// <summary>
    /// Production ctor — uses the process-shared <see cref="PolicyHttpClient"/>.
    /// </summary>
    public TgProxyUpdater(ILogger logger)
        : this(logger, PolicyHttpClient.Shared)
    {
    }

    /// <summary>
    /// Test / DI ctor — caller supplies a custom <see cref="IHttpClient"/>
    /// (typically <c>FakeHttpClient</c> in tests).
    /// </summary>
    public TgProxyUpdater(ILogger logger, IHttpClient http)
    {
        _logger = logger;
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>Check if both Python and proxy source are installed.</summary>
    public static bool IsInstalled() => IsInstalledAt(TgProxyDir, logger: null);

    /// <summary>
    /// v2.31.10 (DBG-4) — Logger-aware overload. When <paramref name="logger"/>
    /// is supplied emits one structured line per probe so a missing autostart
    /// can be diagnosed from logs without re-running with extra instrumentation.
    /// </summary>
    public static bool IsInstalled(ILogger? logger) => IsInstalledAt(TgProxyDir, logger);

    /// <summary>
    /// v2.31.10 (DBG-5) — Path-explicit variant for unit tests that synthesize
    /// a sandbox layout under a temp directory. Mirrors the production check:
    /// <c>{baseDir}/python/python.exe</c> must exist as a file AND
    /// <c>{baseDir}/proxy</c> must exist as a directory.
    ///
    /// <para>Combines DBG-5 path-injection + DBG-4 structured logging in one
    /// method so production (<c>IsInstalled(logger)</c>) and tests
    /// (<c>IsInstalledAt(tempDir)</c>) share the same code path.</para>
    /// </summary>
    internal static bool IsInstalledAt(string baseDir, ILogger? logger = null)
    {
        var pythonExe = Path.Combine(baseDir, "python", "python.exe");
        var proxyDir = Path.Combine(baseDir, "proxy");
        var pythonExeExists = File.Exists(pythonExe);
        var proxySourceExists = Directory.Exists(proxyDir);
        var overall = pythonExeExists && proxySourceExists;

        logger?.Information(
            "[TgProxy] IsInstalled: PythonExe at {PythonExePath} -> {PythonExeExists}, ProxySourceDir at {ProxySourceDir} -> {ProxySourceExists}, overall = {Overall}",
            pythonExe, pythonExeExists, proxyDir, proxySourceExists, overall);

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
        // v2.36 (MVP one-button): per-step progress feedback. Pre-fix
        // the toast read "Downloading tg-ws-proxy..." for 30–90s with
        // no signal of which sub-step was running (Python embeddable
        // ~11 MB, wheels ~10 MB, source zipball ~4 MB). The "Step N/3:"
        // prefix lets the user track progress; format is stable so
        // tests + UI can parse it consistently.
        StatusChanged?.Invoke($"Step 1/3: Downloading Python {PythonVersion} (~11 MB)...");
        _logger.Information("[TgProxy] Downloading Python embeddable: {Url}", PythonZipUrl);

        var tempZip = Path.GetTempFileName() + ".zip";
        try
        {
            // v3.0 Phase 4: SendStreamingAsync replaces GetStreamAsync.
            // Disposal of the wrapper aborts the socket if `file.WriteAsync`
            // throws mid-copy — kernel buffer is freed before we hit the
            // outer cleanup `finally`.
            await using (var response = await _http.SendStreamingAsync(
                new HttpRequest(
                    HttpMethod.Get,
                    new Uri(PythonZipUrl),
                    Timeout: DefaultDownloadTimeout),
                ct).ConfigureAwait(false))
            {
                if (!response.IsSuccess())
                    throw new HttpRequestException(
                        $"HTTP {response.StatusCode} downloading Python embeddable");

                using var file = File.Create(tempZip);
                await response.Body.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            StatusChanged?.Invoke("Step 1/3: Extracting Python...");
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
        // v3.0 Phase 4: route through the shared IHttpClient. PyPI is a
        // public CDN; no special headers (the GitHub Accept header is
        // harmless on pypi.org and lets us share one envelope).
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
            // v2.36 (MVP one-button): unified "Step 2/3:" prefix for
            // the wheels group — three sub-packages (pycparser/cffi/
            // cryptography) collapse into one user-visible step so
            // progress is tractable.
            StatusChanged?.Invoke($"Step 2/3: Installing {pkgName}...");
            _logger.Information("[TgProxy] Downloading {Package}...", pkgName);

            var pypiUrl = $"https://pypi.org/pypi/{pkgName}/json";
            var pypiResp = await _http.SendAsync(
                new HttpRequest(HttpMethod.Get, new Uri(pypiUrl)),
                ct).ConfigureAwait(false);
            if (!pypiResp.IsSuccess())
                throw new HttpRequestException(
                    $"HTTP {pypiResp.StatusCode} fetching PyPI metadata for {pkgName}");
            using var doc = JsonDocument.Parse(pypiResp.AsString());

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

            // Download and extract (wheel = ZIP). v3.0 Phase 4: streaming
            // download so a 10+ MB wheel doesn't sit in a managed buffer
            // before File.Create accepts it.
            var tempWhl = Path.GetTempFileName() + ".whl";
            try
            {
                await using (var response = await _http.SendStreamingAsync(
                    new HttpRequest(
                        HttpMethod.Get,
                        new Uri(wheelUrl),
                        Timeout: DefaultDownloadTimeout),
                    ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccess())
                        throw new HttpRequestException(
                            $"HTTP {response.StatusCode} downloading {pkgName} wheel");

                    using var file = File.Create(tempWhl);
                    await response.Body.CopyToAsync(file, ct).ConfigureAwait(false);
                }

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
        // v2.36 (MVP one-button): final "Step 3/3:" group covers the
        // GitHub release fetch + zipball download + extract.
        StatusChanged?.Invoke("Step 3/3: Fetching proxy source from GitHub...");

        // Get latest release tag
        var url = $"{GitHubApiBase}/{ProxyRepo}/releases/latest";
        var apiResp = await _http.SendAsync(
            new HttpRequest(HttpMethod.Get, new Uri(url), Headers: GitHubApiHeaders),
            ct).ConfigureAwait(false);
        if (!apiResp.IsSuccess())
            throw new HttpRequestException(
                $"HTTP {apiResp.StatusCode} fetching GitHub release info for {ProxyRepo}");
        using var doc = JsonDocument.Parse(apiResp.AsString());
        var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "unknown";

        _logger.Information("[TgProxy] Latest release: {Tag}", tagName);
        StatusChanged?.Invoke($"Step 3/3: Downloading proxy source {tagName}...");

        // Download source zipball
        var zipballUrl = doc.RootElement.GetProperty("zipball_url").GetString()
            ?? throw new Exception("No zipball_url in release");

        var tempZip = Path.GetTempFileName() + ".zip";
        try
        {
            // v3.0 Phase 4: streaming download — zipball can be several MB.
            await using (var response = await _http.SendStreamingAsync(
                new HttpRequest(
                    HttpMethod.Get,
                    new Uri(zipballUrl),
                    Headers: GitHubApiHeaders,
                    Timeout: DefaultDownloadTimeout),
                ct).ConfigureAwait(false))
            {
                if (!response.IsSuccess())
                    throw new HttpRequestException(
                        $"HTTP {response.StatusCode} downloading proxy source zipball");

                using var file = File.Create(tempZip);
                await response.Body.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            StatusChanged?.Invoke("Step 3/3: Extracting proxy source...");

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
