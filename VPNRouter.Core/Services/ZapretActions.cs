using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Microsoft.Win32;

namespace VPNRouter.Core.Services;

/// <summary>
/// Flowseal zapret service actions — diagnostics, Discord cache cleanup,
/// hosts file update, service menu launcher. Pure C# reimplementations.
/// </summary>
public static class ZapretActions
{
    // 3G-2 (v3.0 refactor): replaced per-class `static readonly HttpClient`
    // with the shared IHttpClient seam. Static-class workaround: settable
    // property defaulting to PolicyHttpClient.Shared.
    /// <summary>HTTP seam — tests may override to inject <c>FakeHttpClient</c>.
    /// Defaults to <see cref="PolicyHttpClient.Shared"/>.</summary>
    public static IHttpClient Http { get; set; } = PolicyHttpClient.Shared;

    // v3.0 Phase 2D (2026-05-17): IProcessRunner seam for sc/netsh shells.
    // Default = real ProcessRunner; tests assign FakeProcessRunner to drive
    // canned exit codes / stdout / stderr without invoking real `sc`.
    // Refactored call sites (Phase 2G sub-wave 7b-2, 2026-05-18):
    // RunSc (POC), IsServiceRunning, IsAnyServiceMatching, ServiceExists,
    // RunNetsh. Open process-touching surface that remains direct-Process:
    // ClearDiscordCacheAsync (kills Discord), CheckProxyLine (registry read),
    // RunTests (powershell shell-exec), OpenServiceMenu (cmd shell-exec
    // with runas verb — UAC). Per plans/phase2-2G-untested-services-2026-05-17.md.
    private static IProcessRunner _processRunner = new ProcessRunner();

    /// <summary>Test-only seam: swap in a fake. Production paths use the
    /// default <see cref="ProcessRunner"/>; never call this outside tests.</summary>
    internal static IProcessRunner ProcessRunner
    {
        get => _processRunner;
        set => _processRunner = value ?? new ProcessRunner();
    }

    /// <summary>
    /// How long to wait for `sc stop/delete` to return before considering
    /// the call hung. Matches the legacy 2-3s WaitForExit timings.
    /// </summary>
    private static readonly TimeSpan ScCommandTimeout = TimeSpan.FromSeconds(5);

    // ── Discord cache ──

    public static async IAsyncEnumerable<string> ClearDiscordCacheAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return "=== Clear Discord cache ===";

        var running = Process.GetProcessesByName("Discord");
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

    // ── Diagnostics ──

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
            yield return Process.GetProcessesByName(proc).Length > 0
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

        yield return Process.GetProcessesByName("winws").Length > 0
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

    // ── Update hosts from Flowseal repo ──

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

    private static void OpenHostsEditHelpers(string tempPath, string hostsPath)
    {
        // v2.20.2: notepad / explorer.exe are Windows-only. On Linux / macOS
        // they don't exist and Process.Start would throw into the silent
        // catch, leaving the user with no feedback. Zapret is Windows-only
        // anyway (it ships winws.exe), so in practice this helper should
        // never be reached on other platforms — but guarding the call keeps
        // the fallback noiseless instead of pretending it worked.
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            Process.Start(new ProcessStartInfo("notepad", tempPath) { UseShellExecute = true });
            Process.Start(new ProcessStartInfo("explorer", $"/select,\"{hostsPath}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // ── Game filter (utils/game_filter.enabled) ──

    public enum GameFilterMode { Off = 0, All = 1, TcpOnly = 2, UdpOnly = 3 }

    private static string GameFilterFlagPath =>
        Path.Combine(ZapretUpdater.ZapretDir, "utils", "game_filter.enabled");

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

    // ── IPSet filter (lists/ipset-all.txt) ──

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
                // If switching from Loaded, back it up first
                if (current == IpSetMode.Loaded && File.Exists(IpSetListPath))
                {
                    if (File.Exists(IpSetBackupPath)) File.Delete(IpSetBackupPath);
                    File.Move(IpSetListPath, IpSetBackupPath);
                }
                File.WriteAllText(IpSetListPath, "");
            }
            else if (mode == IpSetMode.None)
            {
                // Back up loaded list if present
                if (current == IpSetMode.Loaded && File.Exists(IpSetListPath))
                {
                    if (File.Exists(IpSetBackupPath)) File.Delete(IpSetBackupPath);
                    File.Move(IpSetListPath, IpSetBackupPath);
                }
                File.WriteAllText(IpSetListPath, "203.0.113.113/32");
            }
            else if (mode == IpSetMode.Loaded)
            {
                // Restore from backup
                if (File.Exists(IpSetBackupPath))
                {
                    if (File.Exists(IpSetListPath)) File.Delete(IpSetListPath);
                    File.Move(IpSetBackupPath, IpSetListPath);
                }
                // else: no backup — user needs to update IPSet list
            }
        }
        catch { }
    }

    // ── IPSet list update ──

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

    // ── Auto-update check toggle ──

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

    // ── Run network tests (test zapret.ps1) ──

    public static void RunTests()
    {
        var testPath = Path.Combine(ZapretUpdater.ZapretDir, "utils", "test zapret.ps1");
        if (!File.Exists(testPath)) throw new FileNotFoundException(testPath);
        Process.Start(new ProcessStartInfo("powershell",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{testPath}\"")
        {
            UseShellExecute = true,
            WorkingDirectory = ZapretUpdater.ZapretDir
        });
    }

    // ── Remove zapret / WinDivert services ──

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

    // v3.0 Phase 2G (2026-05-18): migrated to IProcessRunner so tests can
    // inject FakeProcessRunner. `internal` (not private) so the test seam
    // in VPNRouter.Tests can call directly via InternalsVisibleTo —
    // exercising the method without simulating the entire
    // RemoveZapretServiceAsync orchestration. Behavioural surface
    // preserved: an exception path (FileNotFoundException etc.) still
    // collapses to `false` so the consuming `StopDeleteServiceLineAsync`
    // can fall through to "not installed".
    internal static bool ServiceExists(string svc)
    {
        try
        {
            var task = _processRunner.RunAsync(
                new ProcessRequest(
                    ExecutablePath: "sc",
                    Arguments: new[] { "query", svc },
                    Timeout: TimeSpan.FromSeconds(2)));
            var result = task.GetAwaiter().GetResult();
            return result.Stdout.Contains("SERVICE_NAME", StringComparison.OrdinalIgnoreCase)
                || result.Stdout.Contains("STATE", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // v3.0 Phase 2D refactor: route `sc` invocations through IProcessRunner
    // so tests can intercept the call without spawning the real binary.
    // Argument-splitting: legacy `args` came as a single shell-style string
    // like "stop zapret"; we tokenise on whitespace because none of the
    // service names we pass contain spaces (callers: StopDeleteServiceLineAsync
    // only — pure verb + svc-name). If that changes, switch callers to pass
    // IReadOnlyList<string> directly instead of relying on Split.
    //
    // v3.0 Phase 2G (2026-05-18): made `internal` so tests in VPNRouter.Tests
    // can exercise the executable+ArgumentList composition assertion.
    internal static async Task RunSc(string args)
    {
        var argList = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        await _processRunner.RunAsync(
            new ProcessRequest(
                ExecutablePath: "sc",
                Arguments: argList,
                Timeout: ScCommandTimeout));
    }

    // ── Launch Flowseal service menu ──

    public static void OpenServiceMenu()
    {
        var servicePath = Path.Combine(ZapretUpdater.ZapretDir, "service.bat");
        if (!File.Exists(servicePath))
            throw new FileNotFoundException("service.bat not found", servicePath);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{servicePath}\"\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = ZapretUpdater.ZapretDir
        });
    }

    // ── Service query helpers ──

    // v3.0 Phase 2G (2026-05-18): migrated to IProcessRunner + made `internal`
    // for tests. Note the case-sensitivity contract: `sc query NAME` emits
    // an output block containing the literal token "RUNNING" (or "STOPPED",
    // "PAUSED", etc.) when the service exists; we compare with
    // OrdinalIgnoreCase to mirror the legacy parser. A nonzero exit code
    // (service doesn't exist, 1060) yields empty Stdout → returns false.
    internal static bool IsServiceRunning(string serviceName)
    {
        try
        {
            var task = _processRunner.RunAsync(
                new ProcessRequest(
                    ExecutablePath: "sc",
                    Arguments: new[] { "query", serviceName },
                    Timeout: TimeSpan.FromSeconds(2)));
            var result = task.GetAwaiter().GetResult();
            return result.Stdout.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // v3.0 Phase 2G (2026-05-18): migrated to IProcessRunner + made `internal`.
    // `sc query state= all` enumerates *all* service states; we scan the dump
    // for a case-insensitive substring match. This is intentionally permissive
    // because the diagnostic surface ("⚠ VPN service running — may conflict")
    // shouldn't be brittle about exact wording — false positives are visible
    // to the user, false negatives silently hide conflicts.
    //
    // ArgumentList passes "state=" and "all" as separate tokens to match the
    // legacy shell-string contract (`"query state= all"` parsed with a single
    // space between `state=` and `all`).
    internal static bool IsAnyServiceMatching(string substring)
    {
        try
        {
            var task = _processRunner.RunAsync(
                new ProcessRequest(
                    ExecutablePath: "sc",
                    Arguments: new[] { "query", "state=", "all" },
                    Timeout: TimeSpan.FromSeconds(3)));
            var result = task.GetAwaiter().GetResult();
            return result.Stdout.Contains(substring, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // v3.0 Phase 2G (2026-05-18): migrated to IProcessRunner + made `internal`.
    // Splits the legacy shell-style args on whitespace for the same reason
    // RunSc does: existing callers only pass simple verb+noun strings like
    // "interface tcp show global" — no embedded spaces or quoted args. The
    // `out string output` shape is kept (vs. returning a tuple) to minimise
    // diff for the consumer (CheckTcpTimestampsLine).
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
