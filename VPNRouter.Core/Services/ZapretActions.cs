using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Microsoft.Win32;

namespace VPNRouter.Core.Services;

public static class ZapretActions
{
    public static IHttpClient Http { get; set; } = PolicyHttpClient.Shared;

    private static IProcessRunner _processRunner = new ProcessRunner();

    internal static IProcessRunner ProcessRunner
    {
        get => _processRunner;
        set => _processRunner = value ?? new ProcessRunner();
    }

    private static readonly TimeSpan ScCommandTimeout = TimeSpan.FromSeconds(5);

    public static async IAsyncEnumerable<string> ClearDiscordCacheAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return "=== Clear Discord cache ===";

        var running = Process.GetProcessesByName("Discord");
        try
        {
            if (running.Length == 0)
            {
                yield return "Discord not running";
            }
            else
            {
                foreach (var p in running)
                {
                    yield return KillProcessLine(p);
                }
            }
        }
        finally
        {
            foreach (var p in running) { try { p.Dispose(); } catch { } }
        }

        await Task.Delay(500, ct);

        var root = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "discord");
        if (!Directory.Exists(root))
        {
            yield return "— Discord app data dir not found";
            yield break;
        }

        foreach (var subdir in new[] { "Cache", "Code Cache", "GPUCache" })
        {
            if (ct.IsCancellationRequested) yield break;
            yield return DeleteDirLine(Path.Combine(root, subdir), subdir);
        }

        yield return "=== Done ===";
    }

    private static string KillProcessLine(Process p)
    {
        try
        {
            var pid = p.Id;
            p.Kill(entireProcessTree: true);
            p.WaitForExit(3000);
            return $"✓ Killed Discord (PID {pid})";
        }
        catch (Exception ex) { return $"✗ Failed to kill Discord: {ex.Message}"; }
        finally { p.Dispose(); }
    }

    private static string DeleteDirLine(string path, string label)
    {
        if (!Directory.Exists(path)) return $"— {label}: not present";
        try
        {
            Directory.Delete(path, recursive: true);
            return $"✓ Deleted {label}";
        }
        catch (Exception ex) { return $"✗ Failed {label}: {ex.Message}"; }
    }

    public static async IAsyncEnumerable<string> RunDiagnosticsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return "=== Zapret diagnostics ===";

        yield return IsServiceRunning("BFE")
            ? "✓ Base Filtering Engine running"
            : "✗ [X] Base Filtering Engine NOT running — required";

        yield return CheckProxyLine();
        yield return CheckTcpTimestampsLine();

        foreach (var proc in new[] { "AdguardSvc", "SmartByte" })
        {
            yield return ProcessQuery.AnyAlive(proc)
                ? $"✗ [X] {proc} running — conflicts with zapret"
                : $"✓ {proc} not running";
        }

        foreach (var svc in new[] { "Killer", "GoodbyeDPI", "TracSrvWrapper", "EPWD" })
        {
            yield return IsServiceRunning(svc)
                ? $"✗ [X] {svc} service running — conflicts"
                : $"✓ {svc} not active";
        }

        yield return IsAnyServiceMatching("vpn")
            ? "⚠ VPN service running — may conflict (disable if issues)"
            : "✓ No third-party VPN service running";

        yield return await CheckHostsLineAsync(ct);

        yield return ProcessQuery.AnyAlive("winws")
            ? "✓ winws.exe is running"
            : "— winws.exe not running";

        yield return IsServiceRunning("WinDivert")
            ? "⚠ WinDivert service active"
            : "— WinDivert not running";

        yield return "=== Done ===";
    }

    private static string CheckProxyLine()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            var enabled = (int)(k?.GetValue("ProxyEnable") ?? 0);
            if (enabled == 1)
            {
                var server = k?.GetValue("ProxyServer")?.ToString() ?? "";
                return $"⚠ System proxy enabled: {server} — may conflict";
            }
            return "✓ No system proxy";
        }
        catch { return "? Couldn't read proxy settings"; }
    }

    private static string CheckTcpTimestampsLine()
    {
        var ok = RunNetsh("interface tcp show global", out var netshOut);
        return ok && netshOut.Contains("Timestamps: enabled", StringComparison.OrdinalIgnoreCase)
            ? "✓ TCP timestamps enabled"
            : "⚠ TCP timestamps disabled (run: netsh int tcp set global timestamps=enabled)";
    }

    private static async Task<string> CheckHostsLineAsync(CancellationToken ct)
    {
        var hosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers", "etc", "hosts");
        if (!File.Exists(hosts)) return "— Hosts file not found";
        try
        {
            var content = await File.ReadAllTextAsync(hosts, ct);
            return content.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
                ? "⚠ Hosts file contains youtube.com — may block YouTube"
                : "✓ Hosts file clean";
        }
        catch (Exception ex) { return $"? Couldn't read hosts: {ex.Message}"; }
    }

    public static async IAsyncEnumerable<string> UpdateHostsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return "=== Update hosts file (Flowseal) ===";

        const string url = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/hosts";
        var tempPath = Path.Combine(Path.GetTempPath(), $"zapret_hosts_{Guid.NewGuid():N}.txt");

        var (ok, downloadedOrErr) = await DownloadHostsAsync(url, tempPath, ct);
        if (!ok)
        {
            yield return $"✗ Failed to download: {downloadedOrErr}";
            yield break;
        }
        yield return $"✓ Downloaded to {tempPath}";

        var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers", "etc", "hosts");
        if (!File.Exists(hostsPath))
        {
            yield return "✗ System hosts file not found";
            yield break;
        }

        var (hasFirst, hasLast) = await CheckHostsMatchAsync(hostsPath, downloadedOrErr, ct);
        if (hasFirst && hasLast)
        {
            yield return "✓ Hosts file already has Flowseal entries (up to date)";
            try { File.Delete(tempPath); } catch { }
        }
        else
        {
            yield return "⚠ Hosts file missing some Flowseal entries";
            yield return $"→ Opening {tempPath} and Explorer at {hostsPath}";
            OpenHostsEditHelpers(tempPath, hostsPath);
        }

        yield return "=== Done ===";
    }

    private static async Task<(bool ok, string content)> DownloadHostsAsync(
        string url, string tempPath, CancellationToken ct)
    {
        try
        {
            var resp = await Http.SendAsync(
                new HttpRequest(HttpMethod.Get, new Uri(url),
                    Timeout: TimeSpan.FromSeconds(15)),
                ct);
            if (!resp.IsSuccess())
                return (false, $"HTTP {resp.StatusCode}");
            var content = resp.AsString();
            await File.WriteAllTextAsync(tempPath, content, ct);
            return (true, content);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static async Task<(bool hasFirst, bool hasLast)> CheckHostsMatchAsync(
        string hostsPath, string downloadedContent, CancellationToken ct)
    {
        try
        {
            var currentHosts = await File.ReadAllTextAsync(hostsPath, ct);
            var lines = downloadedContent.Split('\n');
            var first = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"))?.Trim() ?? "";
            var last = lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"))?.Trim() ?? "";
            return (first.Length > 0 && currentHosts.Contains(first),
                    last.Length > 0 && currentHosts.Contains(last));
        }
        catch { return (false, false); }
    }

    internal static void OpenHostsEditHelpers(string tempPath, string hostsPath)
    {
        ArgumentNullException.ThrowIfNull(tempPath);
        ArgumentNullException.ThrowIfNull(hostsPath);

        if (tempPath.Any(c => c is '\r' or '\n' or '"'))
            throw new ArgumentException("Temp path contains disallowed characters", nameof(tempPath));

        if (hostsPath.Any(c => c is '\r' or '\n' or '"'))
            throw new ArgumentException("Hosts path contains disallowed characters", nameof(hostsPath));

        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            Process.Start(new ProcessStartInfo("notepad", tempPath) { UseShellExecute = true });
            Process.Start(new ProcessStartInfo("explorer", $"/select,\"{hostsPath}\"") { UseShellExecute = true });
        }
        catch { }
    }

    public enum GameFilterMode { Off = 0, All = 1, TcpOnly = 2, UdpOnly = 3 }

    private static string GameFilterFlagPath =>
        Path.Combine(ZapretUpdater.ZapretDir, "utils", "game_filter.enabled");

    public static bool IsGameFilterConfigured => File.Exists(GameFilterFlagPath);

    public static GameFilterMode GetGameFilterMode()
    {
        try
        {
            if (!File.Exists(GameFilterFlagPath)) return GameFilterMode.Off;
            var content = File.ReadAllText(GameFilterFlagPath).Trim().ToLowerInvariant();
            return content switch
            {
                "all" => GameFilterMode.All,
                "tcp" => GameFilterMode.TcpOnly,
                "udp" => GameFilterMode.UdpOnly,
                _ => GameFilterMode.Off
            };
        }
        catch { return GameFilterMode.Off; }
    }

    public static void SetGameFilterMode(GameFilterMode mode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GameFilterFlagPath)!);
            if (mode == GameFilterMode.Off)
            {
                if (File.Exists(GameFilterFlagPath)) File.Delete(GameFilterFlagPath);
                return;
            }
            var val = mode switch
            {
                GameFilterMode.All => "all",
                GameFilterMode.TcpOnly => "tcp",
                GameFilterMode.UdpOnly => "udp",
                _ => ""
            };
            File.WriteAllText(GameFilterFlagPath, val);
        }
        catch { }
    }

    public enum IpSetMode { Any = 0, Loaded = 1, None = 2 }

    private static string IpSetListPath =>
        Path.Combine(ZapretUpdater.ZapretDir, "lists", "ipset-all.txt");

    private static string IpSetBackupPath =>
        Path.Combine(ZapretUpdater.ZapretDir, "lists", "ipset-all.txt.backup");

    public static IpSetMode GetIpSetMode()
    {
        try
        {
            if (!File.Exists(IpSetListPath)) return IpSetMode.Any;
            var content = File.ReadAllText(IpSetListPath).Trim();
            if (content.Length == 0) return IpSetMode.Any;
            if (content == "203.0.113.113/32") return IpSetMode.None;
            return IpSetMode.Loaded;
        }
        catch { return IpSetMode.Any; }
    }

    public static void SetIpSetMode(IpSetMode mode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IpSetListPath)!);
            var current = GetIpSetMode();
            if (current == mode) return;

            if (mode == IpSetMode.Any)
            {
                if (current == IpSetMode.Loaded && File.Exists(IpSetListPath))
                {
                    if (File.Exists(IpSetBackupPath)) File.Delete(IpSetBackupPath);
                    File.Move(IpSetListPath, IpSetBackupPath);
                }
                File.WriteAllText(IpSetListPath, "");
            }
            else if (mode == IpSetMode.None)
            {
                if (current == IpSetMode.Loaded && File.Exists(IpSetListPath))
                {
                    if (File.Exists(IpSetBackupPath)) File.Delete(IpSetBackupPath);
                    File.Move(IpSetListPath, IpSetBackupPath);
                }
                File.WriteAllText(IpSetListPath, "203.0.113.113/32");
            }
            else if (mode == IpSetMode.Loaded)
            {
                if (File.Exists(IpSetBackupPath))
                {
                    if (File.Exists(IpSetListPath)) File.Delete(IpSetListPath);
                    File.Move(IpSetBackupPath, IpSetListPath);
                }
            }
        }
        catch { }
    }

    public static async IAsyncEnumerable<string> UpdateIpSetListAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        const string url = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/ipset-service.txt";
        yield return "=== Update IPSet list ===";
        var (ok, content) = await DownloadIpSetAsync(url, ct);
        if (!ok)
        {
            yield return $"✗ Failed: {content}";
            yield break;
        }
        yield return $"✓ Downloaded {content.Length} bytes";
        var lines = content.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l));
        yield return $"✓ {lines} entries";
        yield return SaveIpSetLine(content);
        yield return "=== Done ===";
    }

    private static async Task<(bool ok, string content)> DownloadIpSetAsync(string url, CancellationToken ct)
    {
        try
        {
            var resp = await Http.SendAsync(
                new HttpRequest(HttpMethod.Get, new Uri(url),
                    Timeout: TimeSpan.FromSeconds(15)),
                ct);
            if (!resp.IsSuccess())
                return (false, $"HTTP {resp.StatusCode}");
            return (true, resp.AsString());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static string SaveIpSetLine(string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IpSetListPath)!);
            File.WriteAllText(IpSetListPath, content);
            return $"✓ Saved to {IpSetListPath}";
        }
        catch (Exception ex) { return $"✗ Save failed: {ex.Message}"; }
    }

    private static string AutoUpdateFlagPath =>
        Path.Combine(ZapretUpdater.ZapretDir, "utils", "check_updates.enabled");

    public static bool IsAutoUpdateCheckEnabled() => File.Exists(AutoUpdateFlagPath);

    public static void SetAutoUpdateCheck(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AutoUpdateFlagPath)!);
            if (enabled) File.WriteAllText(AutoUpdateFlagPath, "ENABLED");
            else if (File.Exists(AutoUpdateFlagPath)) File.Delete(AutoUpdateFlagPath);
        }
        catch { }
    }

    public static void RunTests()
    {
        var testPath = Path.Combine(ZapretUpdater.ZapretDir, "utils", "test zapret.ps1");
        if (!File.Exists(testPath)) throw new FileNotFoundException(testPath);

        if (testPath.Any(c => c is '\r' or '\n' or '&' or '|' or '^' or '<' or '>' or '%' or '"'))
            throw new ArgumentException("Test path contains disallowed shell metacharacters", nameof(testPath));

        var psi = new ProcessStartInfo("powershell")
        {
            UseShellExecute = false,
            WorkingDirectory = ZapretUpdater.ZapretDir
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(testPath);

        Process.Start(psi);
    }

    public static async IAsyncEnumerable<string> RemoveZapretServiceAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return "=== Remove zapret service ===";
        foreach (var svc in new[] { "zapret", "WinDivert", "WinDivert14" })
        {
            yield return await StopDeleteServiceLineAsync(svc);
        }
        yield return "=== Done ===";
    }

    private static async Task<string> StopDeleteServiceLineAsync(string svc)
    {
        try
        {
            if (!IsServiceRunning(svc) && !ServiceExists(svc))
                return $"— {svc}: not installed";
            await RunSc($"stop {svc}");
            await RunSc($"delete {svc}");
            return $"✓ {svc}: removed";
        }
        catch (Exception ex) { return $"✗ {svc}: {ex.Message}"; }
    }

    internal static string ScExecutablePath { get; set; } =
        OperatingSystem.IsWindows() ? WindowsServiceCommand.GetSystemScPath() : "sc";

    internal static bool ServiceExists(string svc)
    {
        try
        {
            var task = _processRunner.RunAsync(
                new ProcessRequest(
                    ExecutablePath: ScExecutablePath,
                    Arguments: new[] { "query", svc },
                    Timeout: TimeSpan.FromSeconds(2)));
            var result = task.GetAwaiter().GetResult();
            return result.Stdout.Contains("SERVICE_NAME", StringComparison.OrdinalIgnoreCase)
                || result.Stdout.Contains("STATE", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static async Task RunSc(string args)
    {
        var argList = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        await _processRunner.RunAsync(
            new ProcessRequest(
                ExecutablePath: ScExecutablePath,
                Arguments: argList,
                Timeout: ScCommandTimeout));
    }

    public static void OpenServiceMenu(string? customServicePath = null)
    {
        var targetPath = customServicePath ?? Path.Combine(ZapretUpdater.ZapretDir, "service.bat");
        var fullServicePath = Path.GetFullPath(targetPath);
        var baseDir = Path.GetFullPath(ZapretUpdater.ZapretDir);

        if (!fullServicePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Service path must point to a .bat file", nameof(customServicePath));

        if (!fullServicePath.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullServicePath, baseDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Service path must be located within the Zapret directory", nameof(customServicePath));
        }

        if (!File.Exists(fullServicePath))
            throw new FileNotFoundException("service.bat not found", fullServicePath);

        if (fullServicePath.Any(c => c is '\r' or '\n' or '&' or '|' or '^' or '<' or '>' or '%' or '"'))
            throw new ArgumentException("Service path contains disallowed shell metacharacters", nameof(customServicePath));

        Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{fullServicePath}\"\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(fullServicePath) ?? ZapretUpdater.ZapretDir
        });
    }

    internal static bool IsServiceRunning(string serviceName)
    {
        try
        {
            var task = _processRunner.RunAsync(
                new ProcessRequest(
                    ExecutablePath: ScExecutablePath,
                    Arguments: new[] { "query", serviceName },
                    Timeout: TimeSpan.FromSeconds(2)));
            var result = task.GetAwaiter().GetResult();
            return result.Stdout.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static bool IsAnyServiceMatching(string substring)
    {
        try
        {
            var task = _processRunner.RunAsync(
                new ProcessRequest(
                    ExecutablePath: ScExecutablePath,
                    Arguments: new[] { "query", "state=", "all" },
                    Timeout: TimeSpan.FromSeconds(3)));
            var result = task.GetAwaiter().GetResult();
            return result.Stdout.Contains(substring, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static bool RunNetsh(string args, out string output)
    {
        output = "";
        try
        {
            var argList = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var task = _processRunner.RunAsync(
                new ProcessRequest(
                    ExecutablePath: "netsh",
                    Arguments: argList,
                    Timeout: TimeSpan.FromSeconds(3)));
            var result = task.GetAwaiter().GetResult();
            output = result.Stdout;
            return result.ExitCode == 0;
        }
        catch { return false; }
    }
}
