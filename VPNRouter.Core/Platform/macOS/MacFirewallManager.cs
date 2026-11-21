using System.Text;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Platform.macOS;

public sealed class MacFirewallManager : IFirewallManager, ICommittedFirewallConfig
{
    private const string DefaultPfConf = "/etc/pf.conf";

    internal const string Anchor = "com.vpnrouter/killswitch";

    private readonly object _gate = new();
    private readonly MacPrivilegedClient _privileged;
    private readonly ILogger _logger;
    private readonly string _currentConfigPath;
    private readonly string _pfConfPath;
    private readonly string _rulesPath;
    private readonly string _mainConfPath;
    private readonly Func<string, IReadOnlyList<string>> _resolveHost;

    private bool _armed;
    private bool _loaded;
    private bool _anchorMode;
    private MacFirewallSession? _session;
    private bool _releasePending;
    private bool _releaseUnconfirmed;
    private List<string> _serverIps = new();
    private bool _disposed;

    internal IReadOnlyList<string> ServerIps { get { lock (_gate) { return _serverIps.ToArray(); } } }
    internal bool IsArmed { get { lock (_gate) { return _armed; } } }
    internal bool IsLoaded { get { lock (_gate) { return _loaded; } } }
    internal bool IsAnchorMode { get { lock (_gate) { return _anchorMode; } } }

    public MacFirewallManager(
        ILogger? logger = null,
        IProcessRunner? runner = null,
        string? currentConfigPath = null,
        Func<string, IReadOnlyList<string>>? hostResolver = null,
        string? pfConfPath = null,
        string? rulesPath = null,
        string? mainConfPath = null)
    {
        _logger = logger ?? Log.Logger;
        _privileged = new MacPrivilegedClient(runner ?? new ProcessRunner(), _logger);
        _currentConfigPath = currentConfigPath ?? AppPaths.CurrentConfigPath;
        _pfConfPath = pfConfPath ?? DefaultPfConf;
        _rulesPath = rulesPath ?? System.IO.Path.Combine(AppPaths.DataDir, "vpnrouter-pf-killswitch.conf");
        _mainConfPath = mainConfPath ?? System.IO.Path.Combine(AppPaths.DataDir, "vpnrouter-pf-main.conf");
        _resolveHost = hostResolver ?? DefaultResolveHost;
    }

    public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true)
    {
        lock (_gate)
        {
            var names = (processNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

            if (!isFullTunnel)
            {
                _armed = false;
                _logger.Information(
                    "[MacFirewall] split tunnel ({N} routed app(s)) → pf kill-switch is full-tunnel-only " +
                    "on macOS (per-process blocking impossible with pf) — staying disarmed", names.Count);
                return;
            }

            _serverIps = ReadServerIps();
            _armed = true;
            _logger.Information(
                "[MacFirewall] Armed full-tunnel pf kill-switch (disabled until VPN failure). " +
                "Allow-list: lo0 + RFC1918/link-local + {Count} server IP(s)", _serverIps.Count);
        }
    }

    public void EnableBlockRules()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_armed)
            {
                _logger.Warning(
                    "[MacFirewall] EnableBlockRules: not armed (split tunnel / no block_on_vpn_fail) — " +
                    "NOT blocking; traffic follows normal routing");
                return;
            }
            if (_loaded && !_releaseUnconfirmed) return;

            var rules = BuildRules(_serverIps);
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_rulesPath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                AppPaths.WritePrivateText(_rulesPath, rules);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[MacFirewall] failed to write pf rules file — NOT blocking");
                return;
            }

            if (_session is not { } session)
            {
                _logger.Warning("[MacFirewall] No confirmed runtime session for the kill switch");
                return;
            }

            _releasePending = true;
            if (!_privileged.PfEnable(session))
            {
                _logger.Warning("[MacFirewall] Could not enable pf for the confirmed runtime session");
                ReleasePolicy();
                return;
            }

            _anchorMode = EnsureCarrier(session);
            var applied = _anchorMode
                ? _privileged.PfApplyAnchor(session, rules)
                : _privileged.PfApplyLegacy(session, rules);
            if (applied)
            {
                _loaded = true;
                _releaseUnconfirmed = false;
                _logger.Information("[MacFirewall] pf kill-switch engaged ({Mode})", _anchorMode ? "anchor" : "legacy");
            }
            else
            {
                _logger.Warning("[MacFirewall] Failed to load the pf kill switch");
                ReleasePolicy();
            }
        }
    }

    public void DisableBlockRules() => TryDisableBlockRules();

    internal bool TryDisableBlockRules()
    {
        lock (_gate)
            return ReleasePolicy();
    }

    public void DeleteAllRules()
    {
        lock (_gate)
        {
            _armed = false;
            ReleasePolicy();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _armed = false;
            _disposed = true;
            ReleasePolicy();
        }
    }

    internal void BindToCurrentSession()
    {
        lock (_gate)
        {
            if (_privileged.ReadPfSession() is { } session)
                _session = session;
            else
                _logger.Warning("[MacFirewall] Could not read the confirmed firewall session");
        }
    }

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
                _logger.Warning(ex, "[MacFirewall] Failed to parse committed config JSON — retaining prior server IP list");
                return;
            }

            _armed = true;

            if (!_loaded)
            {
                _serverIps = candidateIps;
                _logger.Information("[MacFirewall] Updated committed server IP cache ({Count} IPs; ruleset not loaded)", _serverIps.Count);
                return;
            }

            var newRules = BuildRules(candidateIps);
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_rulesPath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                AppPaths.WritePrivateText(_rulesPath, newRules);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[MacFirewall] Failed to write pf rules file during refresh — retaining prior configuration");
                return;
            }

            var load = _session is { } session &&
                (!_releaseUnconfirmed || _privileged.PfEnable(session)) &&
                (_anchorMode
                    ? _privileged.PfApplyAnchor(session, newRules)
                    : _privileged.PfApplyLegacy(session, newRules));

            if (load)
            {
                _releaseUnconfirmed = false;
                _serverIps = candidateIps;
                _logger.Information(
                    "[MacFirewall] Refreshed live pf kill-switch ({Mode}) with {Count} server IP(s)",
                    _anchorMode ? "anchor" : "legacy",
                    _serverIps.Count);
            }
            else
            {
                _logger.Warning(
                    "[MacFirewall] Failed to refresh live pf ruleset (privileged helper unavailable) — retaining prior firewall pass-list; live config already committed, cannot rollback");
            }
        }
    }

    private bool EnsureCarrier(MacFirewallSession session)
    {
        if (_privileged.TryGetPfStatus(out var status) &&
            status.Contains(Anchor, StringComparison.Ordinal))
            return true;

        // Keep the merged main ruleset as a user-visible diagnostic. Root reads
        // /etc/pf.conf itself through the helper; this file is never passed to
        // pfctl and is not part of the privilege boundary.
        try
        {
            var conf = System.IO.File.ReadAllText(_pfConfPath);
            var dir = System.IO.Path.GetDirectoryName(_mainConfPath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);
            if (!conf.EndsWith('\n')) conf += "\n";
            AppPaths.WritePrivateText(_mainConfPath, conf + $"anchor \"{Anchor}\"\n");
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[MacFirewall] could not write the merged pf diagnostics file");
        }

        var load = _privileged.PfApplyMain(session);
        if (!load)
            _logger.Warning("[MacFirewall] failed to load main ruleset with anchor carrier through the privileged helper");
        return load;
    }

    private bool ReleasePolicy()
    {
        if (!_releasePending)
            return true;

        if (_session is not { } session || !_privileged.PfRelease(session))
        {
            _releaseUnconfirmed = true;
            _logger.Warning("[MacFirewall] Could not confirm pf policy release");
            return false;
        }

        _loaded = false;
        _releasePending = false;
        _releaseUnconfirmed = false;
        _logger.Information("[MacFirewall] pf kill-switch released");
        return true;
    }

    internal static string BuildRules(List<string> serverIps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("block drop out all");
        sb.AppendLine("pass out quick on lo0 all");
        sb.AppendLine("pass out quick inet from any to 10.0.0.0/8");
        sb.AppendLine("pass out quick inet from any to 172.16.0.0/12");
        sb.AppendLine("pass out quick inet from any to 192.168.0.0/16");
        sb.AppendLine("pass out quick inet from any to 169.254.0.0/16");
        sb.AppendLine("pass out quick inet from any to 100.64.0.0/10");
        sb.AppendLine("pass out quick inet6 from any to fe80::/10");
        sb.AppendLine("pass out quick inet6 from any to fc00::/7");
        foreach (var ip in serverIps)
        {
            var family = ip.Contains(':') ? "inet6" : "inet";
            sb.AppendLine($"pass out quick {family} from any to {ip}");
        }
        return sb.ToString();
    }

    internal List<string> ParseServerIps(string configJson) =>
        UnixFirewallServerIps.Parse(configJson, _resolveHost, _logger, "MacFirewall");

    internal List<string> ReadServerIps()
    {
        try
        {
            if (!System.IO.File.Exists(_currentConfigPath)) return new List<string>();
            return ParseServerIps(System.IO.File.ReadAllText(_currentConfigPath));
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[MacFirewall] could not read server IPs from {Path}", _currentConfigPath);
            return new List<string>();
        }
    }

    private IReadOnlyList<string> DefaultResolveHost(string host) =>
        UnixFirewallServerIps.ResolveHost(host, _logger, "MacFirewall");

    internal void RecoverInactivePolicy()
    {
        lock (_gate)
        {
            if (_privileged.PfRecover())
            {
                _loaded = false;
                _releasePending = false;
                _releaseUnconfirmed = false;
            }
            else
                _logger.Warning("[MacFirewall] Could not recover inactive pf policy");
        }
    }

    public static void TryCleanupOrphanedRulesSafe(ILogger? logger)
    {
        try { new MacFirewallManager(logger).RecoverInactivePolicy(); }
        catch (Exception ex) { logger?.Warning(ex, "[MacFirewall] Firewall session recovery failed"); }
    }
}
