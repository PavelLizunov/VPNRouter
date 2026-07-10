using System.Text;
using System.Text.Json;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Platform.macOS;

/// <summary>
/// macOS pf-based kill-switch for <c>block_on_vpn_fail</c> (r6). GLOBAL egress
/// block, engaged ONLY in full-tunnel mode.
///
/// <para>Why global + full-tunnel-only: pf filters packets (IP/port/interface),
/// it has NO concept of process, so it cannot block just the routed apps the way
/// Windows netsh does. The user-chosen semantics are therefore a global egress
/// block that engages only in full-tunnel (where blocking everything is correct);
/// split tunnel stays a labelled no-op. Full design + rationale:
/// <c>plans/phase3-macos-pf-killswitch-r6-design-2026-06-04.md</c>.</para>
///
/// <para>Full-tunnel signal: <see cref="CreateBlockRules"/> is called with an
/// EMPTY process list (the startup pipeline skips the process scan in full
/// tunnel); a non-empty list means split tunnel → stay disarmed.</para>
///
/// <para>CRITICAL — the ruleset blocks all outbound EXCEPT loopback, RFC1918 /
/// link-local, and the VPN server IP(s) (read from <c>current.json</c>). The
/// server pass is what lets sing-box reconnect during the block window; without
/// it HealthMonitor would never see a healthy restart and the Mac would stay
/// blocked forever (bricked). IPv6 stays fully blocked (no v6 leak).</para>
///
/// <para>Pure <see cref="IProcessRunner"/> orchestration (no macOS APIs) so the
/// exact pfctl command shapes are unit-tested on the Windows build; the live
/// block / reconnect / no-brick behaviour is verified on the Mac host via the
/// kill-9 SSH gate. Default-OFF (only constructed+armed when a profile sets
/// block_on_vpn_fail). Fail-safe: Disable / Delete / Dispose ALWAYS lift the
/// block (anchor flush; legacy engage → stock-ruleset restore) + release our
/// pf-enable ref — never leave the Mac blocked.</para>
///
/// <para>P0.3 (2026-07-10): rules live in the dedicated anchor
/// <c>com.vpnrouter/killswitch</c> instead of replacing the main ruleset. The
/// first engage ensures the main ruleset carries the anchor call (without it
/// anchor rules are inert — proven live); disable/teardown then touch ONLY the
/// anchor, so other pf users' runtime state (e.g. another VPN's anchor) survives
/// our disengage — the pre-P0.3 broad <c>pfctl -f /etc/pf.conf</c> restore wiped
/// it on every shutdown.</para>
/// </summary>
public sealed class MacFirewallManager : IFirewallManager
{
    private const string DefaultPfConf = "/etc/pf.conf";
    private const string PfCtl = "/sbin/pfctl";

    /// <summary>
    /// P0.3 (2026-07-10): dedicated pf anchor. Rules are loaded INTO this anchor
    /// (<c>pfctl -a … -f</c>) and are evaluated ONLY because Enable also ensures a
    /// carrier line <c>anchor "com.vpnrouter/killswitch"</c> exists in the main
    /// ruleset — stock macOS references only <c>com.apple/*</c>, so without the
    /// carrier the anchor rules are INERT (a dead kill-switch). Proven live on the
    /// Mac host 2026-07-10; see plans/macos-p0.3-pf-anchor-corrected-design-2026-07-10.md.
    /// </summary>
    internal const string Anchor = "com.vpnrouter/killswitch";
    internal const string AnchorMarker = "anchor-v1";  // marker content in anchor mode
    private const string LegacyMarker = "engaged";      // pre-P0.3 broad-load mode

    private readonly IProcessRunner _runner;
    private readonly ILogger _logger;
    private readonly string _currentConfigPath;
    private readonly string _markerPath;
    private readonly string _pfConfPath;
    private readonly Func<string, IReadOnlyList<string>> _resolveHost;

    private bool _armed;            // full-tunnel detected at CreateBlockRules
    private bool _loaded;           // our blocking ruleset is live
    private bool _anchorMode;       // true = engaged via the anchor; false = legacy broad load
    private string? _enableToken;   // pfctl -E ref-count token
    private List<string> _serverIps = new();
    private bool _disposed;

    public MacFirewallManager(
        ILogger? logger = null,
        IProcessRunner? runner = null,
        string? currentConfigPath = null,
        string? markerPath = null,
        Func<string, IReadOnlyList<string>>? hostResolver = null,
        string? pfConfPath = null)
    {
        _logger = logger ?? Log.Logger;
        _runner = runner ?? new ProcessRunner();
        _currentConfigPath = currentConfigPath ?? AppPaths.CurrentConfigPath;
        // Crash-recovery sentinel: written when the block is engaged, deleted on
        // clean teardown. If it survives to the next launch, a hard kill stranded
        // the kill-switch and the orphan sweep cleans up (anchor flush for
        // anchor-v1, stock-ruleset restore for legacy).
        _markerPath = markerPath ?? System.IO.Path.Combine(AppPaths.DataDir, "pf-killswitch-engaged.marker");
        _pfConfPath = pfConfPath ?? DefaultPfConf;
        _resolveHost = hostResolver ?? DefaultResolveHost;
    }

    /// <inheritdoc />
    public void CreateBlockRules(IEnumerable<string> processNames)
    {
        var names = (processNames ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

        if (names.Count > 0)
        {
            // Split tunnel: pf can't block per-process, so we don't engage —
            // blocking everything would also kill the never-tunnelled direct
            // apps. Honest no-op (the leak-toggle UI already labels this).
            _armed = false;
            _logger.Information(
                "[MacFirewall] {N} routed app(s) → split tunnel; pf kill-switch is full-tunnel-only " +
                "on macOS (per-process blocking impossible with pf) — staying disarmed", names.Count);
            return;
        }

        _serverIps = ReadServerIps();
        _armed = true;
        _logger.Information(
            "[MacFirewall] Armed full-tunnel pf kill-switch (disabled until VPN failure). " +
            "Allow-list: lo0 + RFC1918/link-local + {Count} server IP(s)", _serverIps.Count);
    }

    /// <inheritdoc />
    public void EnableBlockRules()
    {
        if (_disposed) return;
        if (!_armed)
        {
            _logger.Warning(
                "[MacFirewall] EnableBlockRules: not armed (split tunnel / no block_on_vpn_fail) — " +
                "NOT blocking; traffic follows normal routing");
            return;
        }
        if (_loaded) return; // idempotent

        var rules = BuildRules(_serverIps);
        string tmp;
        try
        {
            tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vpnrouter-pf-killswitch.conf");
            System.IO.File.WriteAllText(tmp, rules);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[MacFirewall] failed to write pf rules file — NOT blocking");
            return;
        }

        // Enable pf (ref-counted) and capture the token so Disable can release
        // OUR reference without disturbing other pf users.
        var en = RunSudo(new[] { "-n", PfCtl, "-E" });
        if (en.ok) _enableToken = ParsePfToken(en.stderr);

        // ── P0.3 anchor mode ──
        // 1. Ensure the main ruleset CARRIES our anchor. pf evaluates anchor
        //    rules only when the main ruleset contains an `anchor "…"` call —
        //    stock macOS calls only com.apple/*, so loading rules into the
        //    anchor without the carrier is a silent no-op (dead kill-switch,
        //    proven live 2026-07-10). The carrier is added by reloading
        //    /etc/pf.conf + one trailing line; once present it stays across
        //    engage cycles (an EMPTY anchor is inert), so subsequent enables
        //    touch ONLY the anchor.
        // 2. Load the block rules into the anchor.
        // Failure of either step → release + stay unloaded (never claim a block
        // we didn't prove; the empty carrier left by a step-2 failure is inert).
        if (EnsureCarrier())
        {
            var load = RunSudo(new[] { "-n", PfCtl, "-a", Anchor, "-f", tmp });
            if (load.ok)
            {
                _loaded = true;
                _anchorMode = true;
                WriteMarker(AnchorMarker); // sentinel so a hard kill is recoverable on next launch
                _logger.Information(
                    "[MacFirewall] pf kill-switch ENGAGED (anchor {Anchor}) — blocking all egress except lo0/LAN/server", Anchor);
                return;
            }
            _logger.Warning(
                "[MacFirewall] FAILED to load anchor ruleset (pfctl sudoers grant missing? {Err}) — NOT blocking; " +
                "releasing pf-enable ref", load.stderr?.Trim());
            ReleaseEnable();
            return;
        }

        // Legacy fallback: /etc/pf.conf unreadable or the carrier load failed —
        // fall back to the pre-P0.3 broad main-ruleset load so the kill-switch
        // still BLOCKS (correctness over blast-radius hygiene).
        _logger.Warning("[MacFirewall] anchor carrier unavailable — falling back to legacy broad pf load");
        var legacy = RunSudo(new[] { "-n", PfCtl, "-f", tmp });
        if (legacy.ok)
        {
            _loaded = true;
            _anchorMode = false;
            WriteMarker(LegacyMarker);
            _logger.Information("[MacFirewall] pf kill-switch ENGAGED (legacy broad load)");
        }
        else
        {
            _logger.Warning(
                "[MacFirewall] FAILED to load pf ruleset (pfctl sudoers grant missing? {Err}) — NOT blocking; " +
                "releasing pf-enable ref", legacy.stderr?.Trim());
            ReleaseEnable(); // don't leave pf enabled-by-us with no blocking ruleset
        }
    }

    /// <inheritdoc />
    public void DisableBlockRules()
    {
        if (!_loaded) return;
        if (_anchorMode)
            FlushAnchor();          // touch ONLY our anchor; main ruleset (and other
                                    // tools' runtime anchors, e.g. amn/*) stay intact
        else
            RestoreDefaultRuleset(); // legacy engage → legacy restore
        ReleaseEnable();
        TryDeleteMarker();
        _loaded = false;
        _logger.Information("[MacFirewall] pf kill-switch lifted ({Mode})",
            _anchorMode ? "anchor flushed" : "default ruleset restored");
    }

    /// <inheritdoc />
    public void DeleteAllRules()
    {
        // Fail-safe teardown — used on clean shutdown and orphan cleanup.
        // Anchor flush is a harmless no-op when nothing is loaded, so (unlike the
        // pre-P0.3 unconditional /etc/pf.conf reload, which stomped OTHER tools'
        // runtime pf state on every shutdown) this never touches the main
        // ruleset unless a LEGACY broad load is actually live.
        if (_loaded && !_anchorMode)
            RestoreDefaultRuleset();
        else
            FlushAnchor();
        ReleaseEnable();
        TryDeleteMarker();
        _loaded = false;
        _armed = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Anti-brick backstop: if our blocking ruleset was ever loaded, make
        // sure it's gone even on an abrupt shutdown.
        if (_loaded)
        {
            try
            {
                if (_anchorMode) FlushAnchor(); else RestoreDefaultRuleset();
                ReleaseEnable();
                TryDeleteMarker();
            }
            catch { /* never throw from Dispose */ }
            _loaded = false;
        }
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private void RestoreDefaultRuleset()
        => RunSudo(new[] { "-n", PfCtl, "-f", DefaultPfConf }); // reload stock macOS ruleset

    private void FlushAnchor()
        => RunSudo(new[] { "-n", PfCtl, "-a", Anchor, "-F", "rules" });

    /// <summary>
    /// Make sure the main ruleset calls our anchor. Checks the live filter rules
    /// (<c>pfctl -sr</c>) first so repeat engages don't reload the main ruleset;
    /// when absent, loads <c>/etc/pf.conf</c> content + one trailing
    /// <c>anchor "com.vpnrouter/killswitch"</c> line.
    /// ponytail: reloading pf.conf+carrier drops OTHER tools' runtime-added
    /// carrier lines (same class as the pre-P0.3 behaviour, but now only on the
    /// FIRST engage instead of every enable/disable); faithful live-ruleset
    /// merge via -sr/-sn reconstruction is the upgrade path if a real
    /// coexistence report ever needs it.
    /// </summary>
    private bool EnsureCarrier()
    {
        var sr = RunSudo(new[] { "-n", PfCtl, "-sr" });
        if (sr.ok && sr.stdout.Contains(Anchor, StringComparison.Ordinal))
            return true; // carrier already present (prior engage this boot)

        string conf;
        try
        {
            conf = System.IO.File.ReadAllText(_pfConfPath);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[MacFirewall] cannot read {PfConf} to add the anchor carrier", _pfConfPath);
            return false;
        }

        string merged;
        try
        {
            merged = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vpnrouter-pf-main.conf");
            if (!conf.EndsWith('\n')) conf += "\n";
            System.IO.File.WriteAllText(merged, conf + $"anchor \"{Anchor}\"\n");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[MacFirewall] failed to write merged pf main ruleset");
            return false;
        }

        var load = RunSudo(new[] { "-n", PfCtl, "-f", merged });
        if (!load.ok)
            _logger.Warning("[MacFirewall] failed to load main ruleset with anchor carrier: {Err}", load.stderr?.Trim());
        return load.ok;
    }

    private void ReleaseEnable()
    {
        if (_enableToken == null) return;
        RunSudo(new[] { "-n", PfCtl, "-X", _enableToken });
        _enableToken = null;
    }

    /// <summary>
    /// Build the pf ruleset: block all outbound, then pass loopback, the
    /// private/link-local ranges, and each VPN server IP (so sing-box can
    /// reconnect). IPv4 passes only — <c>block drop out all</c> keeps IPv6 shut.
    /// No <c>set</c> options: <c>set</c> is main-ruleset-only, so it would fail
    /// the P0.3 anchor load (<c>pfctl -a … -f</c>); pf's default block-policy is
    /// drop anyway, and <c>block drop</c> states it per-rule.
    /// </summary>
    internal static string BuildRules(List<string> serverIps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("block drop out all");
        sb.AppendLine("pass out quick on lo0 all");
        sb.AppendLine("pass out quick inet from any to 10.0.0.0/8");
        sb.AppendLine("pass out quick inet from any to 172.16.0.0/12");
        sb.AppendLine("pass out quick inet from any to 192.168.0.0/16");
        sb.AppendLine("pass out quick inet from any to 169.254.0.0/16");
        foreach (var ip in serverIps)
            sb.AppendLine($"pass out quick inet from any to {ip}");
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
                            // Hostname server (Reality usually uses IPs, but a
                            // subscription can hand out hostnames). pf rules take
                            // literal IPs only, so resolve NOW — while the VPN is
                            // healthy — and add the resolved IP(s) to the pass-list.
                            // Without this the kill-switch would block the
                            // crash-reconnect to a hostname server → bricked Mac.
                            foreach (var rip in _resolveHost(s!))
                                if (!ips.Contains(rip)) ips.Add(rip);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[MacFirewall] could not read server IPs from {Path}", _currentConfigPath);
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
                _logger.Warning("[MacFirewall] DNS resolve of {Host} timed out — kill-switch reconnect may need manual cleanup", host);
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
            _logger.Warning(ex, "[MacFirewall] could not resolve server hostname {Host} — kill-switch reconnect may need manual cleanup", host);
            return Array.Empty<string>();
        }
    }

    private void WriteMarker(string mode)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_markerPath)!);
            // Content encodes HOW we engaged ("anchor-v1" vs legacy "engaged") so
            // the post-crash orphan sweep knows whether an anchor flush suffices
            // or the pre-P0.3 full-ruleset restore is needed.
            System.IO.File.WriteAllText(_markerPath, mode);
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
    /// HARD-killed — kill -9 / crash / power loss — while the kill-switch was
    /// live, so Dispose never ran), unblock the Mac. Marker content picks the
    /// path: <c>anchor-v1</c> → flush ONLY our anchor (the main ruleset was
    /// never ours to restore); legacy <c>engaged</c> / unknown → the pre-P0.3
    /// stock-ruleset reload. A fresh process can't know the old <c>pfctl -E</c>
    /// token, so the enable ref may leak (logged); an enabled pf with an empty
    /// anchor is harmless. No-op when the marker is absent — a normal launch
    /// never touches pf.
    /// </summary>
    internal void CleanupOrphanedRules(ILogger? logger)
    {
        var log = logger ?? _logger;
        try
        {
            if (!System.IO.File.Exists(_markerPath)) return;
            string content;
            try { content = System.IO.File.ReadAllText(_markerPath).Trim(); }
            catch { content = LegacyMarker; }

            bool ok;
            if (content == AnchorMarker)
            {
                log.Warning("[MacFirewall] engaged kill-switch marker (anchor-v1) from a prior session found (hard kill?) — flushing anchor {Anchor}", Anchor);
                ok = RunSudo(new[] { "-n", PfCtl, "-a", Anchor, "-F", "rules" }).ok;
            }
            else
            {
                log.Warning("[MacFirewall] engaged kill-switch marker (legacy) from a prior session found (hard kill?) — restoring default pf ruleset");
                ok = RunSudo(new[] { "-n", PfCtl, "-f", DefaultPfConf }).ok;
            }
            TryDeleteMarker();
            if (ok)
                log.Information("[MacFirewall] orphan cleanup: egress unblocked (a lost pfctl -E token cannot be released by a new process)");
            else
                log.Warning("[MacFirewall] orphan cleanup: pfctl failed (sudoers grant missing?) — if the internet is blocked, run: sudo pfctl -a {Anchor} -F rules; sudo pfctl -f /etc/pf.conf", Anchor);
        }
        catch (Exception ex) { log.Warning(ex, "[MacFirewall] orphan cleanup failed"); }
    }

    /// <summary>
    /// Static entry for app startup / process-exit — mirrors Windows
    /// <c>FirewallManager.TryCleanupOrphanedRulesSafe</c>. Marker-gated, so it's a
    /// no-op unless a prior session was hard-killed while the kill-switch was on.
    /// Never throws.
    /// </summary>
    public static void TryCleanupOrphanedRulesSafe(ILogger? logger)
    {
        try { new MacFirewallManager(logger).CleanupOrphanedRules(logger); } catch { /* never throw from a startup hook */ }
    }

    internal static string? ParsePfToken(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return null;
        // `pfctl -E` prints "Token : 12345678901234" to stderr.
        var m = System.Text.RegularExpressions.Regex.Match(stderr, @"Token\s*:\s*(\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private (bool ok, string stdout, string stderr) RunSudo(string[] args)
    {
        try
        {
            var req = new ProcessRequest("/usr/bin/sudo", args, CaptureStdout: true, CaptureStderr: true);
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var r = _runner.RunAsync(req, cts.Token).GetAwaiter().GetResult();
            if (r.ExitCode != 0)
                _logger.Debug("[MacFirewall] sudo {Args} exited {Code}: {Err}",
                    string.Join(' ', args), r.ExitCode, r.Stderr?.Trim());
            return (r.ExitCode == 0, r.Stdout ?? string.Empty, r.Stderr ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[MacFirewall] sudo run failed");
            return (false, string.Empty, string.Empty);
        }
    }
}
