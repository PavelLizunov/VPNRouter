#nullable enable
using System;

namespace VPNRouter.Core.Services;

public static class ServerHealthPhaseMapper
{
    public static ServerHealthPhases FromQuickProbe(ServerProbeStatus status) => status switch
    {
        ServerProbeStatus.Ok or ServerProbeStatus.Slow
            => new ServerHealthPhases(TcpConnect: PhaseOutcome.Pass),

        ServerProbeStatus.Unreachable or ServerProbeStatus.Timeout
            => new ServerHealthPhases(TcpConnect: PhaseOutcome.Fail),

        ServerProbeStatus.TlsFailed
            => new ServerHealthPhases(TcpConnect: PhaseOutcome.Pass, TlsCamouflage: PhaseOutcome.Fail),

        _ => new ServerHealthPhases(),
    };

    public static ServerHealthPhases FromDeepVerify(DeepVerifyResult? r)
    {
        if (r is null) return new ServerHealthPhases();
        if (r.Ok)
            return new ServerHealthPhases(
                ProxiedHttpControl: PhaseOutcome.Pass,
                BlockedTargetCanary: r.BlockedCanary);

        switch (r.FailurePhase)
        {
            case DeepVerifyFailurePhase.Precondition:
            case DeepVerifyFailurePhase.LocalSpawn:
            case DeepVerifyFailurePhase.SocksBind:
            case DeepVerifyFailurePhase.Cancelled:
                return new ServerHealthPhases();

            case DeepVerifyFailurePhase.UnsupportedByVerifier:
                return new ServerHealthPhases(ProxiedHttpControl: PhaseOutcome.Skipped);

            case DeepVerifyFailurePhase.ProxiedHttp:
            case DeepVerifyFailurePhase.Timeout:
                return new ServerHealthPhases(ProxiedHttpControl: PhaseOutcome.Fail);
        }

        var err = (r.Error ?? string.Empty).ToLowerInvariant();
        if (IsLocalInfraError(err))
            return new ServerHealthPhases();

        return new ServerHealthPhases(ProxiedHttpControl: PhaseOutcome.Fail);
    }

    private static bool IsLocalInfraError(string err) =>
        err.Contains("binary missing")
        || err.Contains("spawn failed")
        || err.Contains("didn't bind")
        || err.StartsWith("sing-box:")
        || err.Contains("placeholder")
        || err.Contains("cancelled");

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
