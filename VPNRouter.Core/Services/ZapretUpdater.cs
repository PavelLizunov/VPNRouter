using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Exception thrown when a Zapret download hits an actionable failure we can
/// describe to the user (category + human-readable message). UI layer should
/// display <see cref="Message"/> directly instead of wrapping again.
/// </summary>
public sealed class ZapretDownloadException : Exception
{
    public ZapretErrorCategory Category { get; }
    public ZapretDownloadException(ZapretErrorCategory category, string message, Exception? inner = null)
        : base(message, inner) => Category = category;
}

public enum ZapretErrorCategory
{
    /// <summary>GitHub API rate-limited us (403). Transient, time-based.</summary>
    GitHubRateLimit,
    /// <summary>GitHub server error (5xx). Transient, retry-friendly.</summary>
    GitHubServerError,
    /// <summary>Network drop / DNS / timeout. Transient, user-dependent.</summary>
    Network,
    /// <summary>Downloaded bytes don't match Content-Length or ZIP is malformed.</summary>
    Corrupted,
    /// <summary>Release structure unexpected (no .zip asset, no bin/winws.exe).</summary>
    Invalid,
    /// <summary>Antivirus / file system / permission issue during extract.</summary>
    FileSystem,
    /// <summary>Another Download already in progress.</summary>
    Concurrent,
    /// <summary>Everything else.</summary>
    Unknown,
}

/// <summary>
/// Downloads Flowseal zapret-discord-youtube releases from GitHub
/// and parses .bat strategy files into winws.exe argument strings.
/// </summary>
public class ZapretUpdater
{
    private const string FlowsealRepo = "Flowseal/zapret-discord-youtube";

    /// <summary>
    /// r37 — exposed for the start-flow auto-update check. The MVM's
    /// ZapretOneClickAsync uses <see cref="RemoteVersionChecker.GetLatestTagAsync"/>
    /// with this repo string to detect newer upstream releases.
    /// </summary>
    public const string FlowsealRepoPublic = FlowsealRepo;
    private const string GitHubApiBase = "https://api.github.com/repos";

    /// <summary>Prevents concurrent downloads — double-click / rebind races.</summary>
    private static readonly SemaphoreSlim _downloadLock = new(1, 1);

    private static readonly string _dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VPNRouter");

    private readonly ILogger _logger;

    // v3.0 Phase 4 (2026-05-18): IHttpClient seam. The release-list call
    // (small JSON) goes through `SendAsync` (buffered); the ZIP download
    // (~3.5 MB) goes through `SendStreamingAsync` so the body is not
    // buffered into a byte[] before the file write — keeps memory flat
    // even on slow filesystems where the kernel can't drain the socket
    // as fast as Github can send.
    private readonly IHttpClient _http;

    // Default per-request timeout for ZIP downloads. The legacy static
    // HttpClient used a 5-min "wall clock" timeout; here we keep the
    // same behaviour by setting it on the HttpRequest envelope so the
    // shared client's 30-s default doesn't choke a large download.
    private static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(5);

    public static string ZapretDir => Path.Combine(_dataDir, "zapret");
    public static string BinDir => Path.Combine(ZapretDir, "bin");
    public static string ListsDir => Path.Combine(ZapretDir, "lists");
    public static string WinwsExePath => Path.Combine(BinDir, "winws.exe");
    public static string VersionFilePath => Path.Combine(ZapretDir, "version.txt");

    public event Action<string>? StatusChanged;

    /// <summary>
    /// Production ctor — uses the process-shared <see cref="PolicyHttpClient"/>.
    /// </summary>
    public ZapretUpdater(ILogger logger)
        : this(logger, PolicyHttpClient.Shared)
    {
    }

    /// <summary>
    /// Test / DI ctor — caller supplies a custom <see cref="IHttpClient"/>
    /// (typically <c>FakeHttpClient</c> in tests).
    /// </summary>
    public ZapretUpdater(ILogger logger, IHttpClient http)
    {
        _logger = logger;
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Headers GitHub's REST API expects on every request. Applied to the
    /// envelope so the policy client doesn't have to know about them.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> GitHubApiHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/vnd.github.v3+json",
        };

    /// <summary>Check if Flowseal zapret is installed (winws.exe exists).</summary>
    public static bool IsInstalled() => File.Exists(WinwsExePath);

    /// <summary>Read locally installed version from version.txt.</summary>
    public static string? GetLocalVersion()
    {
        try
        {
            if (File.Exists(VersionFilePath))
                return File.ReadAllText(VersionFilePath).Trim();

            // Fallback: parse from service.bat
            var serviceBat = Path.Combine(ZapretDir, "service.bat");
            if (File.Exists(serviceBat))
            {
                foreach (var line in File.ReadLines(serviceBat).Take(5))
                {
                    var m = Regex.Match(line, @"LOCAL_VERSION=(.+)""");
                    if (m.Success) return m.Groups[1].Value.Trim();
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Download and extract the latest Flowseal release.
    /// Thread-safe via <see cref="_downloadLock"/> — second concurrent call throws
    /// <see cref="ZapretDownloadException"/> with <see cref="ZapretErrorCategory.Concurrent"/>.
    /// Transient failures (network, GitHub 5xx) are retried up to 3 times with
    /// exponential backoff (2s/4s/8s). Partial temp files are cleaned up pre-flight.
    /// </summary>
    public async Task DownloadAndExtractAsync(CancellationToken ct)
    {
        // Service-level lock: second click while first is in flight gets a clear
        // "already downloading" message instead of racing on ZIP extract.
        if (!await _downloadLock.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
        {
            throw new ZapretDownloadException(
                ZapretErrorCategory.Concurrent,
                "A download is already in progress — wait for it to finish.");
        }

        try
        {
            // Pre-flight: remove stale partial downloads from previous sessions
            // (antivirus handle, crash, user force-quit). Keeps %TEMP% from bloating.
            CleanupStaleTemps();

            StatusChanged?.Invoke("Fetching release info...");
            _logger.Information("[ZapretUpdater] Checking latest release");

            // --- Step 1: GitHub API (with retry) ---
            var apiUrl = $"{GitHubApiBase}/{FlowsealRepo}/releases/latest";
            string resp;
            try
            {
                resp = await RetryAsync(
                    () => FetchGitHubJsonAsync(apiUrl, ct),
                    attempts: 3,
                    baseDelayMs: 2000,
                    onRetry: (i, ex) =>
                    {
                        var secs = (2 * (1 << i)) / 1000 + 2; // 2,4,8
                        StatusChanged?.Invoke($"Retry {i + 1}/3 in {secs}s (GitHub: {ShortError(ex)})");
                        _logger.Warning(ex, "[ZapretUpdater] API retry {N}", i + 1);
                    },
                    ct).ConfigureAwait(false);
            }
            catch (HttpRequestException hre) when ((int?)hre.StatusCode == 403)
            {
                throw new ZapretDownloadException(
                    ZapretErrorCategory.GitHubRateLimit,
                    "GitHub API rate limit reached. Try again in ~15 minutes.",
                    hre);
            }
            catch (HttpRequestException hre) when (hre.StatusCode is System.Net.HttpStatusCode sc && (int)sc >= 500)
            {
                throw new ZapretDownloadException(
                    ZapretErrorCategory.GitHubServerError,
                    $"GitHub is having issues ({(int)hre.StatusCode}). Try again in a minute.",
                    hre);
            }
            catch (TaskCanceledException tce) when (!ct.IsCancellationRequested)
            {
                throw new ZapretDownloadException(
                    ZapretErrorCategory.Network,
                    "Timed out talking to GitHub. Check your internet connection.",
                    tce);
            }
            catch (HttpRequestException hre)
            {
                throw new ZapretDownloadException(
                    ZapretErrorCategory.Network,
                    $"Network error talking to GitHub: {hre.Message}",
                    hre);
            }

            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;
            var tagName = root.GetProperty("tag_name").GetString() ?? "unknown";
            _logger.Information("[ZapretUpdater] Latest release: {Tag}", tagName);

            // Find ZIP asset — prefer named .zip asset, fall back to zipball_url.
            string? zipUrl = null;
            long? expectedSize = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = asset.GetProperty("browser_download_url").GetString();
                    if (asset.TryGetProperty("size", out var sizeProp) && sizeProp.ValueKind == JsonValueKind.Number)
                        expectedSize = sizeProp.GetInt64();
                    break;
                }
            }
            if (zipUrl == null && root.TryGetProperty("zipball_url", out var zb))
                zipUrl = zb.GetString();

            if (zipUrl == null)
            {
                throw new ZapretDownloadException(
                    ZapretErrorCategory.Invalid,
                    "No ZIP asset found in the Flowseal release — upstream changed their release format.");
            }

            StatusChanged?.Invoke($"Downloading {tagName}...");
            _logger.Information("[ZapretUpdater] Downloading: {Url} (expected {Size} bytes)",
                zipUrl, expectedSize?.ToString() ?? "unknown");

            // --- Step 2: Download ZIP (with retry on network failures, size check) ---
            var tempZip = Path.Combine(Path.GetTempPath(), $"vpnr-zapret-{Guid.NewGuid():N}.zip");
            try
            {
                await RetryAsync(
                    async () =>
                    {
                        // Clean prior attempt's partial file
                        try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }

                        // v3.0 Phase 4: SendStreamingAsync replaces
                        // GetStreamAsync. The await-using on the response
                        // closes the socket cleanly even if the file write
                        // throws mid-copy — no half-read kernel buffer leak.
                        await using (var response = await _http.SendStreamingAsync(
                            new HttpRequest(
                                HttpMethod.Get,
                                new Uri(zipUrl),
                                Headers: GitHubApiHeaders,
                                Timeout: DefaultDownloadTimeout),
                            ct).ConfigureAwait(false))
                        {
                            if (!response.IsSuccess())
                                throw new HttpRequestException(
                                    $"HTTP {response.StatusCode} downloading ZIP",
                                    inner: null,
                                    statusCode: (System.Net.HttpStatusCode)response.StatusCode);

                            using (var file = File.Create(tempZip))
                                await response.Body.CopyToAsync(file, ct).ConfigureAwait(false);
                        }

                        // Verify size if GitHub told us what to expect (named assets do,
                        // zipball_url doesn't). Mismatch → force retry.
                        if (expectedSize.HasValue)
                        {
                            var actualSize = new FileInfo(tempZip).Length;
                            if (actualSize != expectedSize.Value)
                            {
                                throw new IOException(
                                    $"Partial download: got {actualSize} bytes, expected {expectedSize.Value}. " +
                                    "Network likely dropped mid-transfer.");
                            }
                        }
                        return true;
                    },
                    attempts: 3,
                    baseDelayMs: 2000,
                    onRetry: (i, ex) =>
                    {
                        var secs = (2 * (1 << i)) / 1000 + 2;
                        StatusChanged?.Invoke($"Retry {i + 1}/3 in {secs}s (download: {ShortError(ex)})");
                        _logger.Warning(ex, "[ZapretUpdater] Download retry {N}", i + 1);
                    },
                    ct).ConfigureAwait(false);

                var zipSize = new FileInfo(tempZip).Length;
                _logger.Information("[ZapretUpdater] Downloaded {Size} KB", zipSize / 1024);

                // --- Step 3: Extract + install ---
                StatusChanged?.Invoke("Extracting...");
                var tempDir = Path.Combine(Path.GetTempPath(), $"vpnr-zapret-extract-{Guid.NewGuid():N}");
                try
                {
                    _logger.Information("[ZapretUpdater] Extracting to {Dir}", tempDir);
                    try
                    {
                        ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);
                    }
                    catch (InvalidDataException ide)
                    {
                        // ZIP central-directory corrupt → almost always truncated download
                        // even though Content-Length matched (rare). Treat as corrupted.
                        throw new ZapretDownloadException(
                            ZapretErrorCategory.Corrupted,
                            "Downloaded file is corrupted (not a valid ZIP). Click Download to retry.",
                            ide);
                    }

                    var extractedRoot = tempDir;
                    var subdirs = Directory.GetDirectories(tempDir);
                    var rootFiles = Directory.GetFiles(tempDir);
                    _logger.Information("[ZapretUpdater] Extracted: {Dirs} dirs, {Files} files in root",
                        subdirs.Length, rootFiles.Length);

                    if (subdirs.Length == 1 && rootFiles.Length == 0)
                        extractedRoot = subdirs[0];

                    var testWinws = Path.Combine(extractedRoot, "bin", "winws.exe");
                    if (!File.Exists(testWinws))
                    {
                        var actualContents = string.Join(", ",
                            Directory.GetFileSystemEntries(extractedRoot).Select(Path.GetFileName));
                        throw new ZapretDownloadException(
                            ZapretErrorCategory.Invalid,
                            $"Release doesn't contain bin/winws.exe (found: [{actualContents}]). " +
                            "Upstream release format changed — report a bug.");
                    }

                    StopWinDivertService();

                    StatusChanged?.Invoke("Installing...");
                    bool allCopied;
                    try
                    {
                        Directory.CreateDirectory(ZapretDir);
                        allCopied = CopyDirectoryOverwrite(extractedRoot, ZapretDir, _logger);
                    }
                    catch (UnauthorizedAccessException ua)
                    {
                        throw new ZapretDownloadException(
                            ZapretErrorCategory.FileSystem,
                            $"Permission denied writing to {ZapretDir}. Run VPNRouter as administrator.",
                            ua);
                    }
                    catch (IOException ioe)
                    {
                        throw new ZapretDownloadException(
                            ZapretErrorCategory.FileSystem,
                            $"Couldn't install files (antivirus may be blocking): {ioe.Message}",
                            ioe);
                    }

                    if (allCopied)
                    {
                        var version = ParseVersionFromServiceBat() ?? tagName;
                        try { File.WriteAllText(VersionFilePath, version); } catch { }
                        _logger.Information("[ZapretUpdater] Installed version {Version}", version);
                        StatusChanged?.Invoke($"Installed {version}");
                    }
                    else
                    {
                        // ZAP-1: a locked file (in-use winws.exe / WinDivert64.sys) leaves a
                        // mixed old/new tree. Do NOT advance version.txt — keep the old marker
                        // so the next update check retries, and tell the user to stop zapret.
                        _logger.Warning("[ZapretUpdater] Some files were locked — version NOT updated. " +
                            "Stop zapret/winws and re-run the update to complete installation.");
                        StatusChanged?.Invoke("Partial install — some files locked. Stop zapret and retry.");
                    }
                }
                catch (ZapretDownloadException)
                {
                    throw; // already categorized, don't wrap again
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "[ZapretUpdater] Extract/install failed");
                    throw new ZapretDownloadException(
                        ZapretErrorCategory.Unknown,
                        $"Install failed: {ex.Message}",
                        ex);
                }
                finally
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
            catch (ZapretDownloadException)
            {
                throw;
            }
            catch (IOException ioe)
            {
                throw new ZapretDownloadException(
                    ZapretErrorCategory.Network,
                    $"Download interrupted: {ioe.Message}. Click Download to retry.",
                    ioe);
            }
            finally
            {
                try { File.Delete(tempZip); } catch { }
            }
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    /// <summary>
    /// Wrap <see cref="IHttpClient.SendAsync"/> for GitHub REST endpoints
    /// and surface a string body. Maps non-2xx to
    /// <see cref="HttpRequestException"/> (matching the legacy
    /// <c>HttpClient.GetStringAsync</c> contract) so the existing catch
    /// blocks for rate-limit / 5xx still trigger.
    /// </summary>
    private async Task<string> FetchGitHubJsonAsync(string url, CancellationToken ct)
    {
        var apiResp = await _http.SendAsync(
            new HttpRequest(
                HttpMethod.Get,
                new Uri(url),
                Headers: GitHubApiHeaders),
            ct).ConfigureAwait(false);
        if (!apiResp.IsSuccess())
        {
            throw new HttpRequestException(
                $"GitHub API HTTP {apiResp.StatusCode}",
                inner: null,
                statusCode: (System.Net.HttpStatusCode)apiResp.StatusCode);
        }
        return apiResp.AsString();
    }

    /// <summary>
    /// Exponential-backoff retry helper. Only retries on transient errors
    /// (network / HTTP 5xx / timeout). Permanent errors (404, ZIP corrupt,
    /// UnauthorizedAccessException) surface immediately.
    /// </summary>
    private static async Task<T> RetryAsync<T>(
        Func<Task<T>> op,
        int attempts,
        int baseDelayMs,
        Action<int, Exception>? onRetry,
        CancellationToken ct)
    {
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                return await op().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // user cancelled — don't retry
            }
            catch (Exception ex) when (i < attempts - 1 && IsTransient(ex))
            {
                onRetry?.Invoke(i, ex);
                var delayMs = baseDelayMs * (1 << i); // 2s, 4s, 8s with baseDelayMs=2000
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }
        // Last attempt — let exception bubble up for categorization above
        return await op().ConfigureAwait(false);
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException hre when hre.StatusCode is System.Net.HttpStatusCode sc
            && ((int)sc >= 500 || (int)sc == 408 || (int)sc == 429) => true,
        HttpRequestException => true, // connection refused, DNS, reset
        TaskCanceledException => true, // HttpClient timeout
        IOException => true, // copy stream interrupted mid-flight
        _ => false,
    };

    private static string ShortError(Exception ex) => ex switch
    {
        HttpRequestException hre when hre.StatusCode.HasValue => $"HTTP {(int)hre.StatusCode}",
        TaskCanceledException => "timeout",
        IOException => "network drop",
        _ => ex.GetType().Name,
    };

    /// <summary>
    /// Remove our own stale temp files (from previous crashed/interrupted runs).
    /// Scoped to files matching our naming scheme + older than 1h to avoid
    /// interfering with active parallel processes on shared machines.
    /// </summary>
    private void CleanupStaleTemps()
    {
        try
        {
            var tempRoot = Path.GetTempPath();
            var cutoff = DateTime.UtcNow.AddHours(-1);

            foreach (var f in Directory.GetFiles(tempRoot, "vpnr-zapret-*.zip"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < cutoff)
                    {
                        File.Delete(f);
                        _logger.Debug("[ZapretUpdater] Cleaned stale temp {File}", Path.GetFileName(f));
                    }
                }
                catch { /* file in use by AV, skip */ }
            }

            foreach (var d in Directory.GetDirectories(tempRoot, "vpnr-zapret-extract-*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(d) < cutoff)
                    {
                        Directory.Delete(d, recursive: true);
                        _logger.Debug("[ZapretUpdater] Cleaned stale temp dir {Dir}", Path.GetFileName(d));
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            // Cleanup is best-effort; never fail the download because of it.
            _logger.Debug("[ZapretUpdater] Temp cleanup skipped: {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// Stop the WinDivert kernel driver service so WinDivert64.sys can be
    /// overwritten on disk. Without this, we end up with mismatched winws.exe
    /// (new) + WinDivert64.sys (old in memory) → winws.exe crashes immediately.
    /// </summary>
    private void StopWinDivertService()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 1. Kill any winws.exe instances first — they use the driver
        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("winws"))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
                _logger.Information("[ZapretUpdater] Killed winws.exe (PID {Pid}) before update", proc.Id);
            }
            catch { }
            finally { proc.Dispose(); }
        }

        // 2. Try stopping known WinDivert service names. WinDivert registers under
        //    various names depending on version (WinDivert, WinDivert14, WinDivertXX).
        var serviceNames = new[] { "WinDivert", "WinDivert14", "WinDivert15", "windivert" };
        foreach (var name in serviceNames)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("sc", $"stop {name}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(5000);
                var stdout = p?.StandardOutput.ReadToEnd() ?? "";
                if (p?.ExitCode == 0 || stdout.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
                    _logger.Information("[ZapretUpdater] Stopped {Svc} driver service", name);
            }
            catch (Exception ex)
            {
                _logger.Debug("[ZapretUpdater] sc stop {Svc} failed: {Msg}", name, ex.Message);
            }
        }

        // 3. Also try to delete the service (frees the driver file immediately)
        foreach (var name in serviceNames)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("sc", $"delete {name}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(3000);
            }
            catch { }
        }

        // 4. Wait for kernel to unload the driver (2 seconds)
        System.Threading.Thread.Sleep(2000);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    /// <summary>
    /// Copy source tree into dest tree, overwriting files where possible,
    /// skipping files that are locked (e.g. WinDivert64.sys loaded as kernel driver).
    /// Returns <c>true</c> only when EVERY file copied; <c>false</c> when one or more
    /// files were skipped. Callers must gate the version marker on this result so a
    /// partial (mixed old/new) install is never reported as current (ZAP-1).
    /// </summary>
    internal static bool CopyDirectoryOverwrite(string source, string dest, ILogger logger)
    {
        Directory.CreateDirectory(dest);
        var allCopied = true;
        foreach (var file in Directory.GetFiles(source))
        {
            var destFile = Path.Combine(dest, Path.GetFileName(file));
            try
            {
                File.Copy(file, destFile, overwrite: true);
            }
            catch (Exception ex)
            {
                allCopied = false;
                logger.Warning("[ZapretUpdater] Skipped locked file {File}: {Msg}",
                    Path.GetFileName(file), ex.Message);
            }
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            if (!CopyDirectoryOverwrite(dir, Path.Combine(dest, Path.GetFileName(dir)), logger))
                allCopied = false;
        }
        return allCopied;
    }

    private static string? ParseVersionFromServiceBat()
    {
        var serviceBat = Path.Combine(ZapretDir, "service.bat");
        if (!File.Exists(serviceBat)) return null;
        try
        {
            foreach (var line in File.ReadLines(serviceBat).Take(5))
            {
                var m = Regex.Match(line, @"LOCAL_VERSION=(.+?)""");
                if (m.Success) return m.Groups[1].Value.Trim();
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Parse all .bat strategy files in the zapret directory.
    /// Returns list of strategy names and their pre-built winws.exe argument strings.
    /// </summary>
    public static List<ZapretStrategy> ParseStrategies()
    {
        var result = new List<ZapretStrategy>();
        if (!Directory.Exists(ZapretDir)) return result;

        // KEEP %BIN% and %LISTS% as-is in the argument string.
        // They will be set via SET commands in the launch .bat file.
        // CMD variable expansion handles quoting correctly for Cygwin.
        // Direct path substitution fails ("cannot access file").
        var binPath = "%BIN%";
        var listsPath = "%LISTS%";

        foreach (var batFile in Directory.GetFiles(ZapretDir, "general*.bat"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(batFile);
                var args = ExtractWinwsArgs(batFile, binPath, listsPath);
                if (!string.IsNullOrWhiteSpace(args))
                    result.Add(new ZapretStrategy(name, args, batFile));
            }
            catch (Exception ex)
            {
                // Skip unparseable files
                System.Diagnostics.Debug.WriteLine($"[ZapretUpdater] Failed to parse {batFile}: {ex.Message}");
            }
        }

        // Sort: "general (ALT3)" first (proven), then "general", then ALT1-11, then others
        result.Sort((a, b) =>
        {
            int Score(string n)
            {
                if (n == "general (ALT3)") return 0;
                if (n == "general") return 1;
                var m = Regex.Match(n, @"ALT(\d+)");
                if (m.Success) return 10 + int.Parse(m.Groups[1].Value);
                if (n.Contains("SIMPLE")) return 100;
                if (n.Contains("FAKE TLS")) return 200;
                return 50;
            }
            return Score(a.Name).CompareTo(Score(b.Name));
        });

        return result;
    }

    /// <summary>
    /// Extract winws.exe arguments from a Flowseal .bat file.
    /// Handles line continuations, variable substitution, and game filter stripping.
    /// </summary>
    private static string? ExtractWinwsArgs(string batPath, string binPath, string listsPath)
    {
        var lines = File.ReadAllLines(batPath);
        return ExtractWinwsArgsFromLines(lines, binPath, listsPath);
    }

    /// <summary>
    /// Pure-function variant of <see cref="ExtractWinwsArgs"/> for tests:
    /// takes in-memory .bat lines (no file system read) and returns the
    /// parsed arg string. Extracted in v3.0 Phase 2G (2026-05-18) so the
    /// strategy parser can be exercised without writing temp files.
    /// </summary>
    internal static string? ExtractWinwsArgsFromLines(string[] lines, string binPath, string listsPath)
    {

        // Find the "start" command line and join continuations
        var cmdBuilder = new System.Text.StringBuilder();
        bool inCommand = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            if (!inCommand)
            {
                if (line.Contains("winws.exe"))
                {
                    inCommand = true;
                    cmdBuilder.Append(line);
                    if (!line.EndsWith("^"))
                        break;
                    // Remove trailing ^
                    cmdBuilder.Length -= 1;
                }
                continue;
            }

            // Continuation line
            cmdBuilder.Append(' ');
            if (line.EndsWith("^"))
                cmdBuilder.Append(line, 0, line.Length - 1);
            else
            {
                cmdBuilder.Append(line);
                break;
            }
        }

        if (cmdBuilder.Length == 0) return null;

        var fullCmd = cmdBuilder.ToString();

        // Extract everything after winws.exe"
        var exeIdx = fullCmd.IndexOf("winws.exe\"", StringComparison.OrdinalIgnoreCase);
        if (exeIdx < 0)
            exeIdx = fullCmd.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx < 0) return null;

        // Skip past winws.exe"
        var afterExe = fullCmd.IndexOf('"', exeIdx);
        var argsStart = afterExe >= 0 ? afterExe + 1 : exeIdx + "winws.exe".Length;
        var args = fullCmd[argsStart..].Trim();

        // Variable substitution
        args = args.Replace("%BIN%", binPath, StringComparison.OrdinalIgnoreCase);
        args = args.Replace("%LISTS%", listsPath, StringComparison.OrdinalIgnoreCase);

        // Strip game filter variables from --wf-tcp/--wf-udp port lists
        // e.g. "--wf-tcp=80,443,%GameFilterTCP%" → "--wf-tcp=80,443"
        args = Regex.Replace(args, @",\s*%GameFilter\w+%", "", RegexOptions.IgnoreCase);
        args = Regex.Replace(args, @"%GameFilter\w+%\s*,?", "", RegexOptions.IgnoreCase);

        // Split into --new segments and filter out game-filter-only blocks
        // After GameFilter substitution, blocks like "--filter-tcp=%GameFilterTCP% ..."
        // become "--filter-tcp= ..." (empty port) which crashes winws.exe
        var segments = Regex.Split(args, @"\s+--new\s+");
        var validSegments = new List<string>();
        foreach (var seg in segments)
        {
            var trimmed = seg.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // Check if this segment has --filter-tcp= or --filter-udp= with empty port
            // e.g. "--filter-tcp=--ipset=..." or "--filter-tcp= --ipset=..."
            if (Regex.IsMatch(trimmed, @"--filter-(?:tcp|udp)=(?:\s|--)", RegexOptions.IgnoreCase))
                continue; // Skip: empty port filter = game filter block
            if (Regex.IsMatch(trimmed, @"--filter-(?:tcp|udp)=$", RegexOptions.IgnoreCase))
                continue; // Skip: trailing empty filter

            validSegments.Add(trimmed);
        }

        args = string.Join(" --new ", validSegments);

        // Clean up: collapse whitespace
        args = Regex.Replace(args, @"\s+", " ").Trim();

        // Remove double backslashes that Path.Combine might create
        args = args.Replace("\\\\", "\\");

        return args;
    }
}

public record ZapretStrategy(string Name, string Arguments, string? BatPath = null);
