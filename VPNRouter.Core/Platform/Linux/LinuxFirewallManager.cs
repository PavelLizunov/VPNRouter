using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Platform.Linux;

/// <summary>
/// Linux nftables-based kill-switch for <c>block_on_vpn_fail</c>. GLOBAL egress
/// block, engaged ONLY in full-tunnel mode — the Linux counterpart to
/// <see cref="VPNRouter.Core.Platform.macOS.MacFirewallManager"/>.
///
/// <para><strong>Why global + full-tunnel-only</strong>: nftables filters by
/// address / interface / uid, NOT by process image, so it cannot block just the
/// routed apps the way Windows netsh does. The chosen semantics mirror the macOS
/// pf design: a global egress block that engages only in full-tunnel (where
/// blocking everything is correct); split tunnel stays a labelled no-op. See
/// <c>plans/firewall-killswitch-linux-macos-2026-06-02.md</c>.</para>
///
/// <para><strong>Full-tunnel signal</strong>: <see cref="CreateBlockRules"/> is
/// called with an EMPTY process list (the startup pipeline skips the process scan
/// in full tunnel); a non-empty list means split tunnel → stay disarmed.</para>
///
/// <para><strong>The ruleset</strong> (loaded only while blocking) is a dedicated
/// <c>inet vpnrouter_ks</c> table with an output chain at <c>policy drop</c> that
/// passes loopback, RFC1918 / link-local LAN, and the VPN server IP(s) read from
/// <c>current.json</c>. The server pass is what lets sing-box reconnect during the
/// block window; without it HealthMonitor would never see a healthy restart and
/// the host would stay blocked forever. IPv6 stays fully blocked (no v6 leak)
/// except loopback. The table exists ONLY while blocking — Disable/Delete remove
/// it entirely, so a disabled kill-switch leaves zero nft state.</para>
///
/// <para><strong>Privilege</strong>: the GUI runs as a normal user (only the
/// bundled sing-box is <c>setcap</c>'d). nft needs CAP_NET_ADMIN, so we shell
/// <c>sudo -n nft</c> exactly like macOS shells <c>sudo -n pfctl</c> — relying on
/// a NOPASSWD sudoers grant for nft. <strong>Fail-safe</strong>: if the grant is
/// missing, <c>sudo -n</c> fails, we log and DO NOT block (traffic follows normal
/// routing) — never a brick. Every Disable/Delete/Dispose path always tries to
/// remove the table.</para>
///
/// <para>Pure <see cref="IProcessRunner"/> orchestration (no Linux APIs) so the
/// exact nft command shapes are unit-tested on the Windows build; live
/// block / reconnect / no-brick behaviour is verified on a real Linux host.
/// Default-OFF (only constructed + armed when a profile sets block_on_vpn_fail).</para>
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxFirewallManager : IFirewallManager
{
    private const string Nft = "nft";
    private const string TableName = "vpnrouter_ks";

    private readonly IProcessRunner _runner;
    private readonly ILogger _logger;
    private readonly string _currentConfigPath;
    private readonly string _markerPath;
    private readonly Func<string, IReadOnlyList<string>> _resolveHost;

    private bool _armed;     // full-tunnel detected at CreateBlockRules
    private bool _loaded;    // our blocking table is live
    private List<string> _serverIps = new();
    private bool _disposed;

    public LinuxFirewallManager(
        ILogger? logger = null,
        IProcessRunner? runner = null,
        string? currentConfigPath = null,
        string? markerPath = null,
        Func<string, IReadOnlyList<string>>? hostResolver = null)
    {
        _logger = logger ?? Log.Logger;
        _runner = runner ?? new ProcessRunner();
        _currentConfigPath = currentConfigPath ?? AppPaths.CurrentConfigPath;
        // Crash-recovery sentinel: written when the block is engaged, deleted on
        // clean teardown. If it survives to the next launch, a hard kill stranded
        // the kill-switch and the orphan sweep removes the leftover nft table.
        _markerPath = markerPath ?? System.IO.Path.Combine(AppPaths.DataDir, "nft-killswitch-engaged.marker");
        _resolveHost = hostResolver ?? DefaultResolveHost;
    }

    /// <inheritdoc />
    public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true)
    {
        var names = (processNames ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

        // P1 (2026-07-10): arm on the EXPLICIT routing intent, NEVER on list
        // emptiness. Pre-fix `names.Count == 0` meant "full tunnel" — so a
        // SPLIT-tunnel user whose process scan timed out (an empty list) had the
        // WHOLE host's egress dropped on a crash. nft can't block per-process, so
        // split stays a labelled no-op no matter what the scan returned.
        if (!isFullTunnel)
        {
            _armed = false;
            _logger.Information(
                "[LinuxFirewall] split tunnel ({N} routed app(s)) → nft kill-switch is full-tunnel-only " +
                "on Linux (per-process blocking impossible with nft) — staying disarmed", names.Count);
            return;
        }

        _serverIps = ReadServerIps();
        _armed = true;
        _logger.Information(
            "[LinuxFirewall] Armed full-tunnel nft kill-switch (disabled until VPN failure). " +
            "Allow-list: lo + RFC1918/link-local + {Count} server IP(s)", _serverIps.Count);
    }

    /// <inheritdoc />
    public void EnableBlockRules()
    {
        if (_disposed) return;
        if (!_armed)
        {
            _logger.Warning(
                "[LinuxFirewall] EnableBlockRules: not armed (split tunnel / no block_on_vpn_fail) — " +
                "NOT blocking; traffic follows normal routing");
            return;
        }
        if (_loaded) return; // idempotent

        var ruleset = BuildRuleset(_serverIps);
        string tmp;
        try
        {
            tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vpnrouter-nft-killswitch.conf");
            System.IO.File.WriteAllText(tmp, ruleset);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[LinuxFirewall] failed to write nft ruleset file — NOT blocking");
            return;
        }

        var load = RunSudo(new[] { "-n", Nft, "-f", tmp });
        if (load.ok)
        {
            _loaded = true;
            WriteMarker(); // sentinel so a hard kill is recoverable on next launch
            _logger.Information("[LinuxFirewall] nft kill-switch ENGAGED — blocking all egress except lo/LAN/server");
        }
        else
        {
            _logger.Warning(
                "[LinuxFirewall] FAILED to load nft ruleset (nft missing, or no NOPASSWD sudoers grant for nft? {Err}) — " +
                "NOT blocking; traffic follows normal routing", load.stderr?.Trim());
        }
    }

    /// <inheritdoc />
    public void DisableBlockRules()
    {
        if (!_loaded) return;
        DeleteTable();
        TryDeleteMarker();
        _loaded = false;
        _logger.Information("[LinuxFirewall] nft kill-switch lifted (table removed)");
    }

    /// <inheritdoc />
    public void DeleteAllRules()
    {
        // Fail-safe full teardown regardless of tracked state — used on clean
        // shutdown and orphan cleanup.
        DeleteTable();
        TryDeleteMarker();
        _loaded = false;
        _armed = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Anti-brick backstop: if our blocking table was ever loaded, make sure
        // it's gone even on an abrupt shutdown.
        if (_loaded)
        {
            try { DeleteTable(); TryDeleteMarker(); } catch { /* never throw from Dispose */ }
            _loaded = false;
        }
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private void DeleteTable()
        => RunSudo(new[] { "-n", Nft, "delete", "table", "inet", TableName }); // ignore "No such file" when absent

    /// <summary>
    /// Build the nft ruleset (atomic add+flush+rules in one -f file): a dedicated
    /// <c>inet vpnrouter_ks</c> table whose output chain defaults to <c>drop</c>
    /// and passes loopback, the private/link-local ranges, and each VPN server IP
    /// (so sing-box can reconnect), split by family into <c>ip daddr</c> (IPv4)
    /// and <c>ip6 daddr</c> (IPv6) rules; all other IPv6 stays dropped by policy.
    /// <c>add table</c> is idempotent; <c>flush table</c> makes the load a clean
    /// replace if a stale table somehow survived.
    /// </summary>
    internal static string BuildRuleset(List<string> serverIps)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"add table inet {TableName}");
        sb.AppendLine($"flush table inet {TableName}");
        sb.AppendLine($"add chain inet {TableName} output {{ type filter hook output priority 0 ; policy drop ; }}");
        sb.AppendLine($"add rule inet {TableName} output oif \"lo\" accept");
        sb.AppendLine($"add rule inet {TableName} output ip daddr {{ 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 }} accept");
        var v4 = serverIps.Where(ip => !ip.Contains(':')).ToList();
        var v6 = serverIps.Where(ip => ip.Contains(':')).ToList();
        if (v4.Count > 0)
            sb.AppendLine($"add rule inet {TableName} output ip daddr {{ {string.Join(", ", v4)} }} accept");
        if (v6.Count > 0)
            sb.AppendLine($"add rule inet {TableName} output ip6 daddr {{ {string.Join(", ", v6)} }} accept");
        return sb.ToString();
    }

    private List<string> ReadServerIps()
    {
        var ips = new List<string>();
        try
        {
            if (!System.IO.File.Exists(_currentConfigPath)) return ips;
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(_currentConfigPath));
            if (doc.RootElement.TryGetProperty("outbounds", out var obs) && obs.ValueKind == JsonValueKind.Array)
            {
                foreach (var ob in obs.EnumerateArray())
                {
                    if (ob.TryGetProperty("server", out var srv) && srv.ValueKind == JsonValueKind.String)
                    {
                        var s = srv.GetString();
                        if (string.IsNullOrWhiteSpace(s)) continue;
                        if (System.Net.IPAddress.TryParse(s, out _))
                        {
                            if (!ips.Contains(s!)) ips.Add(s!);
                        }
                        else
                        {
                            // Hostname server — nft rules take literal IPs only, so
                            // resolve NOW (while the VPN is healthy) and pass-list the
                            // resolved IP(s). Without this the kill-switch would block
                            // the crash-reconnect to a hostname server → bricked host.
                            foreach (var rip in _resolveHost(s!))
                                if (!ips.Contains(rip)) ips.Add(rip);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[LinuxFirewall] could not read server IPs from {Path}", _currentConfigPath);
        }
        return ips;
    }

    /// <summary>Bounded DNS resolve → IPv4 literals. Best-effort; empty on failure.</summary>
    private IReadOnlyList<string> DefaultResolveHost(string host)
    {
        try
        {
            var task = System.Net.Dns.GetHostAddressesAsync(host);
            if (!task.Wait(TimeSpan.FromSeconds(3)))
            {
                _logger.Warning("[LinuxFirewall] DNS resolve of {Host} timed out — kill-switch reconnect may need manual cleanup", host);
                return Array.Empty<string>();
            }
            return task.Result
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .Distinct()
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[LinuxFirewall] could not resolve server hostname {Host} — kill-switch reconnect may need manual cleanup", host);
            return Array.Empty<string>();
        }
    }

    private void WriteMarker()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_markerPath)!);
            System.IO.File.WriteAllText(_markerPath, "engaged");
        }
        catch { /* best-effort; absence just means the orphan sweep won't auto-run */ }
    }

    private void TryDeleteMarker()
    {
        try { if (System.IO.File.Exists(_markerPath)) System.IO.File.Delete(_markerPath); }
        catch { /* swallow */ }
    }

    /// <summary>
    /// Orphan recovery: if our engaged-marker survived (a prior session was
    /// HARD-killed while the kill-switch was live, so Dispose never ran), delete
    /// the leftover nft table so the host isn't stranded with no internet. No-op
    /// when the marker is absent — a normal launch never touches nft.
    /// </summary>
    internal void CleanupOrphanedRules(ILogger? logger)
    {
        var log = logger ?? _logger;
        try
        {
            if (!System.IO.File.Exists(_markerPath)) return;
            log.Warning("[LinuxFirewall] engaged kill-switch marker from a prior session found (hard kill?) — removing leftover nft table");
            var ok = RunSudo(new[] { "-n", Nft, "delete", "table", "inet", TableName }).ok;
            TryDeleteMarker();
            if (ok)
                log.Information("[LinuxFirewall] orphan cleanup: nft table removed — egress restored");
            else
                log.Warning("[LinuxFirewall] orphan cleanup: nft delete table failed (already gone, or no sudoers grant) — if the internet is blocked, run: sudo nft delete table inet {Table}", TableName);
        }
        catch (Exception ex) { log.Warning(ex, "[LinuxFirewall] orphan cleanup failed"); }
    }

    /// <summary>
    /// Static entry for app startup / process-exit — mirrors macOS
    /// <c>MacFirewallManager.TryCleanupOrphanedRulesSafe</c>. Marker-gated, so it's
    /// a no-op unless a prior session was hard-killed while the kill-switch was on.
    /// Never throws.
    /// </summary>
    public static void TryCleanupOrphanedRulesSafe(ILogger? logger)
    {
        try { new LinuxFirewallManager(logger).CleanupOrphanedRules(logger); } catch { /* never throw from a startup hook */ }
    }

    private (bool ok, string stdout, string stderr) RunSudo(string[] args)
    {
        try
        {
            var req = new ProcessRequest("/usr/bin/sudo", args, CaptureStdout: true, CaptureStderr: true);
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var r = _runner.RunAsync(req, cts.Token).GetAwaiter().GetResult();
            if (r.ExitCode != 0)
                _logger.Debug("[LinuxFirewall] sudo {Args} exited {Code}: {Err}",
                    string.Join(' ', args), r.ExitCode, r.Stderr?.Trim());
            return (r.ExitCode == 0, r.Stdout ?? string.Empty, r.Stderr ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[LinuxFirewall] sudo run failed");
            return (false, string.Empty, string.Empty);
        }
    }
}
