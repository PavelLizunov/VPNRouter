using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace VPNRouter.Core.Services;

public sealed class ZapretDownloadException : Exception
{
    public ZapretErrorCategory Category { get; }
    public ZapretDownloadException(ZapretErrorCategory category, string message, Exception? inner = null)
        : base(message, inner) => Category = category;
}

public enum ZapretErrorCategory
{
    GitHubRateLimit,
    GitHubServerError,
    Network,
    Corrupted,
    Invalid,
    FileSystem,
    Concurrent,
    Unknown,
}

public class ZapretUpdater
{
    private const string FlowsealRepo = "Flowseal/zapret-discord-youtube";

    public const string FlowsealRepoPublic = FlowsealRepo;
    private const string GitHubApiBase = "https://api.github.com/repos";

    private static readonly SemaphoreSlim _downloadLock = new(1, 1);

    private static readonly string _dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VPNRouter");

    private readonly ILogger _logger;

    private readonly IHttpClient _http;

    private static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(5);

    public static string ZapretDir => Path.Combine(AppPaths.DataDir, "zapret");
    public static string BinDir => Path.Combine(ZapretDir, "bin");
    public static string ListsDir => Path.Combine(ZapretDir, "lists");
    public static string WinwsExePath => Path.Combine(BinDir, "winws.exe");
    public static string VersionFilePath => Path.Combine(ZapretDir, "version.txt");

    public event Action<string>? StatusChanged;

    public ZapretUpdater(ILogger logger)
        : this(logger, PolicyHttpClient.Shared)
    {
    }

    public ZapretUpdater(ILogger logger, IHttpClient http)
    {
        _logger = logger;
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    private static readonly IReadOnlyDictionary<string, string> GitHubApiHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/vnd.github.v3+json",
        };

    public static bool IsInstalled() => File.Exists(WinwsExePath);

    public static string? GetLocalVersion()
    {
        try
        {
            if (File.Exists(VersionFilePath))
                return File.ReadAllText(VersionFilePath).Trim();

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

    public async Task DownloadAndExtractAsync(CancellationToken ct)
    {
        if (!await _downloadLock.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
        {
            throw new ZapretDownloadException(
                ZapretErrorCategory.Concurrent,
                "A download is already in progress — wait for it to finish.");
        }

        try
        {
            CleanupStaleTemps();

            StatusChanged?.Invoke("Fetching release info...");
            _logger.Information("[ZapretUpdater] Checking latest release");

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
                        var secs = (2 * (1 << i)) / 1000 + 2;
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

            var tempZip = Path.Combine(Path.GetTempPath(), $"vpnr-zapret-{Guid.NewGuid():N}.zip");
            try
            {
                await RetryAsync(
                    async () =>
                    {
                        try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }

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
                        _logger.Warning("[ZapretUpdater] Some files were locked — version NOT updated. " +
                            "Stop zapret/winws and re-run the update to complete installation.");
                        StatusChanged?.Invoke("Partial install — some files locked. Stop zapret and retry.");
                    }
                }
                catch (ZapretDownloadException)
                {
                    throw;
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
                var delayMs = baseDelayMs * (1 << i);
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
                catch {  }
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
            _logger.Debug("[ZapretUpdater] Temp cleanup skipped: {Msg}", ex.Message);
        }
    }

    private void StopWinDivertService()
    {
        if (!OperatingSystem.IsWindows()) return;

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

        var scPath = OperatingSystem.IsWindows() ? WindowsServiceCommand.GetSystemScPath() : "sc";
        var serviceNames = new[] { "WinDivert", "WinDivert14", "WinDivert15", "windivert" };
        foreach (var name in serviceNames)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(scPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("stop");
                psi.ArgumentList.Add(name);
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

        foreach (var name in serviceNames)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(scPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("delete");
                psi.ArgumentList.Add(name);
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(3000);
            }
            catch { }
        }

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

    public static List<ZapretStrategy> ParseStrategies()
    {
        var result = new List<ZapretStrategy>();
        if (!Directory.Exists(ZapretDir)) return result;

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
                System.Diagnostics.Debug.WriteLine($"[ZapretUpdater] Failed to parse {batFile}: {ex.Message}");
            }
        }

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

    private static string? ExtractWinwsArgs(string batPath, string binPath, string listsPath)
    {
        var lines = File.ReadAllLines(batPath);
        return ExtractWinwsArgsFromLines(lines, binPath, listsPath);
    }

    internal static string? ExtractWinwsArgsFromLines(string[] lines, string binPath, string listsPath)
    {

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
                    cmdBuilder.Length -= 1;
                }
                continue;
            }

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

        var exeIdx = fullCmd.IndexOf("winws.exe\"", StringComparison.OrdinalIgnoreCase);
        if (exeIdx < 0)
            exeIdx = fullCmd.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx < 0) return null;

        var afterExe = fullCmd.IndexOf('"', exeIdx);
        var argsStart = afterExe >= 0 ? afterExe + 1 : exeIdx + "winws.exe".Length;
        var args = fullCmd[argsStart..].Trim();

        args = args.Replace("%BIN%", binPath, StringComparison.OrdinalIgnoreCase);
        args = args.Replace("%LISTS%", listsPath, StringComparison.OrdinalIgnoreCase);

        args = Regex.Replace(args, @",\s*%GameFilter\w+%", "", RegexOptions.IgnoreCase);
        args = Regex.Replace(args, @"%GameFilter\w+%\s*,?", "", RegexOptions.IgnoreCase);

        var segments = Regex.Split(args, @"\s+--new\s+");
        var validSegments = new List<string>();
        foreach (var seg in segments)
        {
            var trimmed = seg.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (Regex.IsMatch(trimmed, @"--filter-(?:tcp|udp)=(?:\s|--)", RegexOptions.IgnoreCase))
                continue;
            if (Regex.IsMatch(trimmed, @"--filter-(?:tcp|udp)=$", RegexOptions.IgnoreCase))
                continue;

            validSegments.Add(trimmed);
        }

        args = string.Join(" --new ", validSegments);

        args = Regex.Replace(args, @"\s+", " ").Trim();

        args = args.Replace("\\\\", "\\");

        return args;
    }
}

public record ZapretStrategy(string Name, string Arguments, string? BatPath = null);
