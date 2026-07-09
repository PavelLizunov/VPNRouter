#nullable enable

using VPNRouter.App.ViewModels;
using VPNRouter.Core.Services;
using Xunit;
using CoreStrings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.Tests;

/// <summary>
/// Pins the R2 wiring of the phased health model into <see cref="ServerViewModel"/>:
/// quick-probe + deep-verify outcomes fold through mapper/classifier into one honest
/// verdict line. The audit's rules under test: TCP-only never reads as "works";
/// a local/unsupported deep failure never renders the server "✗"; a TCP-reachable
/// host with a failed proxied HTTP reads as protocol-blocked with the RU-block copy.
/// Plain facts (no Avalonia) — brush properties are deliberately untouched.
/// </summary>
public class ServerViewModelHealthVerdictTests
{
    private static ServerProbeResult Probe(ServerProbeStatus status, int ms = 42)
        => new(status, ms, null);

    // ── No signal → no verdict line ─────────────────────────────────────────

    [Fact]
    public void FreshVm_HasNoVerdict()
    {
        var vm = new ServerViewModel();
        Assert.Equal(ServerHealthVerdict.Unknown, vm.HealthVerdict);
        Assert.False(vm.HasHealthVerdict);
    }

    // ── TCP-only never claims "works" ───────────────────────────────────────

    [Fact]
    public void QuickOkOnly_IsProtocolUntested_NeverHealthy()
    {
        var vm = new ServerViewModel();
        vm.ApplyProbeResult(Probe(ServerProbeStatus.Ok));

        Assert.Equal(ServerHealthVerdict.TcpOpenProtocolUntested, vm.HealthVerdict);
        Assert.True(vm.HasHealthVerdict);
        Assert.Equal(CoreStrings.HealthVerdictLabel(ServerHealthVerdict.TcpOpenProtocolUntested),
            vm.HealthVerdictText);
        Assert.NotEqual(CoreStrings.HealthVerdictLabel(ServerHealthVerdict.Healthy),
            vm.HealthVerdictText);
    }

    [Fact]
    public void QuickUnreachable_IsHostUnreachable()
    {
        var vm = new ServerViewModel();
        vm.ApplyProbeResult(Probe(ServerProbeStatus.Unreachable, 0));
        Assert.Equal(ServerHealthVerdict.HostUnreachable, vm.HealthVerdict);
    }

    // ── Deep verify folds into the verdict ──────────────────────────────────

    [Fact]
    public void DeepOk_IsHealthy()
    {
        var vm = new ServerViewModel();
        vm.ApplyProbeResult(Probe(ServerProbeStatus.Ok));
        vm.ApplyDeepResult(new DeepVerifyResult(true, 120, null, null));

        Assert.True(vm.IsDeepVerified);
        Assert.Equal(ServerHealthVerdict.Healthy, vm.HealthVerdict);
    }

    [Fact]
    public void TcpOk_DeepHttpFail_IsBlockedLikely_WithRuBlockCopyInTooltip()
    {
        var vm = new ServerViewModel();
        vm.ApplyProbeResult(Probe(ServerProbeStatus.Ok));
        vm.ApplyDeepResult(DeepVerifyResult.Failed("http timeout", DeepVerifyFailurePhase.ProxiedHttp));

        Assert.True(vm.IsDeepFailed);
        Assert.False(vm.IsDeepInconclusive);
        Assert.Equal(ServerHealthVerdict.ProtocolHandshakeBlockedLikely, vm.HealthVerdict);
        Assert.Contains(CoreStrings.HealthRuBlockWarning, vm.HealthTooltip);
        Assert.Contains("http timeout", vm.HealthTooltip);   // raw error preserved for diagnostics
    }

    // ── Local / unsupported deep failures never condemn the server ──────────

    [Fact]
    public void DeepUnsupportedByVerifier_IsInconclusive_NotFailed_VerdictStaysUntested()
    {
        var vm = new ServerViewModel();
        vm.ApplyProbeResult(Probe(ServerProbeStatus.Ok));
        vm.ApplyDeepResult(DeepVerifyResult.Failed(
            "deep verify: AmneziaWG needs the lx core (with_awg)",
            DeepVerifyFailurePhase.UnsupportedByVerifier));

        Assert.False(vm.IsDeepFailed);
        Assert.True(vm.IsDeepInconclusive);
        Assert.Equal("!", vm.DeepDisplay);
        Assert.Equal(ServerHealthVerdict.TcpOpenProtocolUntested, vm.HealthVerdict);
        Assert.NotEqual(ServerHealthVerdict.ProtocolHandshakeBlockedLikely, vm.HealthVerdict);
    }

    [Fact]
    public void DeepLocalInfraFailure_LegacyStringResult_IsInconclusive()
    {
        // Legacy 1-arg Failed (phase None) with a local-infra STRING — the mapper's
        // string fallback must keep protecting old results.
        var vm = new ServerViewModel();
        vm.ApplyProbeResult(Probe(ServerProbeStatus.Ok));
        vm.ApplyDeepResult(DeepVerifyResult.Failed("sing-box spawn failed"));

        Assert.False(vm.IsDeepFailed);
        Assert.True(vm.IsDeepInconclusive);
        Assert.Equal(ServerHealthVerdict.TcpOpenProtocolUntested, vm.HealthVerdict);
    }

    // ── A later quick probe re-merges with the stored deep phases ───────────

    [Fact]
    public void QuickReprobe_AfterDeepFail_KeepsBlockedVerdict()
    {
        var vm = new ServerViewModel();
        vm.ApplyProbeResult(Probe(ServerProbeStatus.Ok));
        vm.ApplyDeepResult(DeepVerifyResult.Failed("http failed", DeepVerifyFailurePhase.ProxiedHttp));
        Assert.Equal(ServerHealthVerdict.ProtocolHandshakeBlockedLikely, vm.HealthVerdict);

        // Re-running the quick probe must not wash the deep failure away.
        vm.ApplyProbeResult(Probe(ServerProbeStatus.Ok, 51));
        Assert.Equal(ServerHealthVerdict.ProtocolHandshakeBlockedLikely, vm.HealthVerdict);
    }
}
