using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace VPNRouter.Core.Services;

public class TgProxyUpdater
{
    private const string ProxyRepo = "Flowseal/tg-ws-proxy";
    public const string SupportedProxyVersion = "v1.10.0";
    internal const string SupportedProxySourceSha256 =
        "62193af82c97d494264a0c6b744b37a7670c458cd1492e5e9ef0235298156327";

    private const string GitHubApiBase = "https://api.github.com/repos";
    internal const string PythonVersion = "3.14.10";
    internal const string PythonVersionMarkerFileName = ".vpnrouter-python-version";
    private const string PythonZipUrl = $"https://www.python.org/ftp/python/{PythonVersion}/python-{PythonVersion}-embed-amd64.zip";
    private const string PythonZipSha256 = "90905e64a840557d8c1bc4f5b74db3d7ee85e3ffb217611cac1c7813acea4159";
    private static readonly string PythonRuntimeDllName =
        "python" + string.Concat(PythonVersion.Split('.').Take(2)) + ".dll";

    private sealed record PinnedDependency(
        string Name,
        string Version,
        string FileName,
        string Sha256,
        string[] RequiredFiles,
        string? UniqueFilePattern = null,
        string? UniqueFile = null);

    private static readonly PinnedDependency[] PinnedDependencies =
    {
        new PinnedDependency(
            "pycparser", "3.0",
            "pycparser-3.0-py3-none-any.whl",
            "b727414169a36b7d524c1c3e31839a521725078d7b2ff038656844266160a992",
            new[] { Path.Combine("pycparser", "__init__.py") }),
        new PinnedDependency(
            "cffi", "2.1.1",
            "cffi-2.1.1-cp314-cp314-win_amd64.whl",
            "3222ba5d678f80a030e6afbcc33dc1ae5cb45facabb61cee2c7016b8432fde48",
            new[] { Path.Combine("cffi", "__init__.py") },
            UniqueFilePattern: "_cffi_backend.*.pyd",
            UniqueFile: "_cffi_backend.cp314-win_amd64.pyd"),
        new PinnedDependency(
            "cryptography", "50.0.2",
            "cryptography-50.0.2-cp311-abi3-win_amd64.whl",
            "7afa5a6602a9f29af1f3a2965f831bae7c9d5d597b7cbb716d41ab3b7d89879c",
            new[]
            {
                Path.Combine("cryptography", "__init__.py"),
                Path.Combine("cryptography", "hazmat", "bindings", "_rust.pyd"),
            }),
        new PinnedDependency(
            "certifi", "2026.11.20",
            "certifi-2026.11.20-py3-none-any.whl",
            "7422df25243767b9877031a2248cb14d09ddac9beee846f4cef3d5c9191d488f",
            new[]
            {
                Path.Combine("certifi", "__init__.py"),
                Path.Combine("certifi", "cacert.pem"),
            }),
    };

    private static readonly string _dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VPNRouter");

    private readonly ILogger _logger;

    private readonly IHttpClient _http;
    private readonly IProcessRunner _processRunner;

    private static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(10);

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

    public TgProxyUpdater(ILogger logger)
        : this(logger, PolicyHttpClient.Shared, new ProcessRunner())
    {
    }

    public TgProxyUpdater(ILogger logger, IHttpClient http, IProcessRunner? processRunner = null)
    {
        _logger = logger;
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _processRunner = processRunner ?? new ProcessRunner();
    }

    public static bool IsInstalled() => IsInstalledAt(TgProxyDir, logger: null);

    public static bool IsInstalled(ILogger? logger) => IsInstalledAt(TgProxyDir, logger);

    public static bool HasRuntimeFiles() =>
        File.Exists(PythonExePath) && Directory.Exists(ProxySourceDir);

    internal static bool IsInstalledAt(string baseDir, ILogger? logger = null)
    {
        var pythonExe = Path.Combine(baseDir, "python", "python.exe");
        var proxyDir = Path.Combine(baseDir, "proxy");
        var pythonExeExists = File.Exists(pythonExe);
        var proxySourceExists = Directory.Exists(proxyDir);
        var pythonCurrent = IsPythonCurrentAt(baseDir, logger);
        var dependenciesCurrent = IsDependenciesCurrentAt(baseDir, logger);
        var sourceCurrent = File.Exists(Path.Combine(proxyDir, "tg_ws_proxy.py"))
            && ReadVersionAt(baseDir) == SupportedProxyVersion;
        var overall = pythonExeExists && sourceCurrent && pythonCurrent && dependenciesCurrent
            && !Directory.Exists(Path.Combine(baseDir, ActivationDirectory));

        logger?.Information(
            "[TgProxy] IsInstalled: PythonExe at {PythonExePath} -> {PythonExeExists}, Python version current -> {PythonCurrent}, ProxySourceDir at {ProxySourceDir} -> {ProxySourceExists}, certifi/cryptography dependencies current -> {DependenciesCurrent}, overall = {Overall}",
            pythonExe, pythonExeExists, pythonCurrent, proxyDir, proxySourceExists, dependenciesCurrent, overall);

        return overall;
    }

    internal static bool IsPythonCurrentAt(string baseDir, ILogger? logger = null)
    {
        var pythonDir = Path.Combine(baseDir, "python");
        var pythonExe = Path.Combine(pythonDir, "python.exe");
        if (!File.Exists(pythonExe))
        {
            logger?.Information(
                "[TgProxy] Python version check: python.exe missing at {PythonExePath}", pythonExe);
            return false;
        }

        var runtimeDll = Path.Combine(pythonDir, PythonRuntimeDllName);
        if (!File.Exists(runtimeDll))
        {
            logger?.Information(
                "[TgProxy] Python version check: {PythonDllName} missing at {RuntimeDllPath}",
                PythonRuntimeDllName, runtimeDll);
            return false;
        }

        var markerPath = Path.Combine(pythonDir, PythonVersionMarkerFileName);
        try
        {
            if (!File.Exists(markerPath))
            {
                logger?.Information(
                    "[TgProxy] Python version check: version marker missing at {MarkerPath}; upgrade required",
                    markerPath);
                return false;
            }

            var installedVersion = File.ReadAllText(markerPath).Trim();
            if (!string.Equals(installedVersion, PythonVersion, StringComparison.Ordinal))
            {
                logger?.Information(
                    "[TgProxy] Python version check: expected {ExpectedVersion}, found {InstalledVersion}; upgrade required",
                    PythonVersion, installedVersion);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warning(ex, "[TgProxy] Python version check failed; upgrade required");
            return false;
        }
    }

    internal static bool IsDependenciesCurrentAt(string baseDir, ILogger? logger = null)
    {
        var libDir = Path.Combine(baseDir, "python", "Lib");
        if (!Directory.Exists(libDir))
        {
            logger?.Information("[TgProxy] Dependency check: Lib directory missing at {LibDir}", libDir);
            return false;
        }

        try
        {
            foreach (var package in PinnedDependencies)
            {
                foreach (var relativePath in package.RequiredFiles)
                {
                    var requiredPath = Path.Combine(libDir, relativePath);
                    if (!File.Exists(requiredPath))
                    {
                        logger?.Information(
                            "[TgProxy] Dependency check: {Package} {Version} is missing {RequiredPath}",
                            package.Name, package.Version, requiredPath);
                        return false;
                    }
                }

                var expectedDistInfo = $"{package.Name}-{package.Version}.dist-info";
                var distInfoDirs = Directory.GetDirectories(
                    libDir, $"{package.Name}-*.dist-info", SearchOption.TopDirectoryOnly);
                if (distInfoDirs.Length != 1 ||
                    !string.Equals(
                        Path.GetFileName(distInfoDirs[0]),
                        expectedDistInfo,
                        StringComparison.OrdinalIgnoreCase))
                {
                    logger?.Information(
                        "[TgProxy] Dependency check: {Package} {Version} is not current ({DistInfoCount} dist-info directories)",
                        package.Name, package.Version, distInfoDirs.Length);
                    return false;
                }

                if (package.UniqueFilePattern is not null && package.UniqueFile is not null)
                {
                    var uniqueFiles = Directory.GetFiles(
                        libDir, package.UniqueFilePattern, SearchOption.TopDirectoryOnly);
                    if (uniqueFiles.Length != 1 ||
                        !string.Equals(
                            Path.GetFileName(uniqueFiles[0]),
                            package.UniqueFile,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        logger?.Information(
                            "[TgProxy] Dependency check: {Package} ABI file is not {UniqueFile} ({MatchCount} matches)",
                            package.Name, package.UniqueFile, uniqueFiles.Length);
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warning(ex, "[TgProxy] Dependency check failed; update required");
            return false;
        }
    }

    public static string? GetLocalVersion() => ReadVersionAt(TgProxyDir);

    private static string? ReadVersionAt(string baseDir)
    {
        try
        {
            return File.ReadAllText(Path.Combine(baseDir, "version.txt")).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public async Task DownloadAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(TgProxyDir);

        using var updateLock = new FileStream(Path.Combine(TgProxyDir, ".update.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        RestoreInterruptedActivationAt(TgProxyDir, _logger);

        var stage = Path.Combine(TgProxyDir, $".stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        try
        {
            var python = PythonExePath;
            if (!IsPythonCurrentAt(TgProxyDir, _logger) || !IsDependenciesCurrentAt(TgProxyDir, _logger))
            {
                var stagedPython = Path.Combine(stage, "python");
                await StagePythonAsync(stagedPython, ct).ConfigureAwait(false);
                await InstallDependenciesAsync(stagedPython, ct).ConfigureAwait(false);
                File.WriteAllText(Path.Combine(stagedPython, PythonVersionMarkerFileName), PythonVersion);
                if (!IsPythonCurrentAt(stage) || !IsDependenciesCurrentAt(stage))
                    throw new InvalidDataException("The downloaded Python runtime is incomplete.");
                python = Path.Combine(stagedPython, "python.exe");
            }

            await StageProxySourceAsync(stage, ct).ConfigureAwait(false);
            await SmokeTestAsync(stage, python, ct).ConfigureAwait(false);
            File.WriteAllText(Path.Combine(stage, "version.txt"), SupportedProxyVersion);
            ct.ThrowIfCancellationRequested();
            ActivateRuntimeAt(TgProxyDir, stage, _logger);
            StatusChanged?.Invoke($"Installed {SupportedProxyVersion}");
        }
        finally
        {
            DeleteStage(stage, _logger);
        }
    }

    private async Task StagePythonAsync(string stagedPythonDir, CancellationToken ct)
    {
        StatusChanged?.Invoke($"Step 1/3: Downloading Python {PythonVersion} (~12 MB)...");
        _logger.Information("[TgProxy] Downloading Python embeddable: {Url}", PythonZipUrl);

        var tempZip = Path.Combine(Path.GetTempPath(), $"vpnr-tgproxy-python-{Guid.NewGuid():N}.zip");
        try
        {
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

            VerifyPinnedSha256(tempZip, PythonZipSha256, $"Python {PythonVersion} embeddable");

            StatusChanged?.Invoke("Step 1/3: Extracting Python...");
            ZipFile.ExtractToDirectory(tempZip, stagedPythonDir, overwriteFiles: true);
            PatchPythonPathAt(stagedPythonDir, _logger);
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }
    }

    private static void PatchPythonPathAt(string pythonDir, ILogger? logger = null)
    {
        var pthFiles = Directory.GetFiles(pythonDir, "python*._pth");
        if (pthFiles.Length == 0)
            throw new InvalidOperationException("Python embeddable does not contain a ._pth file.");

        foreach (var pthFile in pthFiles)
        {
            var lines = File.ReadAllLines(pthFile).ToList();

            if (!lines.Contains(".."))
                lines.Add("..");

            if (!lines.Contains("Lib"))
                lines.Add("Lib");

            File.WriteAllLines(pthFile, lines);
            logger?.Information("[TgProxy] Patched {File}", Path.GetFileName(pthFile));
        }
    }

    private async Task InstallDependenciesAsync(string stagedPythonDir, CancellationToken ct)
    {
        var stageLib = Path.Combine(stagedPythonDir, "Lib");
        Directory.CreateDirectory(stageLib);
        foreach (var package in PinnedDependencies)
        {
            StatusChanged?.Invoke($"Step 2/3: Installing {package.Name}...");
            _logger.Information(
                "[TgProxy] Downloading {Package} {Version}...",
                package.Name, package.Version);

            var pypiUrl = $"https://pypi.org/pypi/{package.Name}/{package.Version}/json";
            var pypiResp = await _http.SendAsync(
                new HttpRequest(HttpMethod.Get, new Uri(pypiUrl)),
                ct).ConfigureAwait(false);
            if (!pypiResp.IsSuccess())
                throw new HttpRequestException(
                    $"HTTP {pypiResp.StatusCode} fetching PyPI metadata for {package.Name}");
            using var doc = JsonDocument.Parse(pypiResp.AsString());

            string? wheelUrl = null, wheelSha256 = null;
            foreach (var urlEntry in doc.RootElement.GetProperty("urls").EnumerateArray())
            {
                var filename = urlEntry.GetProperty("filename").GetString() ?? "";
                if (string.Equals(filename, package.FileName, StringComparison.Ordinal))
                {
                    wheelUrl = urlEntry.GetProperty("url").GetString();
                    wheelSha256 = ReadPypiSha256(urlEntry);
                    break;
                }
            }

            if (wheelUrl == null)
                throw new Exception(
                    $"Could not find pinned wheel {package.FileName} for {package.Name} {package.Version}");
            if (!string.Equals(wheelSha256, package.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"PyPI metadata digest changed for pinned {package.Name} {package.Version}. Refusing install.");

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
                            $"HTTP {response.StatusCode} downloading {package.Name} wheel");

                    using var file = File.Create(tempWhl);
                    await response.Body.CopyToAsync(file, ct).ConfigureAwait(false);
                }

                VerifyPinnedSha256(tempWhl, package.Sha256, $"{package.Name} {package.Version} wheel");

                ZipFile.ExtractToDirectory(tempWhl, stageLib, overwriteFiles: true);
                _logger.Information(
                    "[TgProxy] {Package} {Version} staged",
                    package.Name, package.Version);
            }
            finally
            {
                try { File.Delete(tempWhl); } catch { }
            }
        }
    }

    private static string? ReadPypiSha256(JsonElement urlEntry)
        => urlEntry.TryGetProperty("digests", out var digests)
           && digests.TryGetProperty("sha256", out var sha)
           && sha.ValueKind == JsonValueKind.String
            ? sha.GetString()
            : null;

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

    private async Task StageProxySourceAsync(string stageRoot, CancellationToken ct)
    {
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

        _logger.Information("[TgProxy] Pinned release: {Tag}", tagName);
        StatusChanged?.Invoke($"Step 3/3: Downloading proxy source {tagName}...");

        var zipballUrl = doc.RootElement.GetProperty("zipball_url").GetString()
            ?? throw new Exception("No zipball_url in release");

        var tempZip = Path.Combine(Path.GetTempPath(), $"vpnr-tgproxy-source-{Guid.NewGuid():N}.zip");
        try
        {
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

            var tempDir = Path.Combine(Path.GetTempPath(), $"tgproxy-{Guid.NewGuid():N}");
            try
            {
                ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

                var extractedRoot = tempDir;
                var subdirs = Directory.GetDirectories(tempDir);
                if (subdirs.Length == 1)
                    extractedRoot = subdirs[0];

                var sourceProxyDir = Path.Combine(extractedRoot, "proxy");
                if (!Directory.Exists(sourceProxyDir))
                    throw new Exception("proxy/ directory not found in source");

                CopyDirectory(sourceProxyDir, Path.Combine(stageRoot, "proxy"));
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

    private async Task SmokeTestAsync(string stageRoot, string pythonExe, CancellationToken ct)
    {
        const string probeScript = """
            import sys
            from pathlib import Path
            from importlib.metadata import version
            stage = Path(sys.argv[1])
            lib = Path(sys.executable).parent / "Lib"
            sys.path[:0] = [str(stage), str(lib)]
            import certifi, cffi, cryptography, pycparser, _cffi_backend
            from cryptography.hazmat.bindings import _rust
            from cryptography.hazmat.primitives import hashes
            from cryptography.hazmat.primitives.ciphers import algorithms
            from proxy import tg_ws_proxy
            assert tg_ws_proxy.__file__ and Path(tg_ws_proxy.__file__).resolve() == stage / "proxy" / "tg_ws_proxy.py"
            for module in (certifi, cffi, cryptography, pycparser, _cffi_backend, _rust):
                assert Path(module.__file__).resolve().is_relative_to(lib), module.__name__
            assert sys.version_info[:3] == tuple(map(int, sys.argv[2].split(".")))
            for pin in sys.argv[3:]:
                name, expected = pin.split("=")
                assert version(name) == expected, name
            cffi.FFI()
            hashes.Hash(hashes.SHA256()).update(b"x")
            algorithms.AES(b"0" * 32)
            """;
        string[] args = ["-B", "-c", probeScript, stageRoot, PythonVersion,
            .. PinnedDependencies.Select(package => $"{package.Name}={package.Version}")];
        var probe = await _processRunner.RunAsync(new ProcessRequest(
            pythonExe, args,
            WorkingDirectory: stageRoot,
            CaptureStdout: true,
            CaptureStderr: true,
            Timeout: TimeSpan.FromSeconds(15)), ct).ConfigureAwait(false);

        if (probe.TimedOut || probe.ExitCode != 0)
        {
            var stderr = probe.Stderr.Trim();
            if (stderr.Length > 500) stderr = stderr[..500];
            var detail = stderr.Length == 0 ? string.Empty : $" {stderr}";
            throw new InvalidOperationException(
                $"TgProxy {SupportedProxyVersion} smoke test failed (exit {probe.ExitCode}).{detail}");
        }
    }

    private const string ActivationDirectory = ".activating";
    private static readonly string[] RuntimeEntries = ["python", "proxy", "version.txt"];

    internal static void ActivateRuntimeAt(string root, string stage, ILogger? logger = null)
    {
        var previous = Path.Combine(stage, ".previous");
        Directory.CreateDirectory(previous);
        foreach (var name in RuntimeEntries)
        {
            if (Path.Exists(Path.Combine(stage, name)) && !Path.Exists(Path.Combine(root, name)))
                File.WriteAllText(Path.Combine(previous, name + ".absent"), "");
        }

        // A fixed journal survives process termination between same-volume renames.
        var journal = Path.Combine(root, ActivationDirectory);
        Directory.Move(stage, journal);
        try
        {
            foreach (var name in RuntimeEntries)
            {
                var staged = Path.Combine(journal, name);
                if (!Path.Exists(staged)) continue;
                var live = Path.Combine(root, name);
                if (Path.Exists(live))
                    MoveEntry(live, Path.Combine(journal, ".previous", name));
                MoveEntry(staged, live);
            }
            Directory.Move(journal, stage);
        }
        catch
        {
            RestoreInterruptedActivationAt(root, logger);
            throw;
        }
    }

    internal static void RestoreInterruptedActivationAt(string root, ILogger? logger = null)
    {
        var journal = Path.Combine(root, ActivationDirectory);
        if (!Directory.Exists(journal)) return;

        foreach (var name in RuntimeEntries)
        {
            var prior = Path.Combine(journal, ".previous", name);
            var live = Path.Combine(root, name);
            var staged = Path.Combine(journal, name);
            if (!Path.Exists(prior) && !File.Exists(prior + ".absent")) continue;
            if (!Path.Exists(staged) && Path.Exists(live)) MoveEntry(live, staged);
            if (Path.Exists(prior)) MoveEntry(prior, live);
        }

        var restored = Path.Combine(root, $".restored-{Guid.NewGuid():N}");
        Directory.Move(journal, restored);
        DeleteStage(restored, logger);
    }

    private static void MoveEntry(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    private static void DeleteStage(string path, ILogger? logger)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.Warning(ex, "[TgProxy] Inactive runtime files retained at {Path}", path);
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
