#nullable enable
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// macOS/Linux DNS-leak hardening seam (Fix #1, deep-audit 2026-06-04). The Unix
/// sibling of <see cref="IWindowsDnsHardening"/>.
///
/// <para><b>Why this exists:</b> on macOS, mDNSResponder reads its upstream
/// resolver from the SystemConfiguration of the primary network service (en0 →
/// ISP), NOT from the routing table. So DNS queries leave on en0 and never enter
/// utun99, bypassing sing-box's hijack-dns entirely — the diagnosed leak from
/// Olga_K's reports (zero DNS in an 849 KB sing-box log). The fix pins the
/// system resolver to the TUN gateway (e.g. 172.19.0.1) so queries are delivered
/// to the TUN, enter sing-box, and get hijack-dns'd through the proxy; the
/// original resolver is saved and restored on stop / crash.</para>
///
/// <para><b>Failure contract:</b> hardening is best-effort and non-fatal — if a
/// command can't run (sudoers not granted, offline, no default route) the user
/// still gets VPN routing, just without the DNS-leak mitigation, the same
/// outcome as the pre-fix behaviour. The implementation NEVER throws out of
/// these methods.</para>
///
/// <para><b>Crash safety:</b> because Apply repoints the system resolver at the
/// TUN, a crash before Restore would leave DNS pointed at a dead interface. The
/// implementation persists a sentinel of the saved state and exposes
/// <see cref="RestoreStrandedIfAny"/> so a fresh launch (and a CLI
/// <c>--dns-reset</c> verb) can heal a stranded hardening.</para>
/// </summary>
public interface IUnixDnsHardening
{
    /// <summary>
    /// Pin the primary network service's DNS to <paramref name="dnsTarget"/>
    /// (the TUN gateway). Saves the original resolver(s) for <see cref="Restore"/>
    /// and writes a crash-recovery sentinel. No-op / best-effort on failure.
    /// </summary>
    /// <param name="dnsTarget">The address to set as the system resolver — the
    /// TUN gateway, derived from the generated config's tun address.</param>
    /// <param name="logger">Serilog logger for status / error output.</param>
    void Apply(string dnsTarget, ILogger? logger);

    /// <summary>
    /// Restore the original system resolver saved by <see cref="Apply"/> and
    /// delete the sentinel. Idempotent — safe to call when Apply never ran.
    /// </summary>
    void Restore(ILogger? logger);

    /// <summary>
    /// If a sentinel from a previous (crashed) session is present, restore the
    /// system resolver from it. Called at startup and by the CLI
    /// <c>--dns-reset</c> verb so a crash can't leave DNS pointed at a dead TUN.
    /// </summary>
    void RestoreStrandedIfAny(ILogger? logger);
}

/// <summary>
/// No-op <see cref="IUnixDnsHardening"/>. The default on Windows (where
/// <see cref="IWindowsDnsHardening"/> handles DNS hardening) and the seam tests
/// pass.
/// </summary>
public sealed class NullUnixDnsHardening : IUnixDnsHardening
{
    /// <summary>Process-wide default no-op instance.</summary>
    public static NullUnixDnsHardening Default { get; } = new();

    /// <inheritdoc />
    public void Apply(string dnsTarget, ILogger? logger) { }

    /// <inheritdoc />
    public void Restore(ILogger? logger) { }

    /// <inheritdoc />
    public void RestoreStrandedIfAny(ILogger? logger) { }
}
