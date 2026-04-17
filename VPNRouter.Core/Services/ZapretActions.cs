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
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

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
            var content = await _http.GetStringAsync(url, ct);
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
        try
        {
            Process.Start(new ProcessStartInfo("notepad", tempPath) { UseShellExecute = true });
            Process.Start(new ProcessStartInfo("explorer", $"/select,\"{hostsPath}\"") { UseShellExecute = true });
        }
        catch { }
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

    private static bool IsServiceRunning(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo("sc", $"query \"{serviceName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(2000);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool IsAnyServiceMatching(string substring)
    {
        try
        {
            var psi = new ProcessStartInfo("sc", "query state= all")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            return output.Contains(substring, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool RunNetsh(string args, out string output)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            output = p?.StandardOutput.ReadToEnd() ?? "";
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }
}
