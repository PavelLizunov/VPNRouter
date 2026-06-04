using System.Text.Json;
using Serilog;
using VPNRouter.Core.Platform.Unix;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Platform.macOS;

/// <summary>
/// macOS implementation of <see cref="IUnixDnsHardening"/> (Fix #1). Pins the
/// primary network service's system resolver to the TUN gateway so
/// mDNSResponder's queries enter utun99 and get hijack-dns'd, then restores the
/// original on stop / crash. See the interface doc for the full rationale.
///
/// <para>All side effects go through <see cref="IProcessRunner"/> so the command
/// shapes are unit-testable with a fake; the parsing is in <see cref="MacDnsParsers"/>
/// (also unit-tested). Every method is best-effort and never throws — a missing
/// sudoers grant (Fix #5) just degrades to "DNS not hardened", same as pre-fix.</para>
/// </summary>
public sealed class MacDnsHardening : IUnixDnsHardening
{
    private readonly IProcessRunner _runner;
    private readonly string _statePath;

    // The networksetup "empty" token clears DNS back to DHCP-provided servers.
    private const string DhcpToken = "empty";

    public MacDnsHardening(IProcessRunner? runner = null, string? statePath = null)
    {
        _runner = runner ?? new ProcessRunner();
        _statePath = statePath ?? System.IO.Path.Combine(AppPaths.DataDir, "dns-hardening-state.json");
    }

    /// <inheritdoc />
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

            // Crash-safety: only capture the ORIGINAL resolver when we don't
            // already have a saved state. Re-applying (reconnect / post-crash
            // re-entry) must NOT overwrite the saved original with the TUN
            // address — that would turn Restore into a no-op-to-broken.
            if (!System.IO.File.Exists(_statePath))
            {
                var original = GetDnsServers(service, logger);
                SaveState(new MacDnsState { Service = service, OriginalServers = original });
            }

            // Only claim success when networksetup actually applied the change.
            // On failure (missing sudoers grant / networksetup error) the DNS is
            // UNCHANGED — we must not log "Pinned" (it would falsely imply the
            // leak is closed). We KEEP the saved state regardless: networksetup
            // is atomic, so a failed set means DNS is still the saved original
            // and a later Restore is a harmless no-op — strictly safer than
            // deleting it and risking lost recovery. Surface a Warning so the
            // leak path can reflect that hardening did not take effect.
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

    /// <inheritdoc />
    public void Restore(ILogger? logger) => RestoreInternal(logger, "Restore");

    /// <inheritdoc />
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
                return; // nothing to restore — idempotent

            var state = LoadState();
            if (state == null || string.IsNullOrWhiteSpace(state.Service))
            {
                TryDeleteState();
                return;
            }

            // Empty original -> "empty" (DHCP). Otherwise the saved resolver list.
            var servers = state.OriginalServers.Count > 0
                ? state.OriginalServers.ToArray()
                : new[] { DhcpToken };

            // Drop the crash-recovery sentinel ONLY after a confirmed-success
            // restore. If networksetup failed (sudoers revoked, service renamed),
            // keeping the sentinel lets RestoreStrandedIfAny retry next launch
            // instead of stranding DNS on the dead TUN gateway (172.19.0.1)
            // forever — the v2.41.0-r3 stuck-DNS defect.
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

    // ─── command wrappers (via IProcessRunner) ─────────────────────────────

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

    /// <returns>true only when networksetup exited 0 (DNS was actually changed).</returns>
    private bool SetDnsServers(string service, string[] servers, ILogger? logger)
    {
        // Requires root → via sudo -n (non-interactive; fails fast if the
        // networksetup sudoers grant — Fix #5 — isn't present, rather than
        // blocking on a password prompt).
        var args = new List<string> { "-n", "/usr/sbin/networksetup", "-setdnsservers", service };
        args.AddRange(servers);
        return RunSudoChecked(args, logger);
    }

    private void FlushDnsCache(ILogger? logger)
    {
        // Best-effort: a failed flush is a stale cache entry, not a leak.
        RunSudoChecked(new[] { "-n", "/usr/bin/dscacheutil", "-flushcache" }, logger);
        RunSudoChecked(new[] { "-n", "/usr/bin/killall", "-HUP", "mDNSResponder" }, logger);
    }

    private bool RunSudoChecked(IEnumerable<string> sudoArgs, ILogger? logger)
        => RunResult("/usr/bin/sudo", sudoArgs.ToArray(), logger).ok;

    private string Run(string exe, string[] args, ILogger? logger)
        => RunResult(exe, args, logger).stdout;

    /// <summary>
    /// Runs a command and returns BOTH the success flag (exit 0) and stdout.
    /// The success flag is what lets <see cref="Apply"/>/<see cref="RestoreInternal"/>
    /// avoid the "reported success while networksetup actually failed" defect.
    /// </summary>
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

    // ─── persisted crash-recovery state ────────────────────────────────────

    private void SaveState(MacDnsState state)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_statePath)!);
            System.IO.File.WriteAllText(_statePath, JsonSerializer.Serialize(state));
        }
        catch { /* best-effort; absence just means Restore can't auto-heal */ }
    }

    private MacDnsState? LoadState()
    {
        try { return JsonSerializer.Deserialize<MacDnsState>(System.IO.File.ReadAllText(_statePath)); }
        catch { return null; }
    }

    private void TryDeleteState()
    {
        try { if (System.IO.File.Exists(_statePath)) System.IO.File.Delete(_statePath); }
        catch { /* swallow */ }
    }

    /// <summary>Saved resolver state for crash-recovery restore.</summary>
    internal sealed class MacDnsState
    {
        public string Service { get; set; } = string.Empty;
        public List<string> OriginalServers { get; set; } = new();
    }
}
