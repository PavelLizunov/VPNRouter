using System.Text.Json;
using Serilog;
using VPNRouter.Core.Platform.Unix;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Platform.macOS;

public sealed class MacDnsHardening : IUnixDnsHardening
{
    private readonly IProcessRunner _runner;
    private readonly string _statePath;

    private const string DhcpToken = "empty";

    public MacDnsHardening(IProcessRunner? runner = null, string? statePath = null)
    {
        _runner = runner ?? new ProcessRunner();
        _statePath = statePath ?? System.IO.Path.Combine(AppPaths.DataDir, "dns-hardening-state.json");
    }

    public void Apply(string dnsTarget, ILogger? logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dnsTarget))
            {
                logger?.Warning("[MacDnsHardening] Apply: empty DNS target, skipping");
                return;
            }

            var device = GetDefaultRouteDevice(logger);
            if (device == null)
            {
                logger?.Information("[MacDnsHardening] Apply: no default route (offline?), skipping");
                return;
            }

            var service = GetServiceForDevice(device, logger);
            if (service == null)
            {
                logger?.Warning("[MacDnsHardening] Apply: no network service maps to device {Device}", device);
                return;
            }

            if (!System.IO.File.Exists(_statePath))
            {
                var original = GetDnsServers(service, logger);
                SaveState(new MacDnsState { Service = service, OriginalServers = original });
            }

            // Claim success only if networksetup applied the change; keep the saved state either way so Restore still runs.
            if (SetDnsServers(service, new[] { dnsTarget }, logger))
            {
                FlushDnsCache(logger);
                logger?.Information("[MacDnsHardening] Pinned {Service} DNS -> {Target}", service, dnsTarget);
            }
            else
            {
                logger?.Warning(
                    "[MacDnsHardening] FAILED to pin {Service} DNS -> {Target} (networksetup non-zero — " +
                    "sudoers grant missing? DNS is NOT hardened and may leak)", service, dnsTarget);
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[MacDnsHardening] Apply failed (non-fatal — VPN still routes)");
        }
    }

    public void Restore(ILogger? logger) => RestoreInternal(logger, "Restore");

    public void RestoreStrandedIfAny(ILogger? logger)
    {
        if (System.IO.File.Exists(_statePath))
        {
            logger?.Information("[MacDnsHardening] Found stranded DNS state from a prior session — healing");
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
            if (state == null || string.IsNullOrWhiteSpace(state.Service))
            {
                TryDeleteState();
                return;
            }

            var servers = state.OriginalServers.Count > 0
                ? state.OriginalServers.ToArray()
                : new[] { DhcpToken };

            if (SetDnsServers(state.Service, servers, logger))
            {
                FlushDnsCache(logger);
                TryDeleteState();
                logger?.Information("[MacDnsHardening] {Context}: restored {Service} DNS", context, state.Service);
            }
            else
            {
                logger?.Warning(
                    "[MacDnsHardening] {Context}: FAILED to restore {Service} DNS — keeping state for retry " +
                    "next launch. Manual recovery: sudo networksetup -setdnsservers <service> empty",
                    context, state.Service);
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[MacDnsHardening] {Context} failed (non-fatal)", context);
        }
    }

    private string? GetDefaultRouteDevice(ILogger? logger)
    {
        var stdout = Run("/sbin/route", new[] { "-n", "get", "default" }, logger);
        return MacDnsParsers.ParseDefaultRouteDevice(stdout);
    }

    private string? GetServiceForDevice(string device, ILogger? logger)
    {
        var stdout = Run("/usr/sbin/networksetup", new[] { "-listnetworkserviceorder" }, logger);
        return MacDnsParsers.ParseServiceForDevice(stdout, device);
    }

    private List<string> GetDnsServers(string service, ILogger? logger)
    {
        var stdout = Run("/usr/sbin/networksetup", new[] { "-getdnsservers", service }, logger);
        return MacDnsParsers.ParseGetDnsServers(stdout);
    }

    private bool SetDnsServers(string service, string[] servers, ILogger? logger)
    {
        var args = new List<string> { "-n", "/usr/sbin/networksetup", "-setdnsservers", service };
        args.AddRange(servers);
        return RunSudoChecked(args, logger);
    }

    private void FlushDnsCache(ILogger? logger)
    {
        RunSudoChecked(new[] { "-n", "/usr/bin/dscacheutil", "-flushcache" }, logger);
        RunSudoChecked(new[] { "-n", "/usr/bin/killall", "-HUP", "mDNSResponder" }, logger);
    }

    private bool RunSudoChecked(IEnumerable<string> sudoArgs, ILogger? logger)
        => RunResult("/usr/bin/sudo", sudoArgs.ToArray(), logger).ok;

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
                logger?.Debug("[MacDnsHardening] {Exe} exited {Code}: {Err}", exe, result.ExitCode, result.Stderr?.Trim());
            return (result.ExitCode == 0, result.Stdout ?? string.Empty);
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[MacDnsHardening] {Exe} failed to run", exe);
            return (false, string.Empty);
        }
    }

    private void SaveState(MacDnsState state)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_statePath)!);
            System.IO.File.WriteAllText(_statePath, JsonSerializer.Serialize(state));
        }
        catch { }
    }

    private MacDnsState? LoadState()
    {
        try { return JsonSerializer.Deserialize<MacDnsState>(System.IO.File.ReadAllText(_statePath)); }
        catch { return null; }
    }

    private void TryDeleteState()
    {
        try { if (System.IO.File.Exists(_statePath)) System.IO.File.Delete(_statePath); }
        catch { }
    }

    internal sealed class MacDnsState
    {
        public string Service { get; set; } = string.Empty;
        public List<string> OriginalServers { get; set; } = new();
    }
}
