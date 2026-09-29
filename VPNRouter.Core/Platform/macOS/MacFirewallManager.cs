using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Platform.macOS;

public sealed class MacFirewallManager : IFirewallManager, ICommittedFirewallConfig
{
    private const string DefaultPfConf = "/etc/pf.conf";
    private const string PfCtl = "/sbin/pfctl";

    internal const string Anchor = "com.vpnrouter/killswitch";
    internal const string AnchorMarker = "anchor-v1";
    internal const string LegacyMarker = "engaged";

    private readonly object _gate = new();
    private readonly IProcessRunner _runner;
    private readonly ILogger _logger;
    private readonly string _currentConfigPath;
    private readonly string _markerPath;
    private readonly string _pfConfPath;
    private readonly string _rulesPath;
    private readonly string _mainConfPath;
    private readonly Func<string, IReadOnlyList<string>> _resolveHost;

    private bool _armed;
    private bool _loaded;
    private bool _anchorMode;
    private string? _enableToken;
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
        string? markerPath = null,
        Func<string, IReadOnlyList<string>>? hostResolver = null,
        string? pfConfPath = null,
        string? rulesPath = null,
        string? mainConfPath = null)
    {
        _logger = logger ?? Log.Logger;
        _runner = runner ?? new ProcessRunner();
        _currentConfigPath = currentConfigPath ?? AppPaths.CurrentConfigPath;
        _markerPath = markerPath ?? System.IO.Path.Combine(AppPaths.DataDir, "pf-killswitch-engaged.marker");
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
            if (_loaded) return;

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

            if (string.IsNullOrEmpty(_enableToken))
            {
                var en = RunSudo(new[] { "-n", PfCtl, "-E" });
                if (en.ok) _enableToken = ParsePfToken(en.stderr);
            }

            if (EnsureCarrier())
            {
                var load = RunSudo(new[] { "-n", PfCtl, "-a", Anchor, "-f", _rulesPath });
                if (load.ok)
                {
                    _loaded = true;
                    _anchorMode = true;
                    WriteMarker(AnchorMarker);
                    _logger.Information(
                        "[MacFirewall] pf kill-switch ENGAGED (anchor {Anchor}) — blocking all egress except lo0/LAN/server", Anchor);
                    return;
                }
                _logger.Warning(
                    "[MacFirewall] FAILED to load anchor ruleset (pfctl sudoers grant missing or malformed rule " +
                    "(wrong inet/inet6 family)? {Err}) — NOT blocking; releasing pf-enable ref", load.stderr?.Trim());
                ReleaseEnable();
                return;
            }

            _logger.Warning("[MacFirewall] anchor carrier unavailable — falling back to legacy broad pf load");
            var legacy = RunSudo(new[] { "-n", PfCtl, "-f", _rulesPath });
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
                    "[MacFirewall] FAILED to load pf ruleset (pfctl sudoers grant missing or malformed rule " +
                    "(wrong inet/inet6 family)? {Err}) — NOT blocking; releasing pf-enable ref", legacy.stderr?.Trim());
                ReleaseEnable();
            }
        }
    }

    public void DisableBlockRules()
    {
        lock (_gate)
        {
            if (!_loaded && string.IsNullOrEmpty(_enableToken)) return;

            if (_loaded)
            {
                var rulesCleared = _anchorMode ? FlushAnchor() : RestoreDefaultRuleset();
                if (!rulesCleared)
                {
                    _logger.Warning(
                        "[MacFirewall] failed to lift pf kill-switch ({Mode}) — retaining rules loaded state and marker for retry",
                        _anchorMode ? "anchor flush" : "default ruleset restore");
                    return;
                }

                _loaded = false;
                TryDeleteMarker();
                _logger.Information("[MacFirewall] pf kill-switch lifted ({Mode})",
                    _anchorMode ? "anchor flushed" : "default ruleset restored");
            }

            ReleaseEnable();
        }
    }

    public void DeleteAllRules()
    {
        lock (_gate)
        {
            bool isLegacy;
            if (_loaded)
            {
                isLegacy = !_anchorMode;
            }
            else
            {
                var markerState = InspectMarker();
                switch (markerState)
                {
                    case MarkerState.Legacy:
                        isLegacy = true;
                        break;
                    case MarkerState.Anchor:
                    case MarkerState.Missing:
                        isLegacy = false;
                        break;
                    case MarkerState.Unknown:
                    default:
                        _logger.Warning(
                            "[MacFirewall] DeleteAllRules: unreadable or unknown kill-switch marker found ({Path}) — retaining marker without broad restore or flush-as-success",
                            _markerPath);
                        return;
                }
            }

            var rulesCleared = isLegacy ? RestoreDefaultRuleset() : FlushAnchor();
            if (rulesCleared)
            {
                _loaded = false;
                TryDeleteMarker();
                _armed = false;
            }
            else
            {
                _logger.Warning(
                    "[MacFirewall] DeleteAllRules: failed to clear rules ({Mode}) — retaining state and marker for retry",
                    isLegacy ? "default ruleset restore" : "anchor flush");
                return;
            }

            ReleaseEnable();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed && !_loaded && string.IsNullOrEmpty(_enableToken)) return;

            try
            {
                if (_loaded)
                {
                    var rulesCleared = _anchorMode ? FlushAnchor() : RestoreDefaultRuleset();
                    if (rulesCleared)
                    {
                        _loaded = false;
                        TryDeleteMarker();
                    }
                }

                if (!_loaded)
                {
                    ReleaseEnable();
                }
            }
            catch { }

            if (!_loaded && string.IsNullOrEmpty(_enableToken))
            {
                _disposed = true;
            }
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

            var load = _anchorMode
                ? RunSudo(new[] { "-n", PfCtl, "-a", Anchor, "-f", _rulesPath })
                : RunSudo(new[] { "-n", PfCtl, "-f", _rulesPath });

            if (load.ok)
            {
                _serverIps = candidateIps;
                _logger.Information(
                    "[MacFirewall] Refreshed live pf kill-switch ({Mode}) with {Count} server IP(s)",
                    _anchorMode ? "anchor" : "legacy",
                    _serverIps.Count);
            }
            else
            {
                _logger.Warning(
                    "[MacFirewall] Failed to refresh live pf ruleset ({Err}) — retaining prior firewall pass-list; live config already committed, cannot rollback",
                    load.stderr?.Trim());
            }
        }
    }

    private bool RestoreDefaultRuleset()
        => RunSudo(new[] { "-n", PfCtl, "-f", DefaultPfConf }).ok;

    private bool FlushAnchor()
        => RunSudo(new[] { "-n", PfCtl, "-a", Anchor, "-F", "rules" }).ok;

    private bool EnsureCarrier()
    {
        var sr = RunSudo(new[] { "-n", PfCtl, "-sr" });
        if (sr.ok && sr.stdout.Contains(Anchor, StringComparison.Ordinal))
            return true;

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

        try
        {
            var dir = System.IO.Path.GetDirectoryName(_mainConfPath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);
            if (!conf.EndsWith('\n')) conf += "\n";
            AppPaths.WritePrivateText(_mainConfPath, conf + $"anchor \"{Anchor}\"\n");

            var load = RunSudo(new[] { "-n", PfCtl, "-f", _mainConfPath });
            if (!load.ok)
                _logger.Warning("[MacFirewall] failed to load main ruleset with anchor carrier: {Err}", load.stderr?.Trim());
            return load.ok;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[MacFirewall] failed to write merged pf main ruleset");
            return false;
        }
    }

    private bool ReleaseEnable()
    {
        if (string.IsNullOrEmpty(_enableToken)) return true;
        var r = RunSudo(new[] { "-n", PfCtl, "-X", _enableToken });
        if (r.ok)
        {
            _enableToken = null;
            return true;
        }
        _logger.Warning("[MacFirewall] failed to release pf enable token {Token} — retaining token for retry", _enableToken);
        return false;
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

            try
            {
                var resolved = _resolveHost(candidate);
                if (resolved != null)
                {
                    foreach (var rip in resolved)
                    {
                        if (string.IsNullOrWhiteSpace(rip)) continue;
                        var ripTrimmed = rip.Trim();
                        if (IPAddress.TryParse(ripTrimmed, out var resolvedIp))
                        {
                            var canonical = resolvedIp.ToString();
                            if (seen.Add(canonical))
                            {
                                ips.Add(canonical);
                            }
                        }
                        else
                        {
                            _logger.Debug("[MacFirewall] ignored invalid resolver literal for {Host}", candidate);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[MacFirewall] could not resolve server hostname {Host} — kill-switch reconnect may need manual cleanup", candidate);
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
                if (!ep.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String) continue;

                var endpointType = typeProp.GetString();
                if (!string.Equals(endpointType, "wireguard", StringComparison.OrdinalIgnoreCase)) continue;

                if (!ep.TryGetProperty("peers", out var peersProp) || peersProp.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var peer in peersProp.EnumerateArray())
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
            _logger.Debug(ex, "[MacFirewall] could not read server IPs from {Path}", _currentConfigPath);
            return new List<string>();
        }
    }

    private IReadOnlyList<string> DefaultResolveHost(string host)
    {
        try
        {
            var task = Dns.GetHostAddressesAsync(host);
            if (!task.Wait(TimeSpan.FromSeconds(3)))
            {
                _logger.Warning("[MacFirewall] DNS resolve of {Host} timed out — kill-switch reconnect may need manual cleanup", host);
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
            _logger.Warning(ex, "[MacFirewall] could not resolve server hostname {Host} — kill-switch reconnect may need manual cleanup", host);
            return Array.Empty<string>();
        }
    }

    private void WriteMarker(string mode)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_markerPath)!);
            System.IO.File.WriteAllText(_markerPath, mode);
        }
        catch { }
    }

    private void TryDeleteMarker()
    {
        try { if (System.IO.File.Exists(_markerPath)) System.IO.File.Delete(_markerPath); }
        catch { }
    }

    internal enum MarkerState
    {
        Missing,
        Anchor,
        Legacy,
        Unknown
    }

    internal MarkerState InspectMarker(ILogger? logger = null)
    {
        var log = logger ?? _logger;
        try
        {
            if (!System.IO.File.Exists(_markerPath))
                return MarkerState.Missing;

            var content = System.IO.File.ReadAllText(_markerPath).Trim();
            if (content == AnchorMarker)
                return MarkerState.Anchor;
            if (content == LegacyMarker)
                return MarkerState.Legacy;

            log.Warning(
                "[MacFirewall] unknown kill-switch marker at {Path}",
                _markerPath);
            return MarkerState.Unknown;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MacFirewall] failed to read kill-switch marker at {Path}", _markerPath);
            return MarkerState.Unknown;
        }
    }

    // If the engaged marker survived a hard kill while the kill switch was live, unblock the network on the next start.
    internal void CleanupOrphanedRules(ILogger? logger)
    {
        lock (_gate)
        {
            var log = logger ?? _logger;
            try
            {
                var markerState = InspectMarker(log);
                switch (markerState)
                {
                    case MarkerState.Missing:
                        return;

                    case MarkerState.Anchor:
                    {
                        log.Warning("[MacFirewall] engaged kill-switch marker (anchor-v1) from a prior session found (hard kill?) — flushing anchor {Anchor}", Anchor);
                        var ok = FlushAnchor();
                        if (ok)
                        {
                            TryDeleteMarker();
                            log.Information("[MacFirewall] orphan cleanup: egress unblocked (a lost pfctl -E token cannot be released by a new process)");
                        }
                        else
                        {
                            log.Warning("[MacFirewall] orphan cleanup: pfctl failed (sudoers grant missing?) — if the internet is blocked, run: sudo pfctl -a {Anchor} -F rules; sudo pfctl -f /etc/pf.conf", Anchor);
                        }
                        break;
                    }

                    case MarkerState.Legacy:
                    {
                        log.Warning("[MacFirewall] engaged kill-switch marker (legacy) from a prior session found (hard kill?) — restoring default pf ruleset");
                        var ok = RestoreDefaultRuleset();
                        if (ok)
                        {
                            TryDeleteMarker();
                            log.Information("[MacFirewall] orphan cleanup: egress unblocked (a lost pfctl -E token cannot be released by a new process)");
                        }
                        else
                        {
                            log.Warning("[MacFirewall] orphan cleanup: pfctl failed (sudoers grant missing?) — if the internet is blocked, run: sudo pfctl -a {Anchor} -F rules; sudo pfctl -f /etc/pf.conf", Anchor);
                        }
                        break;
                    }

                    case MarkerState.Unknown:
                    default:
                        log.Warning("[MacFirewall] orphan cleanup: kill-switch marker at {Path} is unreadable or has unknown content — retaining marker without broad pf restore or flush-as-success", _markerPath);
                        break;
                }
            }
            catch (Exception ex) { log.Warning(ex, "[MacFirewall] orphan cleanup failed"); }
        }
    }

    public static void TryCleanupOrphanedRulesSafe(ILogger? logger)
    {
        try { new MacFirewallManager(logger).CleanupOrphanedRules(logger); } catch { }
    }

    internal static string? ParsePfToken(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return null;
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
            var ok = r.ExitCode == 0 && !r.TimedOut;
            if (!ok)
                _logger.Debug("[MacFirewall] sudo {Args} failed (exit {Code}, timedOut {TimedOut}): {Err}",
                    string.Join(' ', args), r.ExitCode, r.TimedOut, r.Stderr?.Trim());
            return (ok, r.Stdout ?? string.Empty, r.Stderr ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[MacFirewall] sudo run failed");
            return (false, string.Empty, string.Empty);
        }
    }
}
