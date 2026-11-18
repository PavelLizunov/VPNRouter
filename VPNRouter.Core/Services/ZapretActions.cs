using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Native = VPNRouter.Core.Services.SplitTunnelDriverInterop;
using Text = VPNRouter.Core.Localization.Strings;
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

    internal static Func<string> GetSystemDirectory { get; set; } = () => Environment.SystemDirectory;
    internal static Func<string> GetHostsPreviewDirectory { get; set; } = ResolveHostsPreviewDirectory;
    internal static Action<ProcessStartInfo> ProcessLauncher { get; set; } = info =>
    {
        using var launched = Process.Start(info);
    };

    private static string LocalDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Any(c => c == '"' || char.IsControl(c)))
            throw new IOException("Could not resolve a local Windows directory for Zapret tools.");
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal))
            throw new IOException("Zapret tools require a local directory.");
        return fullPath;
    }

    private static string SystemDirectory => LocalDirectory(GetSystemDirectory());
    private static string WindowsDirectory => Path.GetDirectoryName(SystemDirectory)
        ?? throw new IOException("Could not resolve the Windows directory.");
    private static string SystemHostsPath => Path.Combine(SystemDirectory, "drivers", "etc", "hosts");

    private static Dictionary<string, string> SystemToolEnvironment() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = WindowsDirectory,
        ["WINDIR"] = WindowsDirectory,
        ["PATH"] = string.Join(';', SystemDirectory, WindowsDirectory),
    };

    private static ProcessRequest DiagnosticQuery(string image, string[] arguments, int timeoutSeconds) =>
        new(Path.Combine(SystemDirectory, image), arguments, WorkingDirectory: SystemDirectory,
            EnvironmentOverrides: SystemToolEnvironment(), Timeout: TimeSpan.FromSeconds(timeoutSeconds))
        { ClearInheritedEnvironment = true };

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

        yield return ZapretManager.IsWinwsRunning()
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
        if (!ReadTcpGlobalSettings(out var output)) return "? Couldn't read TCP timestamps";
        foreach (var line in output.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0 || !line[..colon].Contains("Timestamps", StringComparison.OrdinalIgnoreCase)) continue;
            var state = line[(colon + 1)..].Trim();
            if (state.Equals("enabled", StringComparison.OrdinalIgnoreCase)) return "✓ TCP timestamps enabled";
            if (state.Equals("disabled", StringComparison.OrdinalIgnoreCase))
            {
                var netsh = Path.Combine(SystemDirectory, "netsh.exe").Replace("'", "''");
                return $"⚠ TCP timestamps disabled (PowerShell: & '{netsh}' interface tcp set global timestamps=enabled)";
            }
        }
        return "? Couldn't read TCP timestamps";
    }

    private static async Task<string> CheckHostsLineAsync(CancellationToken ct)
    {
        var hosts = SystemHostsPath;
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
        var (ok, downloadedOrErr) = await DownloadHostsAsync(url, ct);
        if (!ok)
        {
            yield return $"✗ Failed to download: {downloadedOrErr}";
            yield break;
        }

        var hostsPath = SystemHostsPath;
        if (!File.Exists(hostsPath))
        {
            yield return "✗ System hosts file not found";
            yield break;
        }

        var (hasFirst, hasLast) = await CheckHostsMatchAsync(hostsPath, downloadedOrErr, ct);
        if (hasFirst && hasLast)
        {
            yield return "✓ Hosts file already has Flowseal entries (up to date)";
        }
        else
        {
            yield return "⚠ Hosts file missing some Flowseal entries";
            var directory = LocalDirectory(GetHostsPreviewDirectory());
            Directory.CreateDirectory(directory);
            var preview = Path.Combine(directory, $"zapret_hosts_{Guid.NewGuid():N}.txt");
            await using (var file = new FileStream(preview, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await using var writer = new StreamWriter(file);
                await writer.WriteAsync(downloadedOrErr.AsMemory(), ct);
            }
            var editor = DesktopTool(Path.Combine(SystemDirectory, "notepad.exe"), directory);
            editor.ArgumentList.Add(preview);
            yield return OpenDesktopTool(editor, preview);

            var explorer = DesktopTool(Path.Combine(WindowsDirectory, "explorer.exe"), directory);
            // Explorer's /select syntax puts the comma outside the quoted, OS-derived path.
            explorer.Arguments = $"/select,\"{hostsPath}\"";
            yield return OpenDesktopTool(explorer, hostsPath);
        }

        yield return "=== Done ===";
    }

    private static async Task<(bool ok, string content)> DownloadHostsAsync(
        string url, CancellationToken ct)
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

    private static string ResolveHostsPreviewDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                return Path.Combine(ZapretRuntimeDirectory.PrepareRoot(), "temp");
        }
        return Path.GetTempPath();
    }

    private static string OpenDesktopTool(ProcessStartInfo info, string path)
    {
        try
        {
            ProcessLauncher(info);
            return $"→ {Path.GetFileName(info.FileName)}: {path}";
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"✗ Could not open {path} with {Path.GetFileName(info.FileName)}: {ex.Message}";
        }
    }

    private static ProcessStartInfo DesktopTool(string image, string tempDirectory)
    {
        var info = new ProcessStartInfo(image) { UseShellExecute = false, WorkingDirectory = SystemDirectory };
        info.Environment.Clear();
        foreach (var (key, value) in SystemToolEnvironment()) info.Environment[key] = value;
        info.Environment["USERPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        info.Environment["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        info.Environment["LOCALAPPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        info.Environment["TEMP"] = info.Environment["TMP"] = tempDirectory;
        return info;
    }

    public enum GameFilterMode { Off = 0, All = 1, TcpOnly = 2, UdpOnly = 3 }

    private static readonly (GameFilterMode Mode, string Tcp, string Udp) DefaultGameFilter =
        (GameFilterMode.Off, "1024-65535", "1024-65535");

    private static string GameFilterFlagPath(string package) => Path.Combine(package, "utils", "game_filter.enabled");

    public static bool IsGameFilterConfigured =>
        ZapretRuntimeDirectory.WithPackage(package => File.Exists(GameFilterFlagPath(package)));

    public static GameFilterMode GetGameFilterMode() => ReadGameFilter().Mode;

    internal static (GameFilterMode Mode, string Tcp, string Udp) ReadGameFilter()
    {
        try
        {
            return ZapretRuntimeDirectory.WithPackage(package => ReadGameFilter(package));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DefaultGameFilter; }
    }

    internal static (GameFilterMode Mode, string Tcp, string Udp) ReadGameFilter(string package)
    {
        var (mode, tcp, udp) = DefaultGameFilter;
        try
        {
            var flag = new FileInfo(GameFilterFlagPath(package));
            if (!flag.Exists || flag.Length > 4096) return (mode, tcp, udp);
            foreach (var line in File.ReadLines(flag.FullName))
            {
                var entry = line.Trim().ToLowerInvariant().Split('=', 2, StringSplitOptions.TrimEntries);
                if (entry.Length == 1 || entry[0] == "mode")
                {
                    mode = entry[^1] switch
                    {
                        "all" => GameFilterMode.All, "tcp" => GameFilterMode.TcpOnly, "udp" => GameFilterMode.UdpOnly, _ => GameFilterMode.Off
                    };
                }
                else if (entry[0] is "tcp" or "udp")
                {
                    var ports = entry[1].Replace(" ", "");
                    if (!ports.Split(',').All(ValidPortRange)) continue;
                    if (entry[0] == "tcp") tcp = ports;
                    else udp = ports;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DefaultGameFilter; }
        return (mode, tcp, udp);
    }

    private static bool ValidPortRange(string value)
    {
        var parts = value.Split('-', 2);
        if (!ushort.TryParse(parts[0], System.Globalization.NumberStyles.None, null, out var first) || first == 0) return false;
        return parts.Length == 1 || ushort.TryParse(parts[1], System.Globalization.NumberStyles.None, null, out var last) && last >= first;
    }

    public static void SetGameFilterMode(GameFilterMode mode)
    {
        try
        {
            ZapretRuntimeDirectory.WithPackage(package =>
            {
                var flag = GameFilterFlagPath(package);
                Directory.CreateDirectory(Path.GetDirectoryName(flag)!);
                if (mode == GameFilterMode.Off)
                {
                    File.Delete(flag);
                    return;
                }
                var val = mode switch
                {
                    GameFilterMode.All => "all",
                    GameFilterMode.TcpOnly => "tcp",
                    GameFilterMode.UdpOnly => "udp",
                    _ => ""
                };
                File.WriteAllText(flag, val);
            });
        }
        catch { }
    }

    public enum IpSetMode { Any = 0, Loaded = 1, None = 2 }

    private static string IpSetListPath(string package) => Path.Combine(package, "lists", "ipset-all.txt");

    public static IpSetMode GetIpSetMode()
    {
        try
        {
            return ZapretRuntimeDirectory.WithPackage(package => GetIpSetMode(package));
        }
        catch { return IpSetMode.Any; }
    }

    internal static IpSetMode GetIpSetMode(string package)
    {
        var list = IpSetListPath(package);
        if (!File.Exists(list)) return IpSetMode.Any;
        var content = File.ReadAllText(list).Trim();
        if (content.Length == 0) return IpSetMode.Any;
        if (content == "203.0.113.113/32") return IpSetMode.None;
        return IpSetMode.Loaded;
    }

    public static void SetIpSetMode(IpSetMode mode)
    {
        try
        {
            ZapretRuntimeDirectory.WithPackage(package =>
            {
                var list = IpSetListPath(package);
                var backup = list + ".backup";
                Directory.CreateDirectory(Path.GetDirectoryName(list)!);
                var current = GetIpSetMode(package);
                if (current == mode) return;

                if (mode is IpSetMode.Any or IpSetMode.None)
                {
                    if (current == IpSetMode.Loaded) File.Move(list, backup, overwrite: true);
                    File.WriteAllText(list, mode == IpSetMode.Any ? "" : "203.0.113.113/32");
                }
                else if (mode == IpSetMode.Loaded && File.Exists(backup))
                {
                    File.Move(backup, list, overwrite: true);
                }
            });
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
            return ZapretRuntimeDirectory.WithPackage(package =>
            {
                var list = IpSetListPath(package);
                Directory.CreateDirectory(Path.GetDirectoryName(list)!);
                File.WriteAllText(list, content);
                return $"✓ Saved to {list}";
            });
        }
        catch (Exception ex) { return $"✗ Save failed: {ex.Message}"; }
    }

    private static string AutoUpdateFlagPath(string package) => Path.Combine(package, "utils", "check_updates.enabled");

    public static bool IsAutoUpdateCheckEnabled() =>
        ZapretRuntimeDirectory.WithPackage(package => File.Exists(AutoUpdateFlagPath(package)));

    public static void SetAutoUpdateCheck(bool enabled)
    {
        try
        {
            ZapretRuntimeDirectory.WithPackage(package =>
            {
                var flag = AutoUpdateFlagPath(package);
                Directory.CreateDirectory(Path.GetDirectoryName(flag)!);
                if (enabled) File.WriteAllText(flag, "ENABLED");
                else File.Delete(flag);
            });
        }
        catch { }
    }

    public static async IAsyncEnumerable<string> RunTestsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var outcomes = await ZapretAutoStrategy.CheckTargetsAsync(null, ct).ConfigureAwait(false);
        for (int i = 0; i < outcomes.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var host = new Uri(ZapretAutoStrategy.DefaultProbeTargets[i]).Host;
            yield return $"{host} — {Localization.Strings.ZapretHttpsResult(outcomes[i] == ZapretAutoStrategy.ProbeOutcome.Success)}";
        }
    }

    internal static async IAsyncEnumerable<string> ApplyServiceStrategyAsync(string? strategyName,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || ZapretRuntimeDirectory.TestRoot != null || !ZapretAutoStrategy.IsRunningAsAdmin())
        {
            yield return Text.ZapretServiceNeedsAdmin;
            yield break;
        }
        ct.ThrowIfCancellationRequested();
        ServiceApply outcome;
        string? image = null;
        using (ZapretRuntimeDirectory.LockInstallation(ZapretRuntimeDirectory.Root))
        {
            var strategy = ZapretUpdater.ParseStrategies().SingleOrDefault(item => item.Name == strategyName);
            if (!ZapretUpdater.IsInstalled())
                outcome = ServiceApply.MissingPackage;
            else if (strategy?.BatPath is not string path)
                outcome = ServiceApply.MissingStrategy;
            else
            {
                image = ServiceImage(ZapretUpdater.WinwsExePath, ZapretManager.ResolveInstalledStrategyArguments(path));
                outcome = ApplyServiceStrategy(ZapretServiceOwnership.Current(), image,
                    WindowsServiceRegistration.Open, CreateZapretService);
            }
        }
        if (outcome == ServiceApply.Migrate)
        {
            var migration = await ZapretLegacyServices.RetryAsync(Serilog.Log.Logger, ct, strategyName);
            if (migration.Message != null)
            {
                yield return migration.Message;
                yield break;
            }
            using var current = WindowsServiceRegistration.Open(ZapretServiceOwnership.Service, false);
            var config = current?.Config;
            outcome = config == null || config.Type != 0x10 || config.Image != image ? ServiceApply.Conflict
                : config.Start == 4 ? ServiceApply.Disabled : current!.IsActive ? ServiceApply.Running : ServiceApply.Stopped;
        }
        yield return Text.ZapretServiceOutcome(outcome);
    }

    internal enum ServiceApply { Created, Running, Stopped, Disabled, Conflict, Migrate, MissingPackage, MissingStrategy }

    internal static string ServiceImage(string exe, string[] arguments)
    {
        static string Quote(string value)
        {
            if (value.Any(c => c is '"' or '%' || char.IsControl(c)))
                throw new InvalidDataException(Text.ZapretSelectInstalled);
            int trailingSlashes = value.Length - value.TrimEnd('\\').Length;
            return "\"" + value + new string('\\', trailingSlashes) + "\"";
        }

        if (!Path.IsPathFullyQualified(exe) || arguments.Length == 0)
            throw new InvalidDataException(Text.ZapretSelectInstalled);
        string image = string.Join(" ", arguments.Prepend(exe).Select(Quote));
        if (image.Length >= 32767) throw new InvalidDataException(Text.ZapretSelectInstalled);
        return image;
    }

    internal static ServiceApply ApplyServiceStrategy(ZapretServiceOwnership paths, string image,
        Func<string, bool, IWindowsRegistration?> open, Func<string, IWindowsRegistration?> create)
    {
        const string name = ZapretServiceOwnership.Service;
        if (!ZapretServiceOwnership.Matches(name, image, paths.ProtectedBin)) return ServiceApply.Conflict;
        using var lookup = open(name, false);
        var previous = lookup?.Config;
        if (previous != null && !paths.Owns(name, previous)) return ServiceApply.Conflict;
        bool migrate = previous != null && (ZapretServiceOwnership.Matches(name, previous.Image, paths.LegacyBin) ||
            lookup!.ReadValue("VPNRouterMigrationV1") != null);
        if (HasDriverConflict(paths, open, allowLegacy: migrate)) return ServiceApply.Conflict;
        if (migrate) return ServiceApply.Migrate;

        using var service = previous == null ? create(image) : open(name, true);
        if (service == null) return ServiceApply.Conflict;
        var config = service.Config;
        if (!paths.Owns(name, config) || config.Start is not (2 or 3 or 4) ||
            (previous == null ? config.Image != image : config != previous))
            return ServiceApply.Conflict;

        // Replace the registered arguments before restarting. Never replay the old command line.
        bool wasRunning = service.IsActive;
        if (config.Image != image) service.Configure(image, config.Start);
        var expected = config with { Image = image };
        if (service.Config != expected) return ServiceApply.Conflict;
        if (wasRunning && config.Start != 4) service.Stop();
        if ((previous == null || wasRunning) && config.Start != 4)
        {
            if (service.Config != expected) return ServiceApply.Conflict;
            try { service.Start(); }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException($"{Text.ZapretStartServiceFailed} ({ex.NativeErrorCode})", ex);
            }
        }
        if (previous == null) return ServiceApply.Created;
        if (config.Start == 4) return ServiceApply.Disabled;
        return service.IsActive ? ServiceApply.Running : ServiceApply.Stopped;
    }

    internal static bool HasDriverConflict() =>
        OperatingSystem.IsWindows() && ZapretRuntimeDirectory.TestRoot == null &&
        HasDriverConflict(ZapretServiceOwnership.Current(), WindowsServiceRegistration.Open);

    internal static bool HasDriverConflict(ZapretServiceOwnership paths, Func<string, bool, IWindowsRegistration?> open,
        bool allowLegacy = false)
    {
        foreach (var driverName in ZapretServiceOwnership.DriverNames)
        {
            try
            {
                using var driver = open(driverName, false);
                var config = driver?.Config;
                if (config == null) continue;
                if (config.Type != 1) return true;
                if (ZapretServiceOwnership.Matches(driverName, config.Image, paths.ProtectedBin)) continue;
                if (!allowLegacy || !ZapretServiceOwnership.Matches(driverName, config.Image, paths.LegacyBin)) return true;
            }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or IOException)
            {
                return true;
            }
        }
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static IWindowsRegistration? CreateZapretService(string image)
    {
        if (ZapretAutoStrategy.AnyWinwsRunning()) throw new InvalidOperationException(Text.ZapretOtherHelper);
        const string name = ZapretServiceOwnership.Service;
        var scm = Native.OpenSCManager(null, null, 2); // SC_MANAGER_CREATE_SERVICE
        if (scm == IntPtr.Zero) throw new Win32Exception();
        try
        {
            const uint access = 1 | 4 | 16; // QUERY_CONFIG | QUERY_STATUS | START
            var handle = Native.CreateService(scm, name, name, access, 0x10, 2, Native.SERVICE_ERROR_NORMAL,
                image, null, IntPtr.Zero, null, null, null);
            if (handle != IntPtr.Zero) return new WindowsServiceRegistration(name, handle);
            var error = Marshal.GetLastWin32Error();
            if (error == Native.ERROR_SERVICE_EXISTS) return null;
            throw new Win32Exception(error);
        }
        finally { Native.CloseServiceHandle(scm); }
    }

    public static async IAsyncEnumerable<string> RemoveZapretServiceAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return "=== Remove zapret service ===";
        if (!OperatingSystem.IsWindows() || ZapretRuntimeDirectory.TestRoot != null) yield break;
        var paths = ZapretServiceOwnership.Current();
        foreach (var name in SystemCleanup.ZapretServices)
        {
            ct.ThrowIfCancellationRequested();
            var action = await Task.Run(() => paths.Remove(name, WindowsServiceRegistration.Open, dryRun: false), ct);
            yield return $"{name}: {action.Outcome} {action.Detail}".TrimEnd();
        }
        yield return "=== Done ===";
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

    internal static bool IsServiceRunning(string serviceName)
    {
        try
        {
            var task = _processRunner.RunAsync(
                DiagnosticQuery("sc.exe", ["query", serviceName], 2));
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
                DiagnosticQuery("sc.exe", ["query", "state=", "all"], 3));
            var result = task.GetAwaiter().GetResult();
            return result.Stdout.Contains(substring, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool ReadTcpGlobalSettings(out string output)
    {
        output = "";
        try
        {
            var task = _processRunner.RunAsync(
                DiagnosticQuery("netsh.exe", ["interface", "tcp", "show", "global"], 3));
            var result = task.GetAwaiter().GetResult();
            output = result.Stdout;
            return result.ExitCode == 0;
        }
        catch { return false; }
    }
}
