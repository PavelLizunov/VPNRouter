using System.Text.Json;
using Serilog;
using VPNRouter.Core.Platform.Unix;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Platform.Linux;

/// <summary>
/// Linux implementation of <see cref="IUnixDnsHardening"/> via systemd-resolved
/// (<c>resolvectl</c>). The Linux sibling of <see cref="VPNRouter.Core.Platform.macOS.MacDnsHardening"/>.
///
/// <para><b>Why:</b> on a glibc/systemd box, <c>systemd-resolved</c> sends DNS to
/// the per-link resolvers it learned from the physical NIC (DHCP/ISP), NOT through
/// the routing table — so queries can leave on the physical link and never enter
/// sing-box's TUN + hijack-dns, the same leak class diagnosed on macOS. The fix
/// points resolved at the TUN: we set the TUN link's DNS to the TUN gateway and
/// give it the default routing domain <c>~.</c>, so <i>all</i> resolution is sent
/// down the TUN to 172.19.0.1 and gets hijack-dns'd through the proxy.</para>
///
/// <para><b>Why the TUN link (not the physical link):</b> setting it on the TUN
/// link means systemd-resolved AUTOMATICALLY drops the per-link config when the
/// TUN disappears (sing-box stops), so a crash can't strand the physical resolver
/// — strictly safer than the macOS approach, which must restore the physical
/// service. <see cref="Restore"/> still issues an explicit <c>resolvectl revert</c>
/// for the clean-stop path.</para>
///
/// <para><b>Failure contract (fail-open):</b> best-effort and non-fatal. If
/// <c>resolvectl</c> is absent (non-systemd distro), the call is denied (polkit /
/// missing CAP_NET_ADMIN), or the TUN interface can't be resolved, the user still
/// gets VPN routing — just without the DNS-leak mitigation, the same outcome as the
/// pre-fix <see cref="NullUnixDnsHardening"/>. It NEVER throws and NEVER rewrites
/// <c>/etc/resolv.conf</c> by hand. All side effects go through
/// <see cref="IProcessRunner"/> so the command shapes are unit-testable with a fake.</para>
/// </summary>
public sealed class LinuxDnsHardening : IUnixDnsHardening
{
    private readonly IProcessRunner _runner;
    private readonly string _statePath;

    // systemd-resolved CLI + the routing tool used to map the gateway to its link.
    // Bare names (resolved via PATH) — both live in PATH on any systemd distro and
    // the exact directory (/usr/bin vs /bin) varies, unlike the stable macOS paths.
    private const string Resolvectl = "resolvectl";
    private const string Ip = "ip";

    // The systemd-resolved "default routing domain" — sends ALL name resolution to
    // this link, the analogue of pinning the system resolver on macOS.
    private const string DefaultRoutingDomain = "~.";

    public LinuxDnsHardening(IProcessRunner? runner = null, string? statePath = null)
    {
        _runner = runner ?? new ProcessRunner();
        _statePath = statePath ?? System.IO.Path.Combine(AppPaths.DataDir, "linux-dns-hardening-state.json");
    }

    /// <inheritdoc />
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

            // Map the TUN gateway to its interface. `ip route get <gateway>` returns
            // the device that carries traffic to the gateway — the sing-box TUN
            // (e.g. VPNRouter-TUN). Deriving it here keeps the IUnixDnsHardening
            // signature unchanged (no TUN-name parameter), mirroring how
            // MacDnsHardening self-detects the device.
            var iface = GetTunInterface(dnsTarget, logger);
            if (iface == null)
            {
                logger?.Warning(
                    "[LinuxDnsHardening] Apply: could not resolve the TUN interface for {Target} " +
                    "(ip route get returned no device) — skipping", dnsTarget);
                return;
            }

            // Crash-safety sentinel: capture the interface so a crashed session can
            // be reverted on the next launch. Only the interface is needed —
            // `resolvectl revert` restores the link to its defaults; we never have
            // to remember the original servers.
            if (!System.IO.File.Exists(_statePath))
                SaveState(new LinuxDnsState { Interface = iface });

            // Only claim success when BOTH the resolver pin AND the routing-domain
            // took effect. On failure (polkit denied / no CAP_NET_ADMIN) the DNS is
            // unchanged — we must not log "Pinned" (it would falsely imply the leak
            // is closed). The sentinel is kept regardless (a no-op revert later is
            // harmless), surfacing a Warning so the leak path reflects the miss.
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

    /// <inheritdoc />
    public void Restore(ILogger? logger) => RestoreInternal(logger, "Restore");

    /// <inheritdoc />
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
                return; // nothing to restore — idempotent

            var state = LoadState();
            if (state == null || string.IsNullOrWhiteSpace(state.Interface))
            {
                TryDeleteState();
                return;
            }

            // `resolvectl revert <link>` clears all per-interface DNS config we set.
            // Unlike macOS, a FAILED revert here is almost always "the TUN link is
            // already gone" (sing-box stopped → resolved auto-dropped the per-link
            // config = already in the correct state), so we clear the sentinel
            // rather than keep retrying a revert against a vanished link forever.
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

    // ─── command wrappers (via IProcessRunner) ─────────────────────────────

    private bool ResolvectlAvailable(ILogger? logger)
        => RunResult(Resolvectl, new[] { "--version" }, logger).ok;

    private string? GetTunInterface(string dnsTarget, ILogger? logger)
    {
        // -o = one line per route (stable to parse). The gateway address is the
        // /30 TUN gateway, so the route to it goes out the TUN device.
        var stdout = Run(Ip, new[] { "-o", "route", "get", dnsTarget }, logger);
        return ParseRouteGetDevice(stdout);
    }

    private bool RunResolvectl(string[] args, ILogger? logger)
        => RunResult(Resolvectl, args, logger).ok;

    private void FlushDnsCache(ILogger? logger)
        // Best-effort: a failed flush is a stale cache entry, not a leak.
        => RunResolvectl(new[] { "flush-caches" }, logger);

    private string Run(string exe, string[] args, ILogger? logger)
        => RunResult(exe, args, logger).stdout;

    /// <summary>
    /// Runs a command and returns BOTH the success flag (exit 0) and stdout. The
    /// success flag is what lets <see cref="Apply"/>/<see cref="RestoreInternal"/>
    /// avoid the "reported success while resolvectl actually failed" defect.
    /// </summary>
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

    /// <summary>
    /// Parse the device from <c>ip -o route get &lt;addr&gt;</c> output, e.g.
    /// <c>"172.19.0.1 dev VPNRouter-TUN src 172.19.0.2 uid 1000 \ cache"</c> -&gt;
    /// <c>"VPNRouter-TUN"</c>. Returns null when no <c>dev</c> token is present.
    /// </summary>
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

    // ─── persisted crash-recovery state ────────────────────────────────────

    private void SaveState(LinuxDnsState state)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_statePath)!);
            System.IO.File.WriteAllText(_statePath, JsonSerializer.Serialize(state));
        }
        catch { /* best-effort; absence just means Restore can't auto-heal */ }
    }

    private LinuxDnsState? LoadState()
    {
        try { return JsonSerializer.Deserialize<LinuxDnsState>(System.IO.File.ReadAllText(_statePath)); }
        catch { return null; }
    }

    private void TryDeleteState()
    {
        try { if (System.IO.File.Exists(_statePath)) System.IO.File.Delete(_statePath); }
        catch { /* swallow */ }
    }

    /// <summary>Saved state for crash-recovery revert — only the TUN interface.</summary>
    internal sealed class LinuxDnsState
    {
        public string Interface { get; set; } = string.Empty;
    }
}
