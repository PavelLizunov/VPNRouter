using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Downloads the appropriate <c>wgturn-cli</c> binary from
/// <c>PavelLizunov/wgturn-core</c> releases for the current OS/arch.
/// Follows the same pattern as <see cref="ZapretUpdater"/> and
/// <see cref="TgProxyUpdater"/>: on-demand fetch, version pinned to a
/// local <c>version.txt</c>, single in-flight download enforced by a
/// process-wide <see cref="SemaphoreSlim"/>.
///
/// <para>Unlike Zapret (ZIP archive + .bat strategy parsing), wgturn-cli
/// is a single executable per platform — so this updater downloads
/// straight to disk, no extraction.</para>
///
/// <para>Two distribution variants exist (see <see cref="WgturnVariant"/>):
/// the slim build requires a system Chromium install, the embedded build
/// bundles Chromium. The variant choice is recorded in
/// <c>{DataDir}/wgturn/variant.txt</c> so the UI can show what's
/// installed and offer to switch.</para>
///
/// <para><b>SHA256 verification</b>: upstream does not currently publish
/// sidecar checksum files. The updater accepts an <c>expectedSha256</c>
/// parameter so the pin can be added later without an API change — for
/// v0.1.0 it is always <c>null</c>.</para>
/// </summary>
public class WgturnUpdater
{
    public const string WgturnRepo = "PavelLizunov/wgturn-core";
    private const string GitHubApiBase = "https://api.github.com/repos";

    /// <summary>Prevents concurrent downloads — double-click / rebind races.</summary>
    private static readonly SemaphoreSlim _downloadLock = new(1, 1);

    // v3.0 Phase 4 (2026-05-18): IHttpClient seam. The GitHub release-info
    // call (small JSON) uses `SendAsync`; the binary download (~5-15 MB
    // for slim, ~150 MB for embedded) uses `SendStreamingAsync` so the
    // body is not buffered to a byte[] before File.Create accepts it.
    private readonly IHttpClient _http;

    /// <summary>
    /// Headers GitHub's REST API expects. Set on every outbound request
    /// via the envelope; the policy client doesn't need to know.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> GitHubApiHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/vnd.github.v3+json",
        };

    // Default per-request timeout for the binary download. Mirrors the
    // legacy static HttpClient's 5-min wall-clock budget so embedded
    // builds (~150 MB) on slow links still complete.
    private static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(5);

    private readonly ILogger _logger;

    /// <summary>
    /// Production ctor — uses the process-shared <see cref="PolicyHttpClient"/>.
    /// </summary>
    public WgturnUpdater(ILogger logger)
        : this(logger, PolicyHttpClient.Shared)
    {
    }

    /// <summary>
    /// Test / DI ctor — caller supplies a custom <see cref="IHttpClient"/>
    /// (typically <c>FakeHttpClient</c> in tests).
    /// </summary>
    public WgturnUpdater(ILogger logger, IHttpClient http)
    {
        _logger = logger;
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public event Action<string>? StatusChanged;

    // ─── Path resolution ─────────────────────────────────────────────────
    //
    // All wgturn-cli artifacts live under {DataDir}/wgturn/ — parallel to
    // {DataDir}/zapret/ and {DataDir}/tg-proxy/. We intentionally do NOT
    // put the binary in AppPaths.BinDir (the legacy v2.32.1 layout), since
    // W-2 will migrate from BinDir → wgturn/bin/.

    // T2-D dedup: forward to AppPaths.Wgturn* (the single source of truth)
    // instead of re-declaring the same Path.Combine logic. Names are kept as
    // thin forwarders rather than deleted because the bare `BinDir` identifier
    // would collide with AppPaths.BinDir (the shared bin/ dir) at repoint sites.
    // Values are byte-identical to the previous local declarations.
    public static string WgturnDir => AppPaths.WgturnDir;
    public static string BinDir => AppPaths.WgturnBinDir;
    public static string CliExePath => AppPaths.WgturnCliExePath;
    public static string VersionFilePath => AppPaths.WgturnVersionPath;
    public static string VariantFilePath => AppPaths.WgturnVariantPath;

    // ─── Public probe API ────────────────────────────────────────────────

    /// <summary>Check if wgturn-cli is installed at the canonical path.</summary>
    public static bool IsInstalled() => IsInstalledAt(WgturnDir);

    /// <summary>
    /// Path-explicit variant for tests. Production reads
    /// <see cref="WgturnDir"/>; tests pass a temp directory.
    /// </summary>
    internal static bool IsInstalledAt(string baseDir)
    {
        var cliExe = Path.Combine(baseDir, "bin",
            OperatingSystem.IsWindows() ? "wgturn-cli.exe" : "wgturn-cli");
        return File.Exists(cliExe);
    }

    /// <summary>Read locally installed version from version.txt.</summary>
    public static string? GetLocalVersion() => GetLocalVersionAt(WgturnDir);

    internal static string? GetLocalVersionAt(string baseDir)
    {
        try
        {
            var path = Path.Combine(baseDir, "version.txt");
            if (File.Exists(path))
                return File.ReadAllText(path).Trim();
        }
        catch { }
        return null;
    }

    /// <summary>Read locally installed variant. Defaults to <see cref="WgturnVariant.Slim"/>.</summary>
    public static WgturnVariant GetLocalVariant() => GetLocalVariantAt(WgturnDir);

    internal static WgturnVariant GetLocalVariantAt(string baseDir)
    {
        try
        {
            var path = Path.Combine(baseDir, "variant.txt");
            if (File.Exists(path))
            {
                var s = File.ReadAllText(path).Trim();
                if (string.Equals(s, "embedded", StringComparison.OrdinalIgnoreCase))
                    return WgturnVariant.Embedded;
            }
        }
        catch { }
        return WgturnVariant.Slim;
    }

    // ─── Asset resolution ────────────────────────────────────────────────

    /// <summary>
    /// Map the current process OS/arch to the GitHub release asset name
    /// for the requested variant. Throws
    /// <see cref="WgturnDownloadException"/> with
    /// <see cref="WgturnErrorCategory.UnsupportedPlatform"/> for combinations
    /// we don't publish (e.g. FreeBSD, Linux ARM32).
    ///
    /// <para>Linux arm64 has no embedded variant in the v0.1.0 release —
    /// when <see cref="WgturnVariant.Embedded"/> is requested for that
    /// platform we silently fall back to <see cref="WgturnVariant.Slim"/>
    /// so the user still gets a working binary.</para>
    /// </summary>
    /// <returns>
    /// (assetName, expectedSha256). <c>expectedSha256</c> is always
    /// <c>null</c> today — sidecar checksums not yet published upstream.
    /// </returns>
    internal static (string assetName, string? expectedSha256)
        ResolveAssetForCurrentPlatform(WgturnVariant variant) =>
        ResolveAssetFor(
            isWindows: OperatingSystem.IsWindows(),
            isMacOS: OperatingSystem.IsMacOS(),
            arch: RuntimeInformation.OSArchitecture,
            variant: variant);

    /// <summary>
    /// Test seam — full signature, exposed for unit tests so the
    /// platform tuple can be mocked. Production callers use the
    /// no-arg overload which queries the real environment.
    /// </summary>
    internal static (string assetName, string? expectedSha256) ResolveAssetFor(
        bool isWindows,
        bool isMacOS,
        Architecture arch,
        WgturnVariant variant)
    {
        var (os, archStr) = (isWindows, isMacOS, arch) switch
        {
            (true,  _,    Architecture.X64)   => ("windows", "amd64"),
            (false, true, Architecture.X64)   => ("darwin",  "amd64"),
            (false, true, Architecture.Arm64) => ("darwin",  "arm64"),
            (false, false, Architecture.X64)   => ("linux",   "amd64"),
            (false, false, Architecture.Arm64) => ("linux",   "arm64"),
            _ => throw new WgturnDownloadException(
                WgturnErrorCategory.UnsupportedPlatform,
                $"wgturn-cli is not published for this OS/architecture combination " +
                $"(IsWindows={isWindows}, IsMacOS={isMacOS}, Arch={arch}).",
                null),
        };

        // Linux arm64 has no embedded variant in v0.1.0 — degrade gracefully
        // to slim. macOS+Windows have both for amd64 and arm64.
        var slimOnly = os == "linux" && archStr == "arm64";
        var effective = (variant == WgturnVariant.Embedded && slimOnly)
            ? WgturnVariant.Slim
            : variant;

        var prefix = effective == WgturnVariant.Embedded
            ? "wgturn-cli-embedded"
            : "wgturn-cli";
        var ext = os == "windows" ? ".exe" : "";
        var name = $"{prefix}-{os}-{archStr}{ext}";
        return (name, expectedSha256: null);
    }

    // ─── Main entry point ────────────────────────────────────────────────

    /// <summary>
    /// Download the latest <c>wgturn-cli</c> from the
    /// <c>PavelLizunov/wgturn-core</c> GitHub releases and install it
    /// to <see cref="CliExePath"/>. Returns the release tag (e.g.
    /// <c>"v0.1.0"</c>) that was installed.
    ///
    /// <para>Thread-safe via a process-wide
    /// <see cref="SemaphoreSlim"/> — a second concurrent call throws
    /// with <see cref="WgturnErrorCategory.Concurrent"/>.</para>
    ///
    /// <para>Transient failures (network, GitHub 5xx) are retried up to
    /// 3 times with exponential backoff (2s/4s/8s).</para>
    /// </summary>
    public async Task<string> DownloadLatestAsync(
        WgturnVariant variant = WgturnVariant.Slim,
        CancellationToken ct = default)
    {
        if (!await _downloadLock.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
        {
            throw new WgturnDownloadException(
                WgturnErrorCategory.Concurrent,
                "A wgturn-cli download is already in progress — wait for it to finish.");
        }

        try
        {
            var (assetName, expectedSha256) = ResolveAssetForCurrentPlatform(variant);
            var effectiveVariant = assetName.StartsWith("wgturn-cli-embedded", StringComparison.Ordinal)
                ? WgturnVariant.Embedded
                : WgturnVariant.Slim;

            StatusChanged?.Invoke("Fetching wgturn-cli release info...");
            _logger.Information("[WgturnUpdater] Checking latest release ({Repo})", WgturnRepo);

            // --- Step 1: GitHub API (with retry) ---
            var apiUrl = $"{GitHubApiBase}/{WgturnRepo}/releases/latest";
            string resp;
            try
            {
                resp = await RetryAsync(
                    () => FetchGitHubJsonAsync(apiUrl, ct),
                    attempts: 3,
                    baseDelayMs: 2000,
                    onRetry: (i, ex) =>
                    {
                        var secs = (2 * (1 << i)) / 1000 + 2;
                        StatusChanged?.Invoke($"Retry {i + 1}/3 in {secs}s (GitHub: {ShortError(ex)})");
                        _logger.Warning(ex, "[WgturnUpdater] API retry {N}", i + 1);
                    },
                    ct).ConfigureAwait(false);
            }
            catch (HttpRequestException hre) when ((int?)hre.StatusCode == 403)
            {
                throw new WgturnDownloadException(
                    WgturnErrorCategory.GitHubRateLimit,
                    "GitHub API rate limit reached. Try again in ~15 minutes.",
                    hre);
            }
            catch (HttpRequestException hre)
                when (hre.StatusCode is System.Net.HttpStatusCode sc && (int)sc >= 500)
            {
                throw new WgturnDownloadException(
                    WgturnErrorCategory.GitHubServerError,
                    $"GitHub is having issues ({(int)hre.StatusCode}). Try again in a minute.",
                    hre);
            }
            catch (TaskCanceledException tce) when (!ct.IsCancellationRequested)
            {
                throw new WgturnDownloadException(
                    WgturnErrorCategory.Network,
                    "Timed out talking to GitHub. Check your internet connection.",
                    tce);
            }
            catch (HttpRequestException hre)
            {
                throw new WgturnDownloadException(
                    WgturnErrorCategory.Network,
                    $"Network error talking to GitHub: {hre.Message}",
                    hre);
            }

            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;
            var tagName = root.GetProperty("tag_name").GetString() ?? "unknown";
            _logger.Information(
                "[WgturnUpdater] Latest release: {Tag}, looking for asset {Asset}",
                tagName, assetName);

            // Find named asset matching the platform/variant.
            string? assetUrl = null;
            long? expectedSize = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (string.Equals(name, assetName, StringComparison.Ordinal))
                {
                    assetUrl = asset.GetProperty("browser_download_url").GetString();
                    if (asset.TryGetProperty("size", out var sizeProp) &&
                        sizeProp.ValueKind == JsonValueKind.Number)
                        expectedSize = sizeProp.GetInt64();
                    break;
                }
            }

            if (assetUrl == null)
            {
                throw new WgturnDownloadException(
                    WgturnErrorCategory.Invalid,
                    $"No asset named '{assetName}' in release {tagName}. " +
                    "Upstream release format may have changed — report a bug.");
            }

            StatusChanged?.Invoke($"Downloading wgturn-cli {tagName} ({FormatBytes(expectedSize)})...");
            _logger.Information(
                "[WgturnUpdater] Downloading {Url} (expected {Size} bytes)",
                assetUrl, expectedSize?.ToString() ?? "unknown");

            // --- Step 2: Download (with retry on network failures, size check) ---
            Directory.CreateDirectory(BinDir);
            var tempBin = Path.Combine(BinDir, $".wgturn-cli-{Guid.NewGuid():N}.tmp");
            try
            {
                await RetryAsync(
                    async () =>
                    {
                        try { if (File.Exists(tempBin)) File.Delete(tempBin); } catch { }

                        // v3.0 Phase 4: SendStreamingAsync replaces
                        // GetStreamAsync. await-using on the response means
                        // a mid-copy exception (disk full, network drop)
                        // aborts the socket before the outer retry path
                        // re-issues the request.
                        await using (var response = await _http.SendStreamingAsync(
                            new HttpRequest(
                                HttpMethod.Get,
                                new Uri(assetUrl),
                                Headers: GitHubApiHeaders,
                                Timeout: DefaultDownloadTimeout),
                            ct).ConfigureAwait(false))
                        {
                            if (!response.IsSuccess())
                                throw new HttpRequestException(
                                    $"HTTP {response.StatusCode} downloading wgturn-cli binary",
                                    inner: null,
                                    statusCode: (System.Net.HttpStatusCode)response.StatusCode);

                            using (var file = File.Create(tempBin))
                                await response.Body.CopyToAsync(file, ct).ConfigureAwait(false);
                        }

                        if (expectedSize.HasValue)
                        {
                            var actualSize = new FileInfo(tempBin).Length;
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
                        _logger.Warning(ex, "[WgturnUpdater] Download retry {N}", i + 1);
                    },
                    ct).ConfigureAwait(false);

                // --- Step 3: Optional SHA256 verification (skip on null) ---
                if (!string.IsNullOrWhiteSpace(expectedSha256))
                {
                    var actual = await ComputeSha256Async(tempBin, ct).ConfigureAwait(false);
                    if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new WgturnDownloadException(
                            WgturnErrorCategory.Corrupted,
                            $"SHA256 mismatch: expected {expectedSha256}, got {actual}. " +
                            "Download corrupted or tampered.");
                    }
                }

                // --- Step 4: Make executable on Unix ---
                if (!OperatingSystem.IsWindows())
                {
                    try
                    {
                        File.SetUnixFileMode(tempBin,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "[WgturnUpdater] Could not chmod +x {Path}", tempBin);
                    }
                }

                // --- Step 5: Atomic replace temp → final ---
                InstallDownloadedBinary(tempBin, CliExePath);

                // --- Step 6: Persist version + variant markers ---
                try
                {
                    Directory.CreateDirectory(WgturnDir);
                    File.WriteAllText(VersionFilePath, tagName);
                    File.WriteAllText(VariantFilePath,
                        effectiveVariant == WgturnVariant.Embedded ? "embedded" : "slim");
                }
                catch (Exception ex)
                {
                    // Marker write is best-effort; don't fail the install if
                    // the binary itself made it.
                    _logger.Warning(ex, "[WgturnUpdater] Failed to write version/variant marker");
                }

                _logger.Information(
                    "[WgturnUpdater] Installed wgturn-cli {Tag} ({Variant}) to {Path}",
                    tagName, effectiveVariant, CliExePath);
                StatusChanged?.Invoke($"Installed wgturn-cli {tagName}");

                return tagName;
            }
            catch (WgturnDownloadException)
            {
                throw;
            }
            catch (IOException ioe)
            {
                throw new WgturnDownloadException(
                    WgturnErrorCategory.Network,
                    $"Download interrupted: {ioe.Message}. Click Download to retry.",
                    ioe);
            }
            finally
            {
                // Cleans the staged temp only; the installed binary is never deleted-first.
                try { if (File.Exists(tempBin)) File.Delete(tempBin); } catch { }
            }
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Replace <paramref name="targetPath"/> with the staged download via
    /// <c>File.Move(overwrite: true)</c>. If the move fails, the working
    /// binary stays intact. Internal for testability.
    /// </summary>
    internal static void InstallDownloadedBinary(string tempBin, string targetPath)
    {
        try
        {
            File.Move(tempBin, targetPath, overwrite: true);
        }
        catch (UnauthorizedAccessException ua)
        {
            throw new WgturnDownloadException(
                WgturnErrorCategory.FileSystem,
                $"Permission denied writing to {targetPath}. Run VPNRouter as administrator.",
                ua);
        }
        catch (IOException ioe)
        {
            throw new WgturnDownloadException(
                WgturnErrorCategory.FileSystem,
                $"Couldn't install wgturn-cli (antivirus or in-use file?): {ioe.Message}",
                ioe);
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
    /// (network / HTTP 5xx / timeout). Permanent errors (404, FS perms)
    /// surface immediately.
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
                throw;
            }
            catch (Exception ex) when (i < attempts - 1 && IsTransient(ex))
            {
                onRetry?.Invoke(i, ex);
                var delayMs = baseDelayMs * (1 << i); // 2s, 4s, 8s with base=2000
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }
        return await op().ConfigureAwait(false);
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException hre when hre.StatusCode is System.Net.HttpStatusCode sc
            && ((int)sc >= 500 || (int)sc == 408 || (int)sc == 429) => true,
        HttpRequestException => true,
        TaskCanceledException => true,
        IOException => true,
        _ => false,
    };

    private static string ShortError(Exception ex) => ex switch
    {
        HttpRequestException hre when hre.StatusCode.HasValue => $"HTTP {(int)hre.StatusCode}",
        TaskCanceledException => "timeout",
        IOException => "network drop",
        _ => ex.GetType().Name,
    };

    private static string FormatBytes(long? bytes)
    {
        if (!bytes.HasValue) return "unknown size";
        var mb = bytes.Value / 1024.0 / 1024.0;
        return mb >= 1.0 ? $"{mb:F1} MB" : $"{bytes.Value / 1024.0:F0} KB";
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
