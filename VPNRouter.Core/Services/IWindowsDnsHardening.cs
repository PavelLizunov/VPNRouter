// Phase 4 (Task #36-A, 2026-05-21) — IWindowsDnsHardening seam.
//
// Why: VpnEngine.StartAsync's full happy-path lifecycle (Task #36-C, next
// agent) cannot run end-to-end in tests because StartupPipeline phase 8
// calls WindowsDnsHardening.Apply directly. That static method writes to
// HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\ +
// HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\, which would
// mutate the dev / CI machine's machine-wide DNS-resolution behaviour.
//
// Phase 2G already documented this blocker as the "Phase 8" surprise in
// plans/phase2G-vpnengine-startasync-seam-2026-05-21.md. This brief
// delivers the seam — interface + impl + null — so #36-C can inject
// NullWindowsDnsHardening and exercise the lifecycle without touching real
// registry / netsh state.
//
// Design:
//   * IWindowsDnsHardening is cross-platform (no #if PLATFORM_WINDOWS gate)
//     so consumers (StartupPipeline, VpnEngine) can take it as a ctor
//     dependency without #if-soup at the call site.
//   * WindowsDnsHardeningImpl wraps the existing static class. On non-
//     Windows builds it's a no-op (matches the existing #if-gated callers
//     in StartupPipeline / VpnEngine that wrap their static-call sites in
//     PLATFORM_WINDOWS guards today).
//   * The static class itself is kept untouched — its existing _runnerOverride
//     seam for netsh + the registry checkpoint state in dns-hardening-state.json
//     keep working as before. The impl just delegates; no behaviour change.
//
// Brief: plans/phase4-iwindowsdnshardening-2026-05-21.md.

#nullable enable

using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Abstraction over the Windows DNS-leak-mitigation layer. Closes three
/// leak vectors that bypass our TUN routing (see
/// <c>WindowsDnsHardening</c> doc comment for the full breakdown):
/// <list type="number">
///   <item>SMHNR (Smart Multi-Homed Name Resolution) — Windows 8+ DNS
///   client fanout that queries every active adapter in parallel and uses
///   the first response.</item>
///   <item>Parallel A+AAAA DNS queries — same multi-homed leak vector at
///   the record-type level.</item>
///   <item>TUN interface metric — Windows picks the lowest-metric adapter
///   for DNS when SMHNR is off; we pin VPNRouter-TUN to metric 1 so it
///   always wins.</item>
/// </list>
///
/// <para>Two production paths:
/// <list type="bullet">
///   <item><see cref="WindowsDnsHardeningImpl.Default"/> — back-compat
///   singleton that delegates to the existing static
///   <c>WindowsDnsHardening</c> facade. On non-Windows builds every method
///   is a no-op so cross-platform consumers (CLI, Service, Tests) can wire
///   without #if soup.</item>
///   <item><c>NullWindowsDnsHardening</c> (in <c>VPNRouter.Tests/Fakes</c>) —
///   capture-only stub that records every <see cref="Apply"/> /
///   <see cref="Restore"/> / <see cref="EnableLockdownIfConfigured"/>
///   invocation so happy-path lifecycle tests (Task #36-C) can pin "the
///   pipeline called this exactly once on cold start" without ever touching
///   HKLM.</item>
/// </list></para>
///
/// <para><b>Failure semantics</b>: all implementations MUST swallow
/// exceptions and never throw out of these methods. The existing static
/// helper catches at the top of <c>Apply</c> / <c>Restore</c> and only
/// logs; the impl mirrors that contract. Hardening failure is non-fatal —
/// the user still gets VPN routing, just without the leak mitigations,
/// which is the same outcome as running on a non-elevated process or on
/// a Windows build that lacks the policy registry keys.</para>
///
/// <para>Brief: plans/phase4-iwindowsdnshardening-2026-05-21.md.</para>
/// </summary>
public interface IWindowsDnsHardening
{
    /// <summary>
    /// Apply DNS hardening: disable SMHNR + parallel A/AAAA, set TUN
    /// metric. Saves original values for later <see cref="Restore"/>.
    /// Wave 39 (2026-05-19) extension: also defers the firewall-level
    /// DNS-port lockdown until <see cref="EnableLockdownIfConfigured"/>
    /// fires from the StartupPipeline warm-up success branch (BR-7
    /// 2026-05-20 — pre-r11 the lockdown installed immediately and broke
    /// the warm-up probe on slow-TUN machines).
    /// </summary>
    /// <param name="settings">App settings carrying the
    /// <c>AppConfig.DnsLeakLockdown</c> flag. Null means "skip the Wave 39
    /// firewall layer" — back-compat for legacy callers without access to
    /// the full settings tree.</param>
    /// <param name="logger">Serilog logger for status / error output.</param>
    void Apply(AppSettings? settings, ILogger? logger);

    /// <summary>
    /// Restore original DNS settings + tear down the firewall-level DNS
    /// lockdown (Wave 39 extension — idempotent, runs even when Apply did
    /// not enable it this session). Called from
    /// <see cref="VpnEngine.Stop"/> alongside firewall + sing-box teardown.
    /// </summary>
    /// <param name="logger">Serilog logger.</param>
    void Restore(ILogger? logger);

    /// <summary>
    /// Install the Wave 39 firewall-level DNS-port lockdown (UDP/53 +
    /// TCP/53 + TCP/853 blocked on non-loopback interfaces, with an
    /// allow-exception for sing-box's TUN DNS endpoint via
    /// <see cref="TunSettings.Ipv4Address"/>). Called from
    /// <see cref="StartupPipeline"/>'s warm-up success branch so the
    /// lockdown only fires once TUN is confirmed routing.
    /// </summary>
    /// <param name="settings">App settings carrying
    /// <c>AppConfig.DnsLeakLockdown</c>. No-op when the flag is false or
    /// settings is null.</param>
    /// <param name="logger">Serilog logger.</param>
    void EnableLockdownIfConfigured(AppSettings? settings, ILogger? logger);

    /// <summary>
    /// Reconcile the firewall DNS-port lockdown against live tunnel state —
    /// the fail-open "Auto" semantics. Driven from the HealthMonitor tick with
    /// the live serving signal (and on the sing-box crash hook with
    /// <paramref name="tunnelServing"/>=false) so the lockdown is LIFTED the
    /// moment the tunnel stops routing (user keeps internet while the VPN is
    /// down / reconnecting) and RE-ARMED once it is confirmed serving again.
    /// Idempotent — only mutates the firewall on a real Enable/Disable
    /// transition. No-op when <c>AppConfig.DnsLeakLockdown</c> is false.
    /// See <see cref="DnsLockdownPolicy"/> for the rationale + decision matrix.
    /// </summary>
    /// <param name="tunnelServing">True when the TUN is confirmed routing
    /// (sing-box healthy AND Clash API responding). False on outage / crash.</param>
    /// <param name="settings">App settings carrying the DnsLeakLockdown flag +
    /// TUN CIDR. No-op when null or the flag is off.</param>
    /// <param name="logger">Serilog logger.</param>
    void ReconcileLockdownForHealth(bool tunnelServing, AppSettings? settings, ILogger? logger);
}

/// <summary>
/// Back-compat singleton wrapping the static <c>WindowsDnsHardening</c>
/// facade. On Windows: delegates each call. On non-Windows: every method
/// is a no-op (matches the existing <c>#if PLATFORM_WINDOWS</c> guards
/// around the static-call sites in StartupPipeline + VpnEngine).
///
/// <para>Singleton because the static facade owns process-scoped state
/// (registry checkpoints written to <c>dns-hardening-state.json</c> + the
/// <c>_runnerOverride</c> netsh seam). Instantiating multiple
/// <see cref="WindowsDnsHardeningImpl"/> instances would just hand out
/// aliases to the same underlying static state — keeping it as a
/// singleton makes that explicit at the type level.</para>
/// </summary>
public sealed class WindowsDnsHardeningImpl : IWindowsDnsHardening
{
    /// <summary>Process-wide default instance.</summary>
    public static WindowsDnsHardeningImpl Default { get; } = new();

    private WindowsDnsHardeningImpl() { }

    /// <inheritdoc />
    public void Apply(AppSettings? settings, ILogger? logger)
    {
#if PLATFORM_WINDOWS
        WindowsDnsHardening.Apply(settings, logger);
#endif
    }

    /// <inheritdoc />
    public void Restore(ILogger? logger)
    {
#if PLATFORM_WINDOWS
        WindowsDnsHardening.Restore(logger);
#endif
    }

    /// <inheritdoc />
    public void EnableLockdownIfConfigured(AppSettings? settings, ILogger? logger)
    {
#if PLATFORM_WINDOWS
        WindowsDnsHardening.EnableLockdownIfConfigured(settings, logger);
#endif
    }

    /// <inheritdoc />
    public void ReconcileLockdownForHealth(bool tunnelServing, AppSettings? settings, ILogger? logger)
    {
#if PLATFORM_WINDOWS
        WindowsDnsHardening.ReconcileLockdownForHealth(tunnelServing, settings, logger);
#endif
    }
}
