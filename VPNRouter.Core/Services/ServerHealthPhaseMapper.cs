#nullable enable
using System;

namespace VPNRouter.Core.Services;

/// <summary>
/// Pure, network-free bridge from the EXISTING probe outputs
/// (<see cref="ServerProbeStatus"/> from the quick TCP/TLS/UDP probe and
/// <see cref="DeepVerifyResult"/> from the sing-box deep verify) into the
/// <see cref="ServerHealthPhases"/> that <see cref="ServerHealthClassifier"/> consumes.
///
/// <para>It only READS those types — it does not modify them — so wiring it into the
/// real pipeline stays a separate, deferred step
/// (<c>plans/urltest-verification-deferred-risky-2026-07-09.md</c> R1/R2). Adding an
/// explicit failure-phase enum to <see cref="DeepVerifyResult"/> would later replace
/// the string heuristic in <see cref="FromDeepVerify"/>.</para>
///
/// <para>Load-bearing rule: a LOCAL sing-box failure (binary missing, spawn failed,
/// SOCKS never bound, placeholder guard, cancellation) says nothing about the server
/// and must map to <see cref="PhaseOutcome.Unknown"/> — never to a phase
/// <see cref="PhaseOutcome.Fail"/> that would let the classifier read it as a server
/// protocol block.</para>
/// </summary>
public static class ServerHealthPhaseMapper
{
    /// <summary>Map the quick TCP/TLS/UDP probe outcome to the host/transport phases.</summary>
    public static ServerHealthPhases FromQuickProbe(ServerProbeStatus status) => status switch
    {
        // Reachable (protocol-aware quick probe passed the applicable layer).
        ServerProbeStatus.Ok or ServerProbeStatus.Slow
            => new ServerHealthPhases(TcpConnect: PhaseOutcome.Pass),

        // Host not reachable. The enum folds DNS failure into Unreachable; the classifier
        // treats Dns=Fail and TcpConnect=Fail identically (HostUnreachable), so TcpConnect=Fail
        // is the representative signal.
        ServerProbeStatus.Unreachable or ServerProbeStatus.Timeout
            => new ServerHealthPhases(TcpConnect: PhaseOutcome.Fail),

        // TCP reached the host but the TLS/camouflage handshake failed.
        ServerProbeStatus.TlsFailed
            => new ServerHealthPhases(TcpConnect: PhaseOutcome.Pass, TlsCamouflage: PhaseOutcome.Fail),

        // Implausible (<5 ms → local intercept), SkippedNotApplicable, Unknown: inconclusive.
        _ => new ServerHealthPhases(),
    };

    /// <summary>
    /// Map the deep-verify (spawn sing-box + proxied control HTTP) outcome to the
    /// proxy/HTTP phase. A local/infra failure is inconclusive, NOT a server verdict.
    /// R1: reads the typed <see cref="DeepVerifyResult.FailurePhase"/> first; the
    /// error-string heuristic remains only as the fallback for legacy
    /// <see cref="DeepVerifyFailurePhase.None"/> results.
    /// </summary>
    public static ServerHealthPhases FromDeepVerify(DeepVerifyResult? r)
    {
        if (r is null) return new ServerHealthPhases();
        if (r.Ok)
            // R4: the canary conclusion (probed via the same tunnel) rides along —
            // control Pass + canary Fail is exactly the OnlyControlWorks shape.
            return new ServerHealthPhases(
                ProxiedHttpControl: PhaseOutcome.Pass,
                BlockedTargetCanary: r.BlockedCanary);

        switch (r.FailurePhase)
        {
            // Local/infra/guard failures — our sing-box never carried a request;
            // says nothing about the server.
            case DeepVerifyFailurePhase.Precondition:
            case DeepVerifyFailurePhase.LocalSpawn:
            case DeepVerifyFailurePhase.SocksBind:
            case DeepVerifyFailurePhase.Cancelled:
                return new ServerHealthPhases();

            // Explicitly untestable on this build (AWG/xhttp without the lx core,
            // naive without libcronet) — Skipped, so the classifier reads it as
            // "protocol untested", never as a block.
            case DeepVerifyFailurePhase.UnsupportedByVerifier:
                return new ServerHealthPhases(ProxiedHttpControl: PhaseOutcome.Skipped);

            // Server-meaningful: the tunnel came up locally but the control request
            // through it failed (or the overall budget drained trying).
            case DeepVerifyFailurePhase.ProxiedHttp:
            case DeepVerifyFailurePhase.Timeout:
                return new ServerHealthPhases(ProxiedHttpControl: PhaseOutcome.Fail);
        }

        // Legacy (FailurePhase == None): fall back to the string heuristic.
        var err = (r.Error ?? string.Empty).ToLowerInvariant();
        if (IsLocalInfraError(err))
            return new ServerHealthPhases();   // says nothing about the server

        // A real proxied-request failure (http status / timeout) on an otherwise-reachable host.
        return new ServerHealthPhases(ProxiedHttpControl: PhaseOutcome.Fail);
    }

    /// <summary>
    /// True when the deep-verify error is a local/infrastructure/guard failure (our sing-box
    /// never carried a request), so it must not be read as a server protocol block.
    /// Mirrors the failure strings produced by <see cref="VlessDeepVerifier"/>.
    /// </summary>
    private static bool IsLocalInfraError(string err) =>
        err.Contains("binary missing")            // sing-box binary missing
        || err.Contains("spawn failed")           // sing-box spawn failed
        || err.Contains("didn't bind")            // SOCKS port never bound (sing-box failed to start)
        || err.StartsWith("sing-box:")            // "sing-box: <stderr>" — same bind-failure branch
        || err.Contains("placeholder")            // placeholder-credential guard rejected it up front
        || err.Contains("cancelled");             // cancellation, not a verdict

    /// <summary>
    /// Field-wise merge: a later non-<see cref="PhaseOutcome.Unknown"/> outcome wins. Used to
    /// fold the quick-probe phases and the deep-verify phases into one
    /// <see cref="ServerHealthPhases"/> to hand to <see cref="ServerHealthClassifier.Classify"/>.
    /// </summary>
    public static ServerHealthPhases Merge(ServerHealthPhases a, ServerHealthPhases b)
    {
        if (a is null) return b ?? new ServerHealthPhases();
        if (b is null) return a;
        static PhaseOutcome Pick(PhaseOutcome x, PhaseOutcome y) => y != PhaseOutcome.Unknown ? y : x;
        return new ServerHealthPhases(
            Pick(a.Dns, b.Dns),
            Pick(a.TcpConnect, b.TcpConnect),
            Pick(a.TlsCamouflage, b.TlsCamouflage),
            Pick(a.ProxyHandshake, b.ProxyHandshake),
            Pick(a.ProxiedHttpControl, b.ProxiedHttpControl),
            Pick(a.BlockedTargetCanary, b.BlockedTargetCanary),
            Pick(a.UdpAppProfile, b.UdpAppProfile));
    }
}
