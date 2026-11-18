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
    private const int MaxArchiveBytes = 64 * 1024 * 1024;
    private const int MaxEntries = 1024;
    private const int MaxEntryBytes = 32 * 1024 * 1024;
    private const int MaxExpandedBytes = 128 * 1024 * 1024;
    private const string GitHubApiBase = "https://api.github.com/repos";

    private static readonly SemaphoreSlim _downloadLock = new(1, 1);

    private readonly ILogger _logger;

    private readonly IHttpClient _http;

    private static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(5);

    public static string ZapretDir => ZapretRuntimeDirectory.Package;
    public static string PackageDisplayPath => Path.Combine(ZapretRuntimeDirectory.Root, "zapret");
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

    public static bool IsInstalled()
    {
        try
        {
            if (!Directory.Exists(ZapretRuntimeDirectory.Root)) return false;
            return ZapretRuntimeDirectory.WithPackage(package => File.Exists(Path.Combine(package, "bin", "winws.exe")));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }

    public static string? GetLocalVersion()
    {
        try
        {
            return ZapretRuntimeDirectory.WithPackage(package =>
            {
                var version = Path.Combine(package, "version.txt");
                if (File.Exists(version)) return File.ReadAllText(version).Trim();

                var serviceBat = Path.Combine(package, "service.bat");
                if (File.Exists(serviceBat))
                {
                    foreach (var line in File.ReadLines(serviceBat).Take(5))
                    {
                        var match = Regex.Match(line, @"LOCAL_VERSION=(.+)""");
                        if (match.Success) return match.Groups[1].Value.Trim();
                    }
                }
                return null;
            });
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
            string? digest = null;
            long? expectedSize = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = asset.GetProperty("browser_download_url").GetString();
                    if (asset.TryGetProperty("digest", out var digestProperty) && digestProperty.ValueKind == JsonValueKind.String)
                        digest = digestProperty.GetString();
                    if (asset.TryGetProperty("size", out var sizeProp) && sizeProp.ValueKind == JsonValueKind.Number)
                        expectedSize = sizeProp.GetInt64();
                    break;
                }
            }

            if (zipUrl == null)
            {
                throw new ZapretDownloadException(
                    ZapretErrorCategory.Invalid,
                    "No ZIP asset found in the Flowseal release — upstream changed their release format.");
            }

            if (!Uri.TryCreate(zipUrl, UriKind.Absolute, out var assetUri) || assetUri.Scheme != Uri.UriSchemeHttps
                || !assetUri.IsDefaultPort || assetUri.UserInfo.Length != 0 || assetUri.Host != "github.com"
                || !assetUri.AbsolutePath.StartsWith("/" + FlowsealRepo + "/releases/download/", StringComparison.OrdinalIgnoreCase))
                throw new ZapretDownloadException(ZapretErrorCategory.Invalid, "Unexpected Zapret release asset location.");

            if (digest is null || !Regex.IsMatch(digest, @"^(?i:sha256):[a-fA-F0-9]{64}\z"))
                throw new ZapretDownloadException(ZapretErrorCategory.Invalid, "The Zapret release archive has no valid SHA-256 digest.");

            if (expectedSize is <= 0 or > MaxArchiveBytes)
                throw new ZapretDownloadException(ZapretErrorCategory.Invalid, "Unexpected Zapret archive size.");
            var runtimeRoot = ZapretRuntimeDirectory.Root;
            using var installLock = OpenInstallLock(runtimeRoot);
            StatusChanged?.Invoke($"Downloading {tagName}...");
            _logger.Information("[ZapretUpdater] Downloading: {Url} (expected {Size} bytes)",
                zipUrl, expectedSize?.ToString() ?? "unknown");

            var tempZip = Path.Combine(runtimeRoot, $"vpnr-zapret-{Guid.NewGuid():N}.zip");
            try
            {
                await RetryAsync(
                    async () =>
                    {
                        try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }

                        await using (var response = await _http.SendStreamingAsync(
                            new HttpRequest(
                                HttpMethod.Get,
                                assetUri,
                                Headers: GitHubApiHeaders,
                                Timeout: DefaultDownloadTimeout),
                            ct).ConfigureAwait(false))
                        {
                            if (!response.IsSuccess())
                                throw new HttpRequestException(
                                    $"HTTP {response.StatusCode} downloading ZIP",
                                    inner: null,
                                    statusCode: (System.Net.HttpStatusCode)response.StatusCode);

                            using var file = new FileStream(tempZip, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                            var buffer = new byte[65536];
                            int read;
                            while ((read = await response.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                            {
                                if (file.Position + read > (expectedSize ?? MaxArchiveBytes))
                                    throw new ZapretDownloadException(ZapretErrorCategory.Invalid, "The Zapret archive exceeded its expected size.");
                                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            }
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

                using (var archive = File.OpenRead(tempZip))
                {
                    var actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(archive, ct).ConfigureAwait(false));
                    if (!actual.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                        throw new ZapretDownloadException(ZapretErrorCategory.Corrupted, "The Zapret release archive failed its SHA-256 check.");
                }
                var zipSize = new FileInfo(tempZip).Length;
                _logger.Information("[ZapretUpdater] Downloaded {Size} KB", zipSize / 1024);

                StatusChanged?.Invoke("Extracting...");
                var tempDir = Path.Combine(runtimeRoot, $"vpnr-zapret-extract-{Guid.NewGuid():N}");
                try
                {
                    _logger.Information("[ZapretUpdater] Extracting to {Dir}", tempDir);
                    try
                    {
                        ExtractPackage(tempZip, tempDir, ct);
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

                    ValidatePackage(extractedRoot);

                    StatusChanged?.Invoke("Installing...");
                    var version = ParseVersionFromServiceBat(extractedRoot) ?? tagName;
                    File.WriteAllText(Path.Combine(extractedRoot, "version.txt"), version);
                    PublishVerifiedPackage(extractedRoot);
                    _logger.Information("[ZapretUpdater] Installed version {Version}", version);
                    StatusChanged?.Invoke($"Installed {version}");
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

    private static void ExtractPackage(string zip, string target, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count is 0 or > MaxEntries)
            throw new InvalidDataException("Unexpected file count in the Zapret package.");
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[65536];
        var root = Path.GetFullPath(target);
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.TrimEnd('/');
            var parts = name.Split('/');
            if (parts.Length > 16 || !seen.Add(name) || parts.Any(part =>
                    part.Length is 0 or > 128 || part.EndsWith('.') || part.EndsWith(' ') ||
                    part.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || part.Any(char.IsControl) ||
                    Regex.IsMatch(part, @"^(?i:CON|NUL|AUX|PRN|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)")) ||
                ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Unexpected path or link in the Zapret package.");
            if (entry.Length > MaxEntryBytes || entry.Length > MaxExpandedBytes - total)
                throw new InvalidDataException("Expanded Zapret package exceeds its size limit.");
            total += entry.Length;
            var dest = Path.GetFullPath(Path.Combine(root, name));
            if (!dest.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidDataException("Zapret archive entry is outside its staging directory.");
            if (entry.FullName.EndsWith('/'))
            {
                if (entry.Length != 0) throw new InvalidDataException("Invalid Zapret directory entry.");
                Directory.CreateDirectory(dest);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var source = entry.Open();
            using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (output.Position + read > entry.Length)
                    throw new InvalidDataException("Zapret file exceeds its declared size.");
                output.Write(buffer, 0, read);
            }
            if (output.Position != entry.Length) throw new InvalidDataException("Incomplete Zapret file.");
        }
    }

    private static void ValidatePackage(string root)
    {
        string[] required =
        [
            "bin/winws.exe", "bin/cygwin1.dll", "bin/WinDivert.dll", "bin/WinDivert64.sys",
            "lists/list-general.txt"
        ];
        foreach (var name in required)
        {
            var file = new FileInfo(Path.Combine(root, name));
            if (!file.Exists || file.Length == 0)
                throw new ZapretDownloadException(ZapretErrorCategory.Invalid, $"The Zapret package is missing {name}.");
            if (name.StartsWith("bin/", StringComparison.Ordinal) && !IsAmd64Image(file))
                throw new ZapretDownloadException(ZapretErrorCategory.Invalid, $"Expected a 64-bit Windows image at {name}.");
        }
        if (!Directory.EnumerateFiles(root, "general*.bat").Any(file =>
                ExtractWinwsArgs(file, "%BIN%", "%LISTS%") is { } options &&
                (options.Contains("--wf-tcp=", StringComparison.Ordinal) || options.Contains("--wf-udp=", StringComparison.Ordinal)) &&
                options.Contains("--dpi-desync=", StringComparison.Ordinal)))
            throw new ZapretDownloadException(ZapretErrorCategory.Invalid, "The Zapret package has no usable strategy.");
    }

    private static bool IsAmd64Image(FileInfo file)
    {
        using var input = file.OpenRead();
        using var reader = new BinaryReader(input);
        if (input.Length < 64 || reader.ReadUInt16() != 0x5a4d) return false;
        input.Position = 0x3c;
        var offset = reader.ReadUInt32();
        if (offset < 64 || offset > input.Length - 26) return false;
        input.Position = offset;
        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664) return false;
        input.Position = offset + 24;
        return reader.ReadUInt16() == 0x020b;
    }

    private static IDisposable OpenInstallLock(string root)
    {
        try
        {
            return ZapretRuntimeDirectory.LockInstallation(root, repair: true);
        }
        catch (IOException ex) when (ZapretRuntimeDirectory.IsLockContention(ex))
        {
            throw new ZapretDownloadException(ZapretErrorCategory.Concurrent, "Could not lock the Zapret installation for update.", ex);
        }
    }

    private static void SeedUserLists(string staged, string package)
    {
        var lists = Directory.CreateDirectory(Path.Combine(staged, "lists")).FullName;
        var oldLists = Path.Combine(package, "lists");
        foreach (var (name, fallback) in new[]
        {
            ("ipset-exclude-user.txt", "203.0.113.113/32"),
            ("list-exclude-user.txt", "domain.example.abc"),
            ("list-general-user.txt", "domain.example.abc")
        })
        {
            var source = Path.Combine(oldLists, name);
            var target = Path.Combine(lists, name);
            if (File.Exists(source)) File.Copy(source, target, overwrite: true);
            else if (!File.Exists(target)) File.WriteAllText(target, fallback + Environment.NewLine);
        }
        var previous = File.Exists(Path.Combine(package, "bin", "winws.exe"));
        foreach (var name in new[] { "game_filter.enabled", "check_updates.enabled" })
        {
            var source = Path.Combine(package, "utils", name);
            if (File.Exists(source))
            {
                var utils = Directory.CreateDirectory(Path.Combine(staged, "utils")).FullName;
                File.Copy(source, Path.Combine(utils, name), overwrite: true);
            }
            else if (previous && File.Exists(Path.Combine(staged, "utils", name)))
            {
                File.Delete(Path.Combine(staged, "utils", name));
            }
        }
        const string list = "ipset-all.txt";
        var oldList = Path.Combine(oldLists, list);
        if (File.Exists(oldList) && ZapretActions.GetIpSetMode(package) is ZapretActions.IpSetMode.Any or ZapretActions.IpSetMode.None)
            File.Copy(oldList, Path.Combine(lists, list), overwrite: true);
        if (File.Exists(oldList + ".backup"))
            File.Copy(oldList + ".backup", Path.Combine(lists, list + ".backup"), overwrite: true);
    }

    internal static void PublishVerifiedPackage(string staged, Action? afterDetach = null) =>
        ZapretRuntimeDirectory.WithPackage(destination =>
        {
            if (!Directory.Exists(staged)) throw new DirectoryNotFoundException(staged);
            SeedUserLists(staged, destination);
            ReplacePackage(staged, destination, afterDetach);
        });

    private static void ReplacePackage(string staged, string destination, Action? afterDetach)
    {
        var previous = destination + ".previous";
        if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
        if (Directory.Exists(destination)) Directory.Move(destination, previous);
        try
        {
            afterDetach?.Invoke();
            Directory.Move(staged, destination);
        }
        catch
        {
            if (Directory.Exists(previous)) Directory.Move(previous, destination);
            throw;
        }
        // A running process can still have an old component open. The next update retries cleanup.
        try { if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? ParseVersionFromServiceBat(string root)
    {
        var serviceBat = Path.Combine(root, "service.bat");
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
        try
        {
            if (!Directory.Exists(ZapretRuntimeDirectory.Root)) return [];
            return ZapretRuntimeDirectory.WithPackage(ParseStrategies);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return []; }
    }

    private static List<ZapretStrategy> ParseStrategies(string package)
    {
        var result = new List<ZapretStrategy>();
        if (!Directory.Exists(package)) return result;

        var binPath = "%BIN%";
        var listsPath = "%LISTS%";

        var game = ZapretActions.ReadGameFilter(package);
        foreach (var batFile in Directory.GetFiles(package, "general*.bat"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(batFile);
                var args = ExtractWinwsArgs(batFile, binPath, listsPath, game.Mode, game.Tcp, game.Udp);
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

    private static string? ExtractWinwsArgs(string batPath, string binPath, string listsPath, ZapretActions.GameFilterMode game = ZapretActions.GameFilterMode.Off,
        string tcpPorts = "1024-65535", string udpPorts = "1024-65535")
    {
        var lines = File.ReadAllLines(batPath);
        return ExtractWinwsArgsFromLines(lines, binPath, listsPath, game, tcpPorts, udpPorts);
    }

    internal static string? ExtractWinwsArgsFromLines(string[] lines, string binPath, string listsPath, ZapretActions.GameFilterMode game = ZapretActions.GameFilterMode.Off,
        string tcpPorts = "1024-65535", string udpPorts = "1024-65535")
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

        if (game is ZapretActions.GameFilterMode.All or ZapretActions.GameFilterMode.TcpOnly)
            args = args.Replace("%GameFilterTCP%", tcpPorts, StringComparison.OrdinalIgnoreCase);
        if (game is ZapretActions.GameFilterMode.All or ZapretActions.GameFilterMode.UdpOnly)
            args = args.Replace("%GameFilterUDP%", udpPorts, StringComparison.OrdinalIgnoreCase);
        if (game != ZapretActions.GameFilterMode.Off)
            args = args.Replace("%GameFilter%", game == ZapretActions.GameFilterMode.UdpOnly ? udpPorts : tcpPorts, StringComparison.OrdinalIgnoreCase);

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
