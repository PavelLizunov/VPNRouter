#nullable enable
using System.Diagnostics;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Flushes the system DNS cache before VPN starts.
///
/// Why: Without flushing, DNS entries resolved BEFORE the VPN was started
/// remain in the system cache. When apps try to connect, they use the cached
/// IP — which was resolved through the direct route, possibly leaking to
/// non-VPN DNS servers and revealing what you intend to access.
///
/// Platforms:
///   - Windows: ipconfig /flushdns
///   - macOS:   sudo dscacheutil -flushcache &amp;&amp; sudo killall -HUP mDNSResponder
///   - Linux:   not implemented (varies by resolver)
///
/// <para>
/// v3.0 Phase 2G refactor: extracted into an instance class taking an
/// <see cref="IProcessRunner"/> via ctor for testability. Static
/// <see cref="Flush"/> facade preserved so existing call sites
/// (<c>VpnEngine</c>) continue to work without modification. Tests
/// construct <see cref="DnsFlusher"/> with <c>FakeProcessRunner</c>.
/// </para>
/// </summary>
public sealed class DnsFlusher
{
    /// <summary>How long to wait for a single shell-out before giving up.</summary>
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromMilliseconds(5000);

    private readonly IProcessRunner _runner;
    private readonly Func<bool>? _nativeFlusher;

    /// <summary>
    /// Default singleton wired to <see cref="ProcessRunner"/>. Used by the
    /// static facade so existing call sites do not need to change.
    /// </summary>
    private static readonly DnsFlusher DefaultInstance = new(new ProcessRunner());

    /// <summary>
    /// Construct a <see cref="DnsFlusher"/> backed by the supplied
    /// <see cref="IProcessRunner"/>. Tests inject <c>FakeProcessRunner</c>;
    /// production code typically uses the static <see cref="Flush"/>
    /// facade which dispatches to <see cref="DefaultInstance"/>.
    /// </summary>
    public DnsFlusher(IProcessRunner? runner = null, Func<bool>? nativeFlusher = null)
    {
        _runner = runner ?? new ProcessRunner();
        _nativeFlusher = nativeFlusher;
    }

    /// <summary>
    /// Instance variant of the platform-dispatching DNS flush. Returns
    /// true if the OS-appropriate flush completed with exit code 0,
    /// false otherwise. Never throws — failure mode is silent stale
    /// cache, not crash.
    /// </summary>
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
        // When running in production (or when an explicit native flusher delegate is injected),
        // try in-process Win32 DnsFlushResolverCache first (< 0.5 ms latency, 0 child processes).
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
            // .GetAwaiter().GetResult() is safe here: VPN start path is
            // single-threaded and the timeout caps the wait at 5s.
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
        // Both commands needed: dscacheutil for system cache, killall for mDNSResponder cache
        // sudo with NOPASSWD requires sudoers entries — but flushing DNS doesn't need root.
        // dscacheutil -flushcache works without sudo.
        // killall -HUP mDNSResponder DOES need sudo, but if it fails we still flushed dscacheutil.

        var anyOk = false;

        // dscacheutil (no sudo needed)
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

        // mDNSResponder restart — needs sudo, may silently fail
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

    // ── Static facade (backwards compatibility) ──

    /// <summary>
    /// Static entry-point preserved for existing call sites (VpnEngine).
    /// Dispatches to <see cref="DefaultInstance"/> which is wired to the
    /// real <see cref="ProcessRunner"/>.
    /// </summary>
    public static void Flush(ILogger? logger = null) => DefaultInstance.FlushInstance(logger);
}
