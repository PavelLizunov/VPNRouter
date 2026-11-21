using Serilog;
using VPNRouter.Core.Platform.Unix;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Platform.macOS;

public sealed class MacDnsHardening : IUnixDnsHardening
{
    private readonly IProcessRunner _runner;

    public MacDnsHardening(IProcessRunner? runner = null)
    {
        _runner = runner ?? new ProcessRunner();
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

            var helper = new MacPrivilegedClient(_runner, logger);
            if (helper.DnsApply(service, dnsTarget))
            {
                helper.DnsFlush();
                logger?.Information("[MacDnsHardening] Pinned {Service} DNS -> {Target}", service, dnsTarget);
            }
            else
            {
                logger?.Warning("[MacDnsHardening] Could not apply DNS policy to {Service}", service);
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[MacDnsHardening] Apply failed (non-fatal — VPN still routes)");
        }
    }

    public void Restore(ILogger? logger)
    {
        if (!new MacPrivilegedClient(_runner, logger).DnsRestore())
            logger?.Warning("[MacDnsHardening] The helper could not restore DNS");
    }

    public void RestoreStrandedIfAny(ILogger? logger)
    {
        if (!new MacPrivilegedClient(_runner, logger).DnsRestore())
            logger?.Debug("[MacDnsHardening] The helper could not check for stranded DNS");
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

    private string Run(string exe, string[] args, ILogger? logger)
    {
        try
        {
            var req = new ProcessRequest(exe, args, CaptureStdout: true, CaptureStderr: true);
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = _runner.RunAsync(req, cts.Token).GetAwaiter().GetResult();
            if (result.ExitCode != 0)
                logger?.Debug("[MacDnsHardening] {Exe} exited {Code}: {Err}", exe, result.ExitCode, result.Stderr?.Trim());
            return result.ExitCode == 0 ? result.Stdout ?? string.Empty : string.Empty;
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[MacDnsHardening] {Exe} failed to run", exe);
            return string.Empty;
        }
    }
}
