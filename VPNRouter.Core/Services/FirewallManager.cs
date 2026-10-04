using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Serilog;
using VPNRouter.Core.Interfaces;

namespace VPNRouter.Core.Services;

public class FirewallManager : IFirewallManager
{
    private const string RulePrefix = "VPNRouter_Block_";

    private const int NetshTimeoutMs = 5000;

    // DNS lockdown blocks outbound DNS ports on every adapter except loopback; sing-box resolves over DoH inside the tunnel, so it is unaffected.
    internal const string DnsLockdownAllowRule = "0_VPNRouter-DnsLockdown-LoopbackAllow";
    internal const string DnsLockdownTunAllowRule = "0_VPNRouter-DnsLockdown-TunAllow";
    internal const string DnsLockdownUdp53Rule = "VPNRouter-DnsLockdown-UDP53";
    internal const string DnsLockdownTcp53Rule = "VPNRouter-DnsLockdown-TCP53";
    internal const string DnsLockdownTcp853Rule = "VPNRouter-DnsLockdown-TCP853";

    internal const string DnsLockdownUdp53Ipv6Rule = "VPNRouter-DnsLockdown-UDP53-v6";
    internal const string DnsLockdownTcp53Ipv6Rule = "VPNRouter-DnsLockdown-TCP53-v6";
    internal const string DnsLockdownTcp853Ipv6Rule = "VPNRouter-DnsLockdown-TCP853-v6";

    private const string Ipv6PublicDnsScope = "2000::/3";

    private static readonly string[] AllPrefixes =
    {
        RulePrefix,
        "VPNRouter-DnsLockdown-",
        "0_VPNRouter-DnsLockdown-",
    };

    internal static IReadOnlyList<string> ManagedRulePrefixes => AllPrefixes;

    private const int PathResolveParallelism = 8;

    private readonly ILogger _logger;
    private readonly IProcessRunner _runner;
    private IFirewallRuleStore? _store;
    private readonly bool _resolveInProcess;
    private readonly List<string> _managedRules = new();
    private List<string> _requestedNames = new();
    private bool _disposed;

    private static readonly Encoding ConsoleEncoding = ResolveConsoleEncoding();

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static Encoding ResolveConsoleEncoding()
    {
        if (!OperatingSystem.IsWindows()) return Encoding.UTF8;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private static readonly IProcessRunner DefaultRunner = new ProcessRunner();

    internal static IProcessRunner Runner { get; set; } = DefaultRunner;

    // The COM rule store is used only with the real process runner; tests that inject a runner keep exercising the netsh path.
    public FirewallManager(ILogger? logger = null, IProcessRunner? runner = null)
    {
        _logger = logger ?? Log.Logger;
        _runner = runner ?? Runner;
        if (runner is null && ReferenceEquals(Runner, DefaultRunner) && OperatingSystem.IsWindows())
        {
            _store = ComFirewallRuleStore.TryCreate(_logger);
            _resolveInProcess = true;
        }
    }

    internal FirewallManager(ILogger? logger, IProcessRunner? runner, IFirewallRuleStore? store)
    {
        _logger = logger ?? Log.Logger;
        _runner = runner ?? Runner;
        _store = store;
    }

    public static void TryCleanupOrphanedRulesSafe(ILogger? logger = null)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                VPNRouter.Core.Platform.macOS.MacFirewallManager.TryCleanupOrphanedRulesSafe(logger ?? Log.Logger);
                return;
            }
            if (OperatingSystem.IsLinux())
            {
                VPNRouter.Core.Platform.Linux.LinuxFirewallManager.TryCleanupOrphanedRulesSafe(logger ?? Log.Logger);
                return;
            }
            if (!OperatingSystem.IsWindows()) return;
            using var fw = new FirewallManager(logger ?? Log.Logger);
            fw.CleanupOrphanedRules();
        }
        catch
        {
        }
    }

    public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true)
    {
        _ = isFullTunnel;
        CleanupOrphanedRules();
        _managedRules.Clear();

        var exact = processNames
            .Where(n => !n.Contains('*') && !n.Contains('?'))
            .ToList();
        _requestedNames = exact;

        // Resolving a name that is not running costs a where.exe process; do them side by side.
        var resolved = exact
            .AsParallel().AsOrdered().WithDegreeOfParallelism(PathResolveParallelism)
            .Select(n => (Name: n, Path: ResolveProcessPath(n)))
            .ToList();

        var notRunning = 0;
        foreach (var (name, exePath) in resolved)
        {
            var ruleName = RulePrefix + name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);

            if (exePath == null)
            {
                notRunning++;
                _logger.Debug("[Firewall] Skipping rule for {Process} — exe path not found (process not running?)", name);
                continue;
            }

            if (CreateBlockRule(ruleName, exePath, enabled: false))
            {
                _managedRules.Add(ruleName);
            }
            else
            {
                _logger.Warning("[Firewall] Failed to create rule for {Process} — netsh error", name);
            }
        }

        _logger.Information(
            "[Firewall] Created {Count} block rules (disabled — will enable on VPN crash); {Skipped} listed apps have no exe on this machine",
            _managedRules.Count, notRunning);
    }

    public void EnableBlockRules()
    {
        var unresolved = 0;
        foreach (var name in _requestedNames)
        {
            var ruleName = RulePrefix + name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
            if (_managedRules.Contains(ruleName)) continue;
            var exePath = ResolveProcessPath(name);
            if (exePath == null)
            {
                unresolved++;
                _logger.Debug("[Firewall] kill-switch: still cannot resolve {Process} — cannot block its direct egress", name);
                continue;
            }
            if (CreateBlockRule(ruleName, exePath, enabled: true))
            {
                _managedRules.Add(ruleName);
                _logger.Information("[Firewall] kill-switch: late-created + enabled block rule for {Process} (was unresolved at connect time)", name);
            }
        }

        if (unresolved > 0)
            _logger.Information("[Firewall] kill-switch: {Count} listed apps have no exe on this machine - nothing to block for them", unresolved);

        var ok = SetManagedRulesEnabled(true);
        if (ok == _managedRules.Count)
            _logger.Information("[Firewall] ENABLED {Count} block rules (VPN down — leak protection active)", ok);
        else
            _logger.Warning("[Firewall] ENABLED {Ok}/{Total} block rules (VPN down — {Missing} missing in firewall)",
                ok, _managedRules.Count, _managedRules.Count - ok);
    }

    public void DisableBlockRules()
    {
        var ok = SetManagedRulesEnabled(false);
        if (ok == _managedRules.Count)
            _logger.Information("[Firewall] Disabled {Count} block rules (VPN up — TUN handles routing)", ok);
        else
            _logger.Warning("[Firewall] Disabled {Ok}/{Total} block rules (VPN up — {Missing} missing in firewall)",
                ok, _managedRules.Count, _managedRules.Count - ok);
    }

    private int SetManagedRulesEnabled(bool enabled)
    {
        if (_store is not null)
        {
            try
            {
                return _store.SetEnabled(_managedRules, enabled);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[Firewall] COM enable/disable failed - falling back to netsh");
                _store = null;
            }
        }

        var ok = 0;
        foreach (var rule in _managedRules)
        {
            if (RunNetsh($"advfirewall firewall set rule name=\"{rule}\" new enable={(enabled ? "yes" : "no")}"))
                ok++;
        }
        return ok;
    }

    private void RemoveRules(IReadOnlyCollection<string> rules)
    {
        if (rules.Count == 0) return;

        if (_store is not null)
        {
            try
            {
                _store.Remove(rules);
                return;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[Firewall] COM delete failed - falling back to netsh");
                _store = null;
            }
        }

        foreach (var rule in rules)
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{rule}\"");
            _logger.Debug("[Firewall] Deleted rule: {Rule}", rule);
        }
    }

    public void DeleteAllRules()
    {
        RemoveRules(_managedRules.ToList());
        _managedRules.Clear();
        _logger.Information("[Firewall] All VPNRouter firewall rules deleted");
    }

    private bool CreateBlockRule(string ruleName, string programPath, bool enabled)
    {
        if (_store is not null)
        {
            try
            {
                if (_store.AddOutboundBlockRule(ruleName, programPath, enabled, "VPNRouter block_on_vpn_fail"))
                {
                    _logger.Debug("[Firewall] Created rule '{Rule}' for {Program} (enabled: {Enabled})",
                        ruleName, programPath, enabled);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[Firewall] COM add failed for '{Rule}' - falling back to netsh", ruleName);
                _store = null;
            }
        }

        var enabledStr = enabled ? "yes" : "no";

        var success = RunNetsh($"advfirewall firewall add rule " +
                 $"name=\"{ruleName}\" " +
                 $"dir=out " +
                 $"action=block " +
                 $"program=\"{programPath}\" " +
                 $"enable={enabledStr} " +
                 $"profile=any " +
                 $"description=\"VPNRouter block_on_vpn_fail\"");

        if (success)
        {
            _logger.Debug("[Firewall] Created rule '{Rule}' for {Program} (enabled: {Enabled})",
                ruleName, programPath, enabled);
        }

        return success;
    }

    private string? ResolveProcessPath(string processName)
    {
        if (OperatingSystem.IsWindows())
        {
            var running = ProcessImagePath.ResolveRunningPath(processName);
            if (!string.IsNullOrEmpty(running))
                return running;
        }

        if (OperatingSystem.IsWindows())
        {
            var onPath = _resolveInProcess
                ? ProcessImagePath.ResolveNameOnPath(processName)
                : ProcessImagePath.ResolveNameToPath(processName, _runner);
            if (!string.IsNullOrEmpty(onPath))
                return onPath;
        }

        _logger.Debug("[Firewall] Could not resolve path for {Process}", processName);
        return null;
    }

    public void CleanupOrphanedRules()
    {
        var orphaned = FindRulesByPrefixes(AllPrefixes);

        if (orphaned.Count == 0)
        {
            _logger.Debug("[Firewall] No orphaned rules found");
            return;
        }

        RemoveRules(orphaned);

        _logger.Information("[Firewall] Cleaned up {Count} orphaned rules", orphaned.Count);
    }

    internal bool DeleteRuleByName(string ruleName) =>
        RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");

    internal List<string> FindRulesByPrefixes(IEnumerable<string> prefixes)
    {
        var prefixList = (prefixes ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        var result = new List<string>();
        if (prefixList.Count == 0) return result;

        if (_store is not null)
        {
            try
            {
                return _store.FindByPrefixes(prefixList);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[Firewall] COM enumeration failed - falling back to netsh");
                _store = null;
            }
        }

        try
        {
            var psiResult = _runner.RunAsync(new ProcessRequest(
                ExecutablePath: "netsh.exe",
                Arguments: new[] { "advfirewall", "firewall", "show", "rule", "name=all", "dir=out" },
                Timeout: TimeSpan.FromMilliseconds(10_000))).GetAwaiter().GetResult();

            if (psiResult.TimedOut) return result;
            var output = psiResult.Stdout;

            var inNewBlock = true;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    inNewBlock = true;
                    continue;
                }
                if (!inNewBlock) continue;
                inNewBlock = false;

                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx < 0) continue;

                var value = trimmed[(colonIdx + 1)..].Trim();
                foreach (var prefix in prefixList)
                {
                    if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(value);
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Firewall] Failed to enumerate firewall rules");
        }

        return result;
    }

    private bool RunNetsh(string arguments)
    {
        try
        {
            var argv = SplitShellArgs(arguments);
            var result = _runner.RunAsync(new ProcessRequest(
                ExecutablePath: "netsh.exe",
                Arguments: argv,
                Timeout: TimeSpan.FromMilliseconds(NetshTimeoutMs))).GetAwaiter().GetResult();

            if (result.TimedOut)
            {
                _logger.Warning("[Firewall] netsh timed out for: {Args}", arguments);
                return false;
            }

            if (result.ExitCode != 0)
            {
                _logger.Warning("[Firewall] netsh returned {Code} for: {Args} | stdout: {Out} | stderr: {Err}",
                    result.ExitCode, arguments, result.Stdout.Trim(), result.Stderr.Trim());
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Firewall] netsh failed: {Args}", arguments);
            return false;
        }
    }

    internal static string[] SplitShellArgs(string arguments)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (int i = 0; i < arguments.Length; i++)
        {
            var c = arguments[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DeleteAllRules();
    }

    internal static readonly string[] DnsLockdownRuleNames =
    {
        DnsLockdownAllowRule, DnsLockdownTunAllowRule, DnsLockdownTunAllowRule + "-TCP",
        DnsLockdownUdp53Rule, DnsLockdownTcp53Rule, DnsLockdownTcp853Rule,
        DnsLockdownUdp53Ipv6Rule, DnsLockdownTcp53Ipv6Rule, DnsLockdownTcp853Ipv6Rule,
    };

    // The rules of the DNS leak lockdown as data, so the COM path and the netsh fallback add exactly the same thing.
    internal static IReadOnlyList<FirewallRuleSpec> BuildDnsLockdownSpecs(string blockExclusionRange) => new[]
    {
        new FirewallRuleSpec(DnsLockdownAllowRule, Allow: true, "UDP", "53", "127.0.0.1",
            "VPNRouter Wave 39: allow loopback DNS for local proxies"),
        new FirewallRuleSpec(DnsLockdownUdp53Rule, Allow: false, "UDP", "53", blockExclusionRange,
            "VPNRouter Wave 39 BR-9: block UDP/53 to prevent DNS leak (TUN range excluded)"),
        new FirewallRuleSpec(DnsLockdownTcp53Rule, Allow: false, "TCP", "53", blockExclusionRange,
            "VPNRouter Wave 39 BR-9: block TCP/53 to prevent DNS leak (TUN range excluded)"),
        new FirewallRuleSpec(DnsLockdownTcp853Rule, Allow: false, "TCP", "853", blockExclusionRange,
            "VPNRouter Wave 39 BR-9: block TCP/853 to prevent DNS leak (TUN range excluded)"),
        new FirewallRuleSpec(DnsLockdownUdp53Ipv6Rule, Allow: false, "UDP", "53", Ipv6PublicDnsScope,
            "VPNRouter r10 #6: block UDP/53 over public IPv6 (2000::/3) to prevent DNS leak"),
        new FirewallRuleSpec(DnsLockdownTcp53Ipv6Rule, Allow: false, "TCP", "53", Ipv6PublicDnsScope,
            "VPNRouter r10 #6: block TCP/53 over public IPv6 (2000::/3) to prevent DNS leak"),
        new FirewallRuleSpec(DnsLockdownTcp853Ipv6Rule, Allow: false, "TCP", "853", Ipv6PublicDnsScope,
            "VPNRouter r10 #6: block TCP/853 over public IPv6 (2000::/3) to prevent DNS leak"),
    };

    internal static string NetshAddArguments(FirewallRuleSpec spec) =>
        $"advfirewall firewall add rule name=\"{spec.Name}\" dir=out action={(spec.Allow ? "allow" : "block")} " +
        $"protocol={spec.Protocol} remoteport={spec.RemotePorts} remoteip={spec.RemoteAddresses} enable=yes profile=any " +
        $"description=\"{spec.Description}\"";

    // Nine netsh processes took about a second each way; the in-process firewall API does it at once. Used only with the real process runner
    // (tests that inject a runner keep the netsh path and its order).
    private static IFirewallRuleStore? TryStaticStore(ILogger log) =>
        OperatingSystem.IsWindows() && ReferenceEquals(Runner, DefaultRunner) ? ComFirewallRuleStore.TryCreate(log) : null;

    [SupportedOSPlatform("windows")]
    public static async Task EnableDnsLockdownAsync(
        ILogger? logger = null,
        string? tunCidr = null,
        CancellationToken ct = default)
    {
        var log = logger ?? Log.Logger;
        if (!OperatingSystem.IsWindows())
        {
            log.Debug("[FirewallManager] DNS lockdown skipped — non-Windows platform");
            return;
        }

        var tunAllowIp = NormalizeTunAllowIp(tunCidr) ?? "172.19.0.0/30";

        var blockExclusionRange = ComputeBlockExclusionRange(tunAllowIp) ?? "0.0.0.0-172.18.255.255,172.19.0.4-255.255.255.255";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await Task.Run(() =>
            {
                var specs = BuildDnsLockdownSpecs(blockExclusionRange);
                var store = TryStaticStore(log);
                if (store is not null)
                {
                    try
                    {
                        foreach (var spec in specs) store.AddRule(spec);
                        return;
                    }
                    catch (Exception ex)
                    {
                        log.Warning(ex, "[FirewallManager] COM DNS lockdown failed - using netsh");
                        try { store.Remove(specs.Select(sp => sp.Name).ToList()); } catch { }
                    }
                }

                foreach (var spec in specs) RunNetshStatic(log, NetshAddArguments(spec));
            }, timeoutCts.Token).ConfigureAwait(false);

            log.Information(
                "[FirewallManager] DNS leak lockdown enabled — UDP/53, TCP/53, " +
                "TCP/853 blocked on non-loopback IPv4 interfaces (TUN block-exclusion={Tun}, " +
                "BR-9) + public-IPv6 ({Ipv6Scope}) DNS blocked (r10 #6)", tunAllowIp, Ipv6PublicDnsScope);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            log.Warning("[FirewallManager] DNS leak lockdown setup timed out after 5s — partial rule set may be active");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[FirewallManager] DNS leak lockdown setup failed (non-fatal — VPN start continues)");
        }
    }

    [SupportedOSPlatform("windows")]
    public static async Task DisableDnsLockdownAsync(ILogger? logger = null, CancellationToken ct = default)
    {
        var log = logger ?? Log.Logger;
        if (!OperatingSystem.IsWindows())
        {
            log.Debug("[FirewallManager] DNS lockdown disable skipped — non-Windows platform");
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await Task.Run(() =>
            {
                var store = TryStaticStore(log);
                if (store is not null)
                {
                    try
                    {
                        store.Remove(DnsLockdownRuleNames);
                        return;
                    }
                    catch (Exception ex)
                    {
                        log.Warning(ex, "[FirewallManager] COM DNS lockdown teardown failed - using netsh");
                    }
                }

                foreach (var name in DnsLockdownRuleNames)
                    RunNetshStatic(log, $"advfirewall firewall delete rule name=\"{name}\"");
            }, timeoutCts.Token).ConfigureAwait(false);

            log.Information(
                "[FirewallManager] DNS leak lockdown disabled — IPv4 + IPv6 firewall rules deleted (BR-9 r17 + r10 #6)");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            log.Warning("[FirewallManager] DNS leak lockdown teardown timed out after 5s — orphan rules may remain (CleanupOrphanedRules will sweep on next boot)");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[FirewallManager] DNS leak lockdown teardown failed (non-fatal)");
        }
    }

    internal static string? NormalizeTunAllowIp(string? tunCidr)
    {
        if (string.IsNullOrWhiteSpace(tunCidr)) return null;
        try
        {
            var trimmed = tunCidr.Trim();
            var slash = trimmed.IndexOf('/');
            var ip = slash >= 0 ? trimmed[..slash] : trimmed;
            var prefix = slash >= 0 && int.TryParse(trimmed[(slash + 1)..], out var p) ? p : 30;

            if (!System.Net.IPAddress.TryParse(ip, out var parsed))
                return null;
            if (parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return null;

            var bytes = parsed.GetAddressBytes();
            var hostBits = 32 - prefix;
            uint mask = hostBits >= 32 ? 0u : (0xFFFFFFFFu << hostBits);
            uint addr = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            uint network = addr & mask;
            var netBytes = new byte[]
            {
                (byte)(network >> 24),
                (byte)(network >> 16),
                (byte)(network >> 8),
                (byte)network,
            };
            var networkAddr = new System.Net.IPAddress(netBytes);
            return $"{networkAddr}/{prefix}";
        }
        catch
        {
            return null;
        }
    }

    internal static string? ComputeBlockExclusionRange(string? tunCidr)
    {
        if (string.IsNullOrWhiteSpace(tunCidr)) return null;
        try
        {
            var trimmed = tunCidr.Trim();
            var slash = trimmed.IndexOf('/');
            var ipPart = slash >= 0 ? trimmed[..slash] : trimmed;
            var prefix = slash >= 0 && int.TryParse(trimmed[(slash + 1)..], out var p) ? p : 30;

            if (!System.Net.IPAddress.TryParse(ipPart, out var parsed))
                return null;
            if (parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return null;

            var bytes = parsed.GetAddressBytes();
            var hostBits = 32 - prefix;
            uint mask = hostBits >= 32 ? 0u : (0xFFFFFFFFu << hostBits);
            uint addr = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            uint network = addr & mask;
            uint broadcast = network | (~mask & 0xFFFFFFFFu);

            if (network == 0u) return $"{Format(broadcast + 1u)}-255.255.255.255";
            if (broadcast == 0xFFFFFFFFu) return $"0.0.0.0-{Format(network - 1u)}";

            return $"0.0.0.0-{Format(network - 1u)},{Format(broadcast + 1u)}-255.255.255.255";

            static string Format(uint a) =>
                $"{(a >> 24) & 0xFF}.{(a >> 16) & 0xFF}.{(a >> 8) & 0xFF}.{a & 0xFF}";
        }
        catch
        {
            return null;
        }
    }

    private static bool RunNetshStatic(ILogger log, string arguments)
    {
        try
        {
            var argv = SplitShellArgs(arguments);
            var result = Runner.RunAsync(new ProcessRequest(
                ExecutablePath: "netsh.exe",
                Arguments: argv,
                Timeout: TimeSpan.FromMilliseconds(3000))).GetAwaiter().GetResult();

            if (result.TimedOut)
            {
                log.Warning("[FirewallManager] netsh timed out after 3s for: {Args}", arguments);
                return false;
            }

            if (result.ExitCode != 0)
            {
                log.Debug("[FirewallManager] netsh returned {Code} for: {Args} | stdout: {Out} | stderr: {Err}",
                    result.ExitCode, arguments, result.Stdout.Trim(), result.Stderr.Trim());
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[FirewallManager] netsh failed: {Args}", arguments);
            return false;
        }
    }
}
