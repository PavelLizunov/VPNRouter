using System.Net;
using System.Net.Sockets;
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
/// <para><strong>Full-tunnel signal</strong>: arming is governed by the explicit
/// <c>isFullTunnel</c> flag passed to <see cref="CreateBlockRules"/> (split tunnel
/// stays disarmed even if a process scan returns an empty list).</para>
///
/// <para><strong>The ruleset</strong> (loaded only while blocking) is a dedicated
/// <c>inet vpnrouter_ks</c> table with an output chain at <c>policy drop</c> that
/// passes loopback, RFC1918 / link-local LAN, and the VPN server IP(s) read from
/// <c>current.json</c>. The server pass is what lets sing-box reconnect during the
/// block window while the ruleset is active (local monitors or fallback can still
/// disengage the block). Server IPv4 and IPv6 addresses are passed so sing-box
/// can reconnect; all other IPv6 stays fully blocked (no v6 leak)
/// except loopback. The table exists ONLY while blocking — Disable/Delete remove
/// it entirely, so a disabled kill-switch leaves zero nft state.</para>
///
/// <para><strong>Privilege</strong>: the GUI runs as a normal user (only the
/// bundled sing-box is <c>setcap</c>'d). nft needs CAP_NET_ADMIN, so we shell
/// <c>sudo -n nft</c> exactly like macOS shells <c>sudo -n pfctl</c> — relying on
/// a NOPASSWD sudoers grant for nft. <strong>Fail-safe</strong>: if the grant is
/// missing, <c>sudo -n</c> fails, we log and DO NOT block (traffic follows normal
/// routing). Every Disable/Delete/Dispose path always tries to
/// remove the table.</para>
///
/// <para>Pure <see cref="IProcessRunner"/> orchestration (no Linux APIs) so the
/// exact nft command shapes are unit-tested on the Windows build; live
/// block / reconnect / teardown behaviour is verified on a real Linux host.
/// Default-OFF (only constructed + armed when a profile sets block_on_vpn_fail).</para>
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxFirewallManager : IFirewallManager, ICommittedFirewallConfig
{
    private const string Nft = "nft";
    private const string TableName = "vpnrouter_ks";

    private readonly object _gate = new();
    private readonly IProcessRunner _runner;
    private readonly ILogger _logger;
    private readonly string _currentConfigPath;
    private readonly string _markerPath;
    private readonly string _rulesetPath;
    private readonly Func<string, IReadOnlyList<string>> _resolveHost;

    private bool _armed;     // full-tunnel detected at CreateBlockRules
    private bool _loaded;    // our blocking table is live
    private List<string> _serverIps = new();
    private bool _disposed;

    internal IReadOnlyList<string> ServerIps { get { lock (_gate) { return _serverIps.ToArray(); } } }
    internal bool IsArmed { get { lock (_gate) { return _armed; } } }
    internal bool IsLoaded { get { lock (_gate) { return _loaded; } } }

    public LinuxFirewallManager(
        ILogger? logger = null,
        IProcessRunner? runner = null,
        string? currentConfigPath = null,
        string? markerPath = null,
        Func<string, IReadOnlyList<string>>? hostResolver = null,
        string? rulesetPath = null)
    {
        _logger = logger ?? Log.Logger;
        _runner = runner ?? new ProcessRunner();
        _currentConfigPath = currentConfigPath ?? AppPaths.CurrentConfigPath;
        // Crash-recovery sentinel: written when the block is engaged, deleted on
        // clean teardown. If it survives to the next launch, a hard kill stranded
        // the kill-switch and the orphan sweep removes the leftover nft table.
        _markerPath = markerPath ?? System.IO.Path.Combine(AppPaths.DataDir, "nft-killswitch-engaged.marker");
        _rulesetPath = rulesetPath ?? System.IO.Path.Combine(AppPaths.DataDir, "vpnrouter-nft-killswitch.conf");
        _resolveHost = hostResolver ?? DefaultResolveHost;
    }

    /// <inheritdoc />
    public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true)
    {
        lock (_gate)
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
    }

    /// <inheritdoc />
    public void EnableBlockRules()
    {
        lock (_gate)
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
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_rulesetPath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                AppPaths.WritePrivateText(_rulesetPath, ruleset);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[LinuxFirewall] failed to write nft ruleset file — NOT blocking");
                return;
            }

            var load = RunSudo(new[] { "-n", Nft, "-f", _rulesetPath });
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
    }

    /// <inheritdoc />
    public void DisableBlockRules()
    {
        lock (_gate)
        {
            if (!_loaded) return;
            if (!DeleteTable())
            {
                _logger.Warning("[LinuxFirewall] failed to remove nft table — retaining kill-switch state for retry");
                return;
            }
            TryDeleteMarker();
            _loaded = false;
            _logger.Information("[LinuxFirewall] nft kill-switch lifted (table removed)");
        }
    }

    /// <inheritdoc />
    public void DeleteAllRules()
    {
        lock (_gate)
        {
            // Fail-safe full teardown regardless of tracked state — used on clean
            // shutdown and orphan cleanup.
            if (!DeleteTable())
            {
                _logger.Warning("[LinuxFirewall] DeleteAllRules: failed to remove nft table — retaining state for retry");
                return;
            }
            TryDeleteMarker();
            _loaded = false;
            _armed = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            // Teardown backstop: if our blocking table was ever loaded, make sure
            // it's gone even on an abrupt shutdown.
            if (_loaded)
            {
                try
                {
                    if (DeleteTable())
                    {
                        TryDeleteMarker();
                        _loaded = false;
                        _disposed = true;
                    }
                }
                catch
                {
                    /* never throw from Dispose */
                }
            }
            else
            {
                _disposed = true;
            }
        }
    }

    /// <inheritdoc />
    void ICommittedFirewallConfig.UpdateCommittedConfig(string configJson, bool enabledForFullTunnel)
        => UpdateCommittedConfig(configJson, enabledForFullTunnel);

    internal void UpdateCommittedConfig(string configJson, bool enabledForFullTunnel)
    {
        lock (_gate)
        {
            if (_disposed) return;

            if (!enabledForFullTunnel)
            {
                _armed = false;
                DisableBlockRules();
                return;
            }

            List<string> candidateIps;
            try
            {
                candidateIps = ParseServerIps(configJson);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[LinuxFirewall] Failed to parse committed config JSON — retaining prior server IP list");
                return;
            }

            _armed = true;

            if (!_loaded)
            {
                _serverIps = candidateIps;
                _logger.Information("[LinuxFirewall] Updated committed server IP cache ({Count} IPs; ruleset not loaded)", _serverIps.Count);
                return;
            }

            var newRuleset = BuildRuleset(candidateIps);
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_rulesetPath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                AppPaths.WritePrivateText(_rulesetPath, newRuleset);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[LinuxFirewall] Failed to write nft ruleset file during refresh — retaining prior configuration");
                return;
            }

            var load = RunSudo(new[] { "-n", Nft, "-f", _rulesetPath });
            if (load.ok)
            {
                _serverIps = candidateIps;
                _logger.Information("[LinuxFirewall] Refreshed live nft kill-switch ruleset with {Count} server IP(s)", _serverIps.Count);
            }
            else
            {
                _logger.Warning(
                    "[LinuxFirewall] Failed to refresh live nft ruleset ({Err}) — retaining prior firewall pass-list; live config already committed, cannot rollback",
                    load.stderr?.Trim());
            }
        }
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Delete the dedicated nft table. Returns true on confirmed exit 0 without timeout,
    /// or when a failed delete is followed by a successful `nft -j list tables` inventory
    /// confirming the table is already absent.
    /// We NEVER guess absence from arbitrary exit codes or stderr error text.
    /// </summary>
    private bool DeleteTable()
    {
        if (RunSudo(new[] { "-n", Nft, "delete", "table", "inet", TableName }).ok)
            return true;

        return IsTableAbsent();
    }

    private bool IsTableAbsent()
    {
        var (ok, stdout, _) = RunSudo(new[] { "-n", Nft, "-j", "list", "tables" });
        if (!ok || string.IsNullOrWhiteSpace(stdout))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("nftables", out var nftables) ||
                nftables.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var elem in nftables.EnumerateArray())
            {
                if (elem.ValueKind != JsonValueKind.Object)
                    return false;

                var propCount = 0;
                JsonProperty singleProp = default;
                foreach (var prop in elem.EnumerateObject())
                {
                    propCount++;
                    if (propCount > 1)
                        return false;
                    singleProp = prop;
                }

                if (propCount == 0)
                    return false;

                if (singleProp.NameEquals("table"))
                {
                    var tbl = singleProp.Value;
                    if (tbl.ValueKind != JsonValueKind.Object)
                        return false;

                    if (!tbl.TryGetProperty("family", out var fam) ||
                        fam.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(fam.GetString()))
                    {
                        return false;
                    }

                    if (!tbl.TryGetProperty("name", out var name) ||
                        name.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(name.GetString()))
                    {
                        return false;
                    }

                    if (fam.ValueEquals("inet") && name.ValueEquals(TableName))
                    {
                        return false;
                    }

                    continue;
                }

                if (singleProp.NameEquals("metainfo"))
                {
                    if (singleProp.Value.ValueKind != JsonValueKind.Object)
                        return false;

                    continue;
                }

                return false;
            }

            _logger.Debug("[LinuxFirewall] nft table {Table} confirmed absent via table inventory", TableName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[LinuxFirewall] failed to parse nft table inventory JSON");
            return false;
        }
    }

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

    internal List<string> ParseServerIps(string configJson)
    {
        var ips = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var candidate = raw.Trim();

            if (IPAddress.TryParse(candidate, out var parsedIp))
            {
                var canonical = parsedIp.ToString();
                if (seen.Add(canonical))
                {
                    ips.Add(canonical);
                }
                return;
            }

            // Hostname server — nft rules take literal IPs only, so resolve NOW
            // (while the VPN is healthy) and pass-list the resolved IP(s).
            // Without this the kill-switch would block the crash-reconnect to a hostname server.
            try
            {
                var resolved = _resolveHost(candidate);
                if (resolved == null) return;
                foreach (var rip in resolved)
                {
                    if (string.IsNullOrWhiteSpace(rip)) continue;
                    var trimmedRip = rip.Trim();
                    if (IPAddress.TryParse(trimmedRip, out var parsedResolvedIp))
                    {
                        var canonical = parsedResolvedIp.ToString();
                        if (seen.Add(canonical))
                        {
                            ips.Add(canonical);
                        }
                    }
                    else
                    {
                        _logger.Debug("[LinuxFirewall] ignored invalid resolver literal for {Host}", candidate);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[LinuxFirewall] could not resolve server hostname {Host} — kill-switch reconnect may need manual cleanup", candidate);
            }
        }

        using var doc = JsonDocument.Parse(configJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Expected JSON object root, got {root.ValueKind}.");

        if (root.TryGetProperty("outbounds", out var obs) && obs.ValueKind == JsonValueKind.Array)
        {
            foreach (var ob in obs.EnumerateArray())
            {
                if (ob.ValueKind == JsonValueKind.Object &&
                    ob.TryGetProperty("server", out var srv) &&
                    srv.ValueKind == JsonValueKind.String)
                {
                    AddCandidate(srv.GetString());
                }
            }
        }

        if (root.TryGetProperty("endpoints", out var eps) && eps.ValueKind == JsonValueKind.Array)
        {
            foreach (var ep in eps.EnumerateArray())
            {
                if (ep.ValueKind != JsonValueKind.Object) continue;

                if (!ep.TryGetProperty("type", out var typeProp) ||
                    typeProp.ValueKind != JsonValueKind.String ||
                    !string.Equals(typeProp.GetString(), "wireguard", StringComparison.OrdinalIgnoreCase))
                {
                    // Unknown or non-wireguard endpoint type ignored
                    continue;
                }

                // CRITICAL: NEVER read ep["address"] (local tunnel addresses) or peer["allowed_ips"].
                // Only read known type wireguard endpoints[].peers[].address.
                if (!ep.TryGetProperty("peers", out var peers) || peers.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var peer in peers.EnumerateArray())
                {
                    if (peer.ValueKind == JsonValueKind.Object &&
                        peer.TryGetProperty("address", out var addrProp) &&
                        addrProp.ValueKind == JsonValueKind.String)
                    {
                        AddCandidate(addrProp.GetString());
                    }
                }
            }
        }

        return ips;
    }

    internal List<string> ReadServerIps()
    {
        try
        {
            if (!System.IO.File.Exists(_currentConfigPath)) return new List<string>();
            return ParseServerIps(System.IO.File.ReadAllText(_currentConfigPath));
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[LinuxFirewall] could not read server IPs from {Path}", _currentConfigPath);
            return new List<string>();
        }
    }

    /// <summary>Bounded DNS resolve → IPv4 and IPv6 literals. Best-effort; empty on failure.</summary>
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
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork ||
                            a.AddressFamily == AddressFamily.InterNetworkV6)
                .Select(a => a.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
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
        lock (_gate)
        {
            var log = logger ?? _logger;
            try
            {
                if (!System.IO.File.Exists(_markerPath)) return;
                log.Warning("[LinuxFirewall] engaged kill-switch marker from a prior session found (hard kill?) — removing leftover nft table");
                var ok = DeleteTable();
                if (ok)
                {
                    TryDeleteMarker();
                    _loaded = false;
                    log.Information("[LinuxFirewall] orphan cleanup: nft table removed — egress restored");
                }
                else
                {
                    log.Warning("[LinuxFirewall] orphan cleanup: nft delete table failed (already gone, or no sudoers grant) — if the internet is blocked, run: sudo nft delete table inet {Table}", TableName);
                }
            }
            catch (Exception ex) { log.Warning(ex, "[LinuxFirewall] orphan cleanup failed"); }
        }
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
            var ok = r.ExitCode == 0 && !r.TimedOut;
            if (!ok)
                _logger.Debug("[LinuxFirewall] sudo {Args} exited {Code} (timedOut={TimedOut}): {Err}",
                    string.Join(' ', args), r.ExitCode, r.TimedOut, r.Stderr?.Trim());
            return (ok, r.Stdout ?? string.Empty, r.Stderr ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[LinuxFirewall] sudo run failed");
            return (false, string.Empty, string.Empty);
        }
    }
}
