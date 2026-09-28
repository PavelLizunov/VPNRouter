using System.Text.Json;
using Serilog;
using VPNRouter.Core.Platform.Unix;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Platform.Linux;

public sealed class LinuxDnsHardening : IUnixDnsHardening
{
    private readonly IProcessRunner _runner;
    private readonly string _statePath;

    private const string Resolvectl = "resolvectl";
    private const string Ip = "ip";

    private const string DefaultRoutingDomain = "~.";

    public LinuxDnsHardening(IProcessRunner? runner = null, string? statePath = null)
    {
        _runner = runner ?? new ProcessRunner();
        _statePath = statePath ?? System.IO.Path.Combine(AppPaths.DataDir, "linux-dns-hardening-state.json");
    }

    public void Apply(string dnsTarget, ILogger? logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dnsTarget))
            {
                logger?.Warning("[LinuxDnsHardening] Apply: empty DNS target, skipping");
                return;
            }

            if (!ResolvectlAvailable(logger))
            {
                logger?.Information(
                    "[LinuxDnsHardening] Apply: resolvectl/systemd-resolved unavailable — " +
                    "DNS not hardened (VPN still routes)");
                return;
            }

            var iface = GetTunInterface(dnsTarget, logger);
            if (iface == null)
            {
                logger?.Warning(
                    "[LinuxDnsHardening] Apply: could not resolve the TUN interface for {Target} " +
                    "(ip route get returned no device) — skipping", dnsTarget);
                return;
            }

            if (!System.IO.File.Exists(_statePath))
                SaveState(new LinuxDnsState { Interface = iface });

            var dnsOk = RunResolvectl(new[] { "dns", iface, dnsTarget }, logger);
            var domainOk = RunResolvectl(new[] { "domain", iface, DefaultRoutingDomain }, logger);
            if (dnsOk && domainOk)
            {
                FlushDnsCache(logger);
                logger?.Information(
                    "[LinuxDnsHardening] Pinned {Iface} DNS -> {Target} (routing-domain {Domain})",
                    iface, dnsTarget, DefaultRoutingDomain);
            }
            else
            {
                logger?.Warning(
                    "[LinuxDnsHardening] FAILED to pin {Iface} DNS -> {Target} (resolvectl non-zero — " +
                    "polkit/CAP_NET_ADMIN missing? DNS is NOT hardened and may leak)", iface, dnsTarget);
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[LinuxDnsHardening] Apply failed (non-fatal — VPN still routes)");
        }
    }

    public void Restore(ILogger? logger) => RestoreInternal(logger, "Restore");

    public void RestoreStrandedIfAny(ILogger? logger)
    {
        if (System.IO.File.Exists(_statePath))
        {
            logger?.Information("[LinuxDnsHardening] Found stranded DNS state from a prior session — healing");
            RestoreInternal(logger, "RestoreStranded");
        }
    }

    private void RestoreInternal(ILogger? logger, string context)
    {
        try
        {
            if (!System.IO.File.Exists(_statePath))
                return;

            var state = LoadState();
            if (state == null || string.IsNullOrWhiteSpace(state.Interface))
            {
                TryDeleteState();
                return;
            }

            if (RunResolvectl(new[] { "revert", state.Interface }, logger))
            {
                FlushDnsCache(logger);
                logger?.Information("[LinuxDnsHardening] {Context}: reverted {Iface} DNS", context, state.Interface);
            }
            else
            {
                logger?.Information(
                    "[LinuxDnsHardening] {Context}: resolvectl revert {Iface} non-zero " +
                    "(link likely already gone — resolved auto-dropped per-link config)",
                    context, state.Interface);
            }
            TryDeleteState();
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[LinuxDnsHardening] {Context} failed (non-fatal)", context);
        }
    }

    private bool ResolvectlAvailable(ILogger? logger)
        => RunResult(Resolvectl, new[] { "--version" }, logger).ok;

    private string? GetTunInterface(string dnsTarget, ILogger? logger)
    {
        var stdout = Run(Ip, new[] { "-o", "route", "get", dnsTarget }, logger);
        return ParseRouteGetDevice(stdout);
    }

    private bool RunResolvectl(string[] args, ILogger? logger)
        => RunResult(Resolvectl, args, logger).ok;

    private void FlushDnsCache(ILogger? logger)
        => RunResolvectl(new[] { "flush-caches" }, logger);

    private string Run(string exe, string[] args, ILogger? logger)
        => RunResult(exe, args, logger).stdout;

    private (bool ok, string stdout) RunResult(string exe, string[] args, ILogger? logger)
    {
        try
        {
            var req = new ProcessRequest(exe, args, CaptureStdout: true, CaptureStderr: true);
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = _runner.RunAsync(req, cts.Token).GetAwaiter().GetResult();
            if (result.ExitCode != 0)
                logger?.Debug("[LinuxDnsHardening] {Exe} exited {Code}: {Err}", exe, result.ExitCode, result.Stderr?.Trim());
            return (result.ExitCode == 0, result.Stdout ?? string.Empty);
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[LinuxDnsHardening] {Exe} failed to run", exe);
            return (false, string.Empty);
        }
    }

    internal static string? ParseRouteGetDevice(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;
        var tokens = stdout.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < tokens.Length; i++)
            if (tokens[i] == "dev")
                return tokens[i + 1];
        return null;
    }

    private void SaveState(LinuxDnsState state)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_statePath)!);
            System.IO.File.WriteAllText(_statePath, JsonSerializer.Serialize(state));
        }
        catch { }
    }

    private LinuxDnsState? LoadState()
    {
        try { return JsonSerializer.Deserialize<LinuxDnsState>(System.IO.File.ReadAllText(_statePath)); }
        catch { return null; }
    }

    private void TryDeleteState()
    {
        try { if (System.IO.File.Exists(_statePath)) System.IO.File.Delete(_statePath); }
        catch { }
    }

    internal sealed class LinuxDnsState
    {
        public string Interface { get; set; } = string.Empty;
    }
}
