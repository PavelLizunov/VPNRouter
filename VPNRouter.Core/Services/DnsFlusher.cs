#nullable enable
using System.Diagnostics;
using Serilog;

namespace VPNRouter.Core.Services;

public sealed class DnsFlusher
{
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromMilliseconds(5000);

    private readonly IProcessRunner _runner;
    private readonly Func<bool>? _nativeFlusher;

    private static readonly DnsFlusher DefaultInstance = new(new ProcessRunner());

    public DnsFlusher(IProcessRunner? runner = null, Func<bool>? nativeFlusher = null)
    {
        _runner = runner ?? new ProcessRunner();
        _nativeFlusher = nativeFlusher;
    }

    public bool FlushInstance(ILogger? logger = null)
    {
        var log = logger ?? Log.Logger;

        try
        {
            if (OperatingSystem.IsWindows())
                return FlushWindows(log);
            if (OperatingSystem.IsMacOS())
                return FlushMac(log);

            log.Debug("[DnsFlusher] Platform not supported — skipping DNS flush");
            return false;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[DnsFlusher] DNS flush failed (non-critical)");
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    private static extern bool NativeDnsFlush();

    private bool FlushWindows(ILogger log)
    {
        if (_nativeFlusher != null || _runner is ProcessRunner)
        {
            try
            {
                var ok = _nativeFlusher != null ? _nativeFlusher() : (OperatingSystem.IsWindows() && NativeDnsFlush());
                if (ok)
                {
                    log.Information("[DnsFlusher] Windows DNS cache flushed via DnsFlushResolverCache");
                    return true;
                }
            }
            catch (Exception ex)
            {
                log.Debug(ex, "[DnsFlusher] Native DnsFlushResolverCache threw — falling back to ipconfig");
            }
        }

        var request = new ProcessRequest(
            ExecutablePath: "ipconfig.exe",
            Arguments: new[] { "/flushdns" },
            Timeout: FlushTimeout);

        try
        {
            var result = _runner.RunAsync(request).GetAwaiter().GetResult();

            if (result.TimedOut)
            {
                log.Warning("[DnsFlusher] ipconfig /flushdns timed out after {Timeout}", FlushTimeout);
                return false;
            }
            if (result.ExitCode == 0)
            {
                log.Information("[DnsFlusher] Windows DNS cache flushed");
                return true;
            }
            log.Warning("[DnsFlusher] ipconfig /flushdns returned exit code {Code}", result.ExitCode);
            return false;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[DnsFlusher] ipconfig.exe failed");
            return false;
        }
    }

    private bool FlushMac(ILogger log)
    {
        var anyOk = false;

        try
        {
            var r1 = _runner.RunAsync(new ProcessRequest(
                ExecutablePath: "/usr/bin/dscacheutil",
                Arguments: new[] { "-flushcache" },
                Timeout: FlushTimeout)).GetAwaiter().GetResult();
            if (r1.ExitCode == 0 && !r1.TimedOut) anyOk = true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[DnsFlusher] dscacheutil failed");
        }

        try
        {
            _ = _runner.RunAsync(new ProcessRequest(
                ExecutablePath: "/usr/bin/sudo",
                Arguments: new[] { "-n", "killall", "-HUP", "mDNSResponder" },
                Timeout: FlushTimeout)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[DnsFlusher] mDNSResponder restart failed (sudo not configured for killall)");
        }

        log.Information("[DnsFlusher] macOS DNS cache flush attempted");
        return anyOk;
    }

    public static void Flush(ILogger? logger = null) => DefaultInstance.FlushInstance(logger);
}
