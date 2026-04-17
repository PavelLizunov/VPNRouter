using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Downloads Flowseal zapret-discord-youtube releases from GitHub
/// and parses .bat strategy files into winws.exe argument strings.
/// </summary>
public class ZapretUpdater
{
    private const string FlowsealRepo = "Flowseal/zapret-discord-youtube";
    private const string GitHubApiBase = "https://api.github.com/repos";

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
        Timeout = TimeSpan.FromMinutes(5)
    };

    public static string ZapretDir => Path.Combine(_dataDir, "zapret");
    public static string BinDir => Path.Combine(ZapretDir, "bin");
    public static string ListsDir => Path.Combine(ZapretDir, "lists");
    public static string WinwsExePath => Path.Combine(BinDir, "winws.exe");
    public static string VersionFilePath => Path.Combine(ZapretDir, "version.txt");

    public event Action<string>? StatusChanged;

    public ZapretUpdater(ILogger logger) => _logger = logger;

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

    /// <summary>Download and extract the latest Flowseal release.</summary>
    public async Task DownloadAndExtractAsync(CancellationToken ct)
    {
        StatusChanged?.Invoke("Fetching release info...");
        _logger.Information("[ZapretUpdater] Checking latest release");

        // Get latest release from GitHub API
        var url = $"{GitHubApiBase}/{FlowsealRepo}/releases/latest";
        var resp = await _http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(resp);
        var root = doc.RootElement;

        var tagName = root.GetProperty("tag_name").GetString() ?? "unknown";
        _logger.Information("[ZapretUpdater] Latest release: {Tag}", tagName);

        // Find ZIP asset
        string? zipUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                zipUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }

        if (zipUrl == null)
        {
            // Fallback: use zipball_url
            zipUrl = root.GetProperty("zipball_url").GetString();
        }

        if (zipUrl == null)
            throw new Exception("No ZIP asset found in release");

        // Download ZIP
        StatusChanged?.Invoke($"Downloading {tagName}...");
        _logger.Information("[ZapretUpdater] Downloading: {Url}", zipUrl);

        var tempZip = Path.GetTempFileName() + ".zip";
        try
        {
            using (var stream = await _http.GetStreamAsync(zipUrl, ct))
            using (var file = File.Create(tempZip))
            {
                await stream.CopyToAsync(file, ct);
            }

            var zipSize = new FileInfo(tempZip).Length;
            _logger.Information("[ZapretUpdater] Downloaded {Size} KB", zipSize / 1024);

            // Extract
            StatusChanged?.Invoke("Extracting...");
            var tempDir = Path.Combine(Path.GetTempPath(), $"zapret-extract-{Guid.NewGuid():N}");
            try
            {
                _logger.Information("[ZapretUpdater] Extracting to {Dir}", tempDir);
                ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

                // Find the root folder inside ZIP (some releases wrap, some don't)
                var extractedRoot = tempDir;
                var subdirs = Directory.GetDirectories(tempDir);
                var rootFiles = Directory.GetFiles(tempDir);
                _logger.Information("[ZapretUpdater] Extracted: {Dirs} dirs, {Files} files in root",
                    subdirs.Length, rootFiles.Length);

                if (subdirs.Length == 1 && rootFiles.Length == 0)
                    extractedRoot = subdirs[0]; // ZIP has a wrapper folder

                // Verify it has bin/winws.exe
                var testWinws = Path.Combine(extractedRoot, "bin", "winws.exe");
                if (!File.Exists(testWinws))
                {
                    var actualContents = string.Join(", ",
                        Directory.GetFileSystemEntries(extractedRoot).Select(Path.GetFileName));
                    throw new Exception($"Invalid release: bin/winws.exe not found. " +
                        $"Extracted contents: [{actualContents}]");
                }

                // Delete old zapret directory (may be locked if winws running)
                if (Directory.Exists(ZapretDir))
                {
                    _logger.Information("[ZapretUpdater] Removing old zapret dir");
                    try { Directory.Delete(ZapretDir, recursive: true); }
                    catch (Exception ex)
                    {
                        throw new Exception(
                            $"Cannot remove old zapret dir (stop zapret first?): {ex.Message}", ex);
                    }
                }

                // Copy extracted content (Move fails across different drives)
                StatusChanged?.Invoke("Installing...");
                Directory.CreateDirectory(ZapretDir);
                CopyDirectory(extractedRoot, ZapretDir);

                // Write version file
                var version = ParseVersionFromServiceBat() ?? tagName;
                File.WriteAllText(VersionFilePath, version);
                _logger.Information("[ZapretUpdater] Installed version {Version}", version);

                StatusChanged?.Invoke($"Installed {version}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[ZapretUpdater] Extract/install failed");
                StatusChanged?.Invoke($"Error: {ex.Message}");
                throw;
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
                    result.Add(new ZapretStrategy(name, args));
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

public record ZapretStrategy(string Name, string Arguments);
