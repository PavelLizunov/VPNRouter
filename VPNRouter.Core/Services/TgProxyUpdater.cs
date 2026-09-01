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
    public const string SupportedProxyVersion = "v1.10.0";
    internal const string SupportedProxySourceSha256 =
        "62193af82c97d494264a0c6b744b37a7670c458cd1492e5e9ef0235298156327";

    /// <summary>
    /// r37 — exposed for the start-flow auto-update check. The MVM's
    /// ToggleTgProxyAsync uses <see cref="RemoteVersionChecker.GetLatestTagAsync"/>
    /// with this repo string to detect newer upstream releases.
    /// </summary>
    public const string ProxyRepoPublic = ProxyRepo;

    private const string GitHubApiBase = "https://api.github.com/repos";
    private const string PythonVersion = "3.12.7";
    private const string PythonZipUrl = $"https://www.python.org/ftp/python/{PythonVersion}/python-{PythonVersion}-embed-amd64.zip";
    // P1-2 (dep-review 2026-07-09): pinned sha256 of python-3.12.7-embed-amd64.zip,
    // captured from python.org canonical (11062583 bytes). python.org has no
    // authoritative sha256 API (MD5 + GPG only), so this locks the embeddable to
    // the exact known-good file: a later MITM / poisoned mirror on a user's box
    // fails CLOSED instead of unpacking + running a swapped Python interpreter.
    // MUST be recomputed when PythonVersion is bumped.
    private const string PythonZipSha256 = "0d57bb6cb078b74d23dbfe91f77d6780d45bed328911609f1f7ee2ba1606bf44";

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
    private readonly IProcessRunner _processRunner;

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
        : this(logger, PolicyHttpClient.Shared, new ProcessRunner())
    {
    }

    /// <summary>
    /// Test / DI ctor — caller supplies a custom <see cref="IHttpClient"/>
    /// (typically <c>FakeHttpClient</c> in tests).
    /// </summary>
    public TgProxyUpdater(ILogger logger, IHttpClient http, IProcessRunner? processRunner = null)
    {
        _logger = logger;
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _processRunner = processRunner ?? new ProcessRunner();
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
        var certifiExists = File.Exists(Path.Combine(
            baseDir, "python", "Lib", "certifi", "__init__.py"));
        var overall = pythonExeExists && proxySourceExists && certifiExists;

        logger?.Information(
            "[TgProxy] IsInstalled: PythonExe at {PythonExePath} -> {PythonExeExists}, ProxySourceDir at {ProxySourceDir} -> {ProxySourceExists}, certifi -> {CertifiExists}, overall = {Overall}",
            pythonExe, pythonExeExists, proxyDir, proxySourceExists, certifiExists, overall);

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
        var certifiMarker = Path.Combine(PythonDir, "Lib", "certifi", "__init__.py");
        if (!File.Exists(cryptoMarker) || !File.Exists(cffiMarker) || !File.Exists(certifiMarker))
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

        var tempZip = Path.Combine(Path.GetTempPath(), $"vpnr-tgproxy-python-{Guid.NewGuid():N}.zip");
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

            // P1-2: fail-closed sha256 pin of the Python embeddable BEFORE
            // extract — this is a whole interpreter we're about to run under the
            // user's account.
            VerifyPinnedSha256(tempZip, PythonZipSha256, $"Python {PythonVersion} embeddable");

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
        var stageRoot = Path.Combine(TgProxyDir, $".lib-stage-{Guid.NewGuid():N}");
        var stageLib = Path.Combine(stageRoot, "Lib");
        var backupDir = Path.Combine(TgProxyDir, $".lib-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageLib);
        if (Directory.Exists(libDir))
            CopyDirectory(libDir, stageLib);

        // cryptography needs cffi, cffi needs pycparser — install all three
        var packages = new[]
        {
            (Name: "pycparser", Version: "3.0", Pattern: "py3-none-any", Marker: Path.Combine("pycparser", "__init__.py"), Sha256: "b727414169a36b7d524c1c3e31839a521725078d7b2ff038656844266160a992"),
            (Name: "cffi", Version: "2.1.1", Pattern: "cp312-cp312-win_amd64", Marker: Path.Combine("cffi", "__init__.py"), Sha256: "f53e442b08449d42821fa4a4fba000095af9f62742a500f978a9f557ec44339a"),
            (Name: "cryptography", Version: "46.0.5", Pattern: "cp311-abi3-win_amd64", Marker: Path.Combine("cryptography", "__init__.py"), Sha256: "38946c54b16c885c72c4f59846be9743d699eee2b69b6988e0a00a01f46a61a4"),
            (Name: "certifi", Version: "2026.7.22", Pattern: "py3-none-any", Marker: Path.Combine("certifi", "__init__.py"), Sha256: "62f22742b58a1a33014a2b6b706588a8d7e2a88ae7bd1a6ebe8c992928483775"),
        };

        var installedAny = false;
        try
        {
        foreach (var package in packages)
        {
            var pkgName = package.Name;
            var wheelPattern = package.Pattern;
            if (File.Exists(Path.Combine(libDir, package.Marker)))
            {
                _logger.Information("[TgProxy] {Package} already installed; keeping existing package", pkgName);
                continue;
            }

            installedAny = true;
            // v2.36 (MVP one-button): unified "Step 2/3:" prefix for
            // the wheels group — three sub-packages (pycparser/cffi/
            // cryptography) collapse into one user-visible step so
            // progress is tractable.
            StatusChanged?.Invoke($"Step 2/3: Installing {pkgName}...");
            _logger.Information("[TgProxy] Downloading {Package}...", pkgName);

            var pypiUrl = $"https://pypi.org/pypi/{pkgName}/{package.Version}/json";
            var pypiResp = await _http.SendAsync(
                new HttpRequest(HttpMethod.Get, new Uri(pypiUrl)),
                ct).ConfigureAwait(false);
            if (!pypiResp.IsSuccess())
                throw new HttpRequestException(
                    $"HTTP {pypiResp.StatusCode} fetching PyPI metadata for {pkgName}");
            using var doc = JsonDocument.Parse(pypiResp.AsString());

            // Find matching wheel. P1-2 (dep-review 2026-07-09): capture the
            // PyPI-published sha256 (urls[].digests.sha256) ALONGSIDE the URL so
            // the download can be verified — this installs executable code
            // (cffi/cryptography C/Rust extensions) under the user's account, and
            // pre-fix the wheel was taken on trust with zero integrity check.
            string? wheelUrl = null, wheelSha256 = null;
            foreach (var urlEntry in doc.RootElement.GetProperty("urls").EnumerateArray())
            {
                var filename = urlEntry.GetProperty("filename").GetString() ?? "";
                if (filename.Contains(wheelPattern) && filename.EndsWith(".whl"))
                {
                    wheelUrl = urlEntry.GetProperty("url").GetString();
                    wheelSha256 = ReadPypiSha256(urlEntry);
                    break;
                }
            }

            if (wheelUrl == null)
                throw new Exception($"Could not find wheel for {pkgName}");
            if (!string.Equals(wheelSha256, package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"PyPI metadata digest changed for pinned {pkgName} {package.Version}. Refusing install.");

            // Download and extract (wheel = ZIP). v3.0 Phase 4: streaming
            // download so a 10+ MB wheel doesn't sit in a managed buffer
            // before File.Create accepts it.
            var tempWhl = Path.Combine(Path.GetTempPath(), $"vpnr-tgproxy-wheel-{Guid.NewGuid():N}.whl");
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

                // P1-2: verify the wheel against PyPI's published sha256 BEFORE
                // extracting/importing it. Fail-CLOSED — a mismatch (MITM, a
                // compromised mirror, a truncated download) aborts the install
                // rather than unpacking untrusted code. If PyPI didn't publish a
                // digest (shouldn't happen — every file carries one), log and
                // proceed so a metadata quirk doesn't brick TgProxy setup.
                VerifyPinnedSha256(tempWhl, package.Sha256, $"{pkgName} {package.Version} wheel");

                ZipFile.ExtractToDirectory(tempWhl, stageLib, overwriteFiles: true);
                _logger.Information("[TgProxy] {Package} {Version} staged", pkgName, package.Version);
            }
            finally
            {
                try { File.Delete(tempWhl); } catch { }
            }
        }

        if (!installedAny) return;

        await SmokeTestDependenciesAsync(stageLib, ct).ConfigureAwait(false);
        ActivateDependencyDirectoryAt(libDir, stageLib, backupDir, _logger);
        }
        finally
        {
            try { if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true); } catch { }
        }
    }

    private async Task SmokeTestDependenciesAsync(string stageLib, CancellationToken ct)
    {
        var python = "import sys; sys.path.insert(0, " +
                     JsonSerializer.Serialize(stageLib) +
                     "); import pycparser, cffi, cryptography, certifi";
        var probe = await _processRunner.RunAsync(new ProcessRequest(
            PythonExePath,
            ["-c", python],
            WorkingDirectory: TgProxyDir,
            CaptureStdout: true,
            CaptureStderr: true,
            Timeout: TimeSpan.FromSeconds(15)), ct).ConfigureAwait(false);

        if (probe.TimedOut || probe.ExitCode != 0)
            throw new InvalidOperationException(
                $"TgProxy dependency smoke test failed (exit {probe.ExitCode}).");
    }

    internal static void ActivateDependencyDirectoryAt(
        string currentLibDir,
        string stagedLibDir,
        string backupDir,
        ILogger? logger = null)
    {
        var movedOld = false;
        var installedNew = false;
        try
        {
            if (Directory.Exists(currentLibDir))
            {
                Directory.Move(currentLibDir, backupDir);
                movedOld = true;
            }

            Directory.Move(stagedLibDir, currentLibDir);
            installedNew = true;
        }
        catch (Exception activationError)
        {
            try
            {
                if (installedNew && Directory.Exists(currentLibDir))
                    Directory.Delete(currentLibDir, recursive: true);
                if (movedOld && Directory.Exists(backupDir))
                    Directory.Move(backupDir, currentLibDir);
            }
            catch (Exception rollbackError)
            {
                logger?.Error(
                    rollbackError,
                    "[TgProxy] Dependency rollback failed; recovery backup remains at {BackupDir}",
                    backupDir);
                throw new InvalidOperationException(
                    "TgProxy dependency activation and rollback failed. The previous runtime was preserved for recovery.",
                    new AggregateException(activationError, rollbackError));
            }

            throw;
        }

        try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true); } catch { }
    }

    /// <summary>Read <c>digests.sha256</c> from a PyPI <c>urls[]</c> entry; null if absent.</summary>
    private static string? ReadPypiSha256(JsonElement urlEntry)
        => urlEntry.TryGetProperty("digests", out var digests)
           && digests.TryGetProperty("sha256", out var sha)
           && sha.ValueKind == JsonValueKind.String
            ? sha.GetString()
            : null;

    /// <summary>Compute the file's sha256 and throw <see cref="InvalidOperationException"/>
    /// unless it equals <paramref name="expectedSha256"/> (hex, case-insensitive).
    /// The single fail-closed integrity primitive for every executable TgProxy
    /// pulls down (Python interpreter + wheels).</summary>
    internal static void VerifyPinnedSha256Static(string filePath, string expectedSha256, string label, ILogger? logger = null)
    {
        using var fs = File.OpenRead(filePath);
        var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fs));
        var expected = (expectedSha256 ?? string.Empty).Trim().ToLowerInvariant();
        if (actual != expected)
            throw new InvalidOperationException(
                $"{label} sha256 mismatch — expected {expected}, got {actual}. " +
                "Refusing to install a file that doesn't match the trusted digest.");
        logger?.Information("[TgProxy] {Label} sha256 verified", label);
    }

    private void VerifyPinnedSha256(string filePath, string expectedSha256, string label)
        => VerifyPinnedSha256Static(filePath, expectedSha256, label, _logger);

    /// <summary>Download the VPNRouter-tested proxy source tag.</summary>
    private async Task DownloadProxySourceAsync(CancellationToken ct)
    {
        // v2.36 (MVP one-button): final "Step 3/3:" group covers the
        // GitHub release fetch + zipball download + extract.
        StatusChanged?.Invoke("Step 3/3: Fetching proxy source from GitHub...");

        var url = $"{GitHubApiBase}/{ProxyRepo}/releases/tags/{SupportedProxyVersion}";
        var apiResp = await _http.SendAsync(
            new HttpRequest(HttpMethod.Get, new Uri(url), Headers: GitHubApiHeaders),
            ct).ConfigureAwait(false);
        if (!apiResp.IsSuccess())
            throw new HttpRequestException(
                $"HTTP {apiResp.StatusCode} fetching GitHub release info for {ProxyRepo}");
        using var doc = JsonDocument.Parse(apiResp.AsString());
        var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "unknown";
        if (!string.Equals(tagName, SupportedProxyVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"GitHub returned '{tagName}' for supported TgProxy '{SupportedProxyVersion}'.");

        _logger.Information("[TgProxy] Latest release: {Tag}", tagName);
        StatusChanged?.Invoke($"Step 3/3: Downloading proxy source {tagName}...");

        // Download source zipball
        var zipballUrl = doc.RootElement.GetProperty("zipball_url").GetString()
            ?? throw new Exception("No zipball_url in release");

        var tempZip = Path.Combine(Path.GetTempPath(), $"vpnr-tgproxy-source-{Guid.NewGuid():N}.zip");
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

            VerifyPinnedSha256(
                tempZip,
                SupportedProxySourceSha256,
                $"tg-ws-proxy {tagName} source");

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

                var stageRoot = Path.Combine(TgProxyDir, $".source-stage-{Guid.NewGuid():N}");
                var stagedProxyDir = Path.Combine(stageRoot, "proxy");
                var backupDir = Path.Combine(TgProxyDir, $".source-backup-{Guid.NewGuid():N}");
                Directory.CreateDirectory(stageRoot);
                try
                {
                    CopyDirectory(sourceProxyDir, stagedProxyDir);
                    await SmokeTestAsync(stageRoot, ct).ConfigureAwait(false);
                    ActivateSource(stagedProxyDir, backupDir, tagName);
                }
                finally
                {
                    try { if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true); } catch { }
                }

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

    private async Task SmokeTestAsync(string stageRoot, CancellationToken ct)
    {
        var python = "import sys; sys.path.insert(0, " +
                     JsonSerializer.Serialize(stageRoot) +
                     "); import certifi; import proxy.tg_ws_proxy";
        var probe = await _processRunner.RunAsync(new ProcessRequest(
            PythonExePath,
            ["-c", python],
            WorkingDirectory: TgProxyDir,
            CaptureStdout: true,
            CaptureStderr: true,
            Timeout: TimeSpan.FromSeconds(15)), ct).ConfigureAwait(false);

        if (probe.TimedOut || probe.ExitCode != 0)
            throw new InvalidOperationException(
                $"TgProxy {SupportedProxyVersion} smoke test failed (exit {probe.ExitCode}).");
    }

    internal void ActivateSource(string stagedProxyDir, string backupDir, string tagName)
        => ActivateSourceAt(ProxySourceDir, VersionFilePath, stagedProxyDir, backupDir, tagName, _logger);

    internal static void ActivateSourceAt(
        string currentProxyDir,
        string versionFilePath,
        string stagedProxyDir,
        string backupDir,
        string tagName,
        ILogger? logger = null)
    {
        var movedOld = false;
        var installedNew = false;
        var versionTemp = versionFilePath + ".tmp";
        try
        {
            if (Directory.Exists(currentProxyDir))
            {
                Directory.Move(currentProxyDir, backupDir);
                movedOld = true;
            }

            Directory.Move(stagedProxyDir, currentProxyDir);
            installedNew = true;
            File.WriteAllText(versionTemp, tagName);
            File.Move(versionTemp, versionFilePath, overwrite: true);
        }
        catch (Exception activationError)
        {
            try
            {
                try { if (File.Exists(versionTemp)) File.Delete(versionTemp); } catch { }
                if (installedNew && Directory.Exists(currentProxyDir))
                    Directory.Delete(currentProxyDir, recursive: true);
                if (movedOld && Directory.Exists(backupDir))
                    Directory.Move(backupDir, currentProxyDir);
            }
            catch (Exception rollbackError)
            {
                logger?.Error(
                    rollbackError,
                    "[TgProxy] Source rollback failed; recovery backup remains at {BackupDir}",
                    backupDir);
                throw new InvalidOperationException(
                    "TgProxy source activation and rollback failed. The previous runtime was preserved for recovery.",
                    new AggregateException(activationError, rollbackError));
            }
            throw;
        }

        try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true); } catch { }
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
