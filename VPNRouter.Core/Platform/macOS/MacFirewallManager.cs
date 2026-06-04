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
/// block_on_vpn_fail). Fail-safe: Disable / Delete / Dispose ALWAYS try to
/// restore the default ruleset + release our pf-enable ref — never leave the Mac
/// blocked.</para>
/// </summary>
public sealed class MacFirewallManager : IFirewallManager
{
    private const string DefaultPfConf = "/etc/pf.conf";
    private const string PfCtl = "/sbin/pfctl";

    private readonly IProcessRunner _runner;
    private readonly ILogger _logger;
    private readonly string _currentConfigPath;

    private bool _armed;            // full-tunnel detected at CreateBlockRules
    private bool _loaded;           // our blocking ruleset is live
    private string? _enableToken;   // pfctl -E ref-count token
    private List<string> _serverIps = new();
    private bool _disposed;

    public MacFirewallManager(ILogger? logger = null, IProcessRunner? runner = null, string? currentConfigPath = null)
    {
        _logger = logger ?? Log.Logger;
        _runner = runner ?? new ProcessRunner();
        _currentConfigPath = currentConfigPath ?? AppPaths.CurrentConfigPath;
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

        var load = RunSudo(new[] { "-n", PfCtl, "-f", tmp });
        if (load.ok)
        {
            _loaded = true;
            _logger.Information("[MacFirewall] pf kill-switch ENGAGED — blocking all egress except lo0/LAN/server");
        }
        else
        {
            _logger.Warning(
                "[MacFirewall] FAILED to load pf ruleset (pfctl sudoers grant missing? {Err}) — NOT blocking; " +
                "releasing pf-enable ref", load.stderr?.Trim());
            ReleaseEnable(); // don't leave pf enabled-by-us with no blocking ruleset
        }
    }

    /// <inheritdoc />
    public void DisableBlockRules()
    {
        if (!_loaded) return;
        RestoreDefaultRuleset();
        ReleaseEnable();
        _loaded = false;
        _logger.Information("[MacFirewall] pf kill-switch lifted (default ruleset restored)");
    }

    /// <inheritdoc />
    public void DeleteAllRules()
    {
        // Fail-safe full teardown regardless of tracked state — used on clean
        // shutdown and orphan cleanup.
        RestoreDefaultRuleset();
        ReleaseEnable();
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
            try { RestoreDefaultRuleset(); ReleaseEnable(); } catch { /* never throw from Dispose */ }
            _loaded = false;
        }
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private void RestoreDefaultRuleset()
        => RunSudo(new[] { "-n", PfCtl, "-f", DefaultPfConf }); // reload stock macOS ruleset

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
    /// </summary>
    internal static string BuildRules(List<string> serverIps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("set block-policy drop");
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
                        // pf rules take literal IPs only — a hostname can't be
                        // passed, so skip it (rare for Reality, which uses IPs).
                        if (!string.IsNullOrWhiteSpace(s) &&
                            System.Net.IPAddress.TryParse(s, out _) &&
                            !ips.Contains(s!))
                        {
                            ips.Add(s!);
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
