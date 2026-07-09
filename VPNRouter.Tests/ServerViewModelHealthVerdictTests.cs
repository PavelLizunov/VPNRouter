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

    // ── R3: pool-wide subnet-risk flags ──────────────────────────────────────

    [Fact]
    public void RefreshProviderRiskFlags_FlagsHighRiskSubnetRows_NotOthers()
    {
        var prevDataDir = VPNRouter.Core.AppPaths.DataDir;
        var temp = Path.Combine(Path.GetTempPath(), $"vpnrouter-prf-{Guid.NewGuid():N}");
        VPNRouter.Core.AppPaths.OverrideDataDir(temp);
        ServerHealthStore.ResetForTests();
        try
        {
            VPNRouter.Core.Models.VlessServerEntry E(string n, string ip) =>
                new() { Name = n, Server = ip, Port = 443, Protocol = "vless" };

            var b1 = E("b1", "10.0.0.1"); var b2 = E("b2", "10.0.0.2");
            var sib = E("sib", "10.0.0.3"); var ok = E("ok", "77.7.7.7");
            ServerHealthStore.Record(b1, ServerHealthVerdict.ProtocolHandshakeBlockedLikely, providerKey: "net:10.0.0.0/24");
            ServerHealthStore.Record(b2, ServerHealthVerdict.ProtocolHandshakeBlockedLikely, providerKey: "net:10.0.0.0/24");
            ServerHealthStore.Record(sib, ServerHealthVerdict.TcpOpenProtocolUntested,       providerKey: "net:10.0.0.0/24");
            ServerHealthStore.Record(ok,  ServerHealthVerdict.Healthy,                       providerKey: "net:77.7.7.0/24");

            var vms = new[] { new ServerViewModel(b1), new ServerViewModel(b2),
                              new ServerViewModel(sib), new ServerViewModel(ok) };
            ServerViewModel.RefreshProviderRiskFlags(vms);

            Assert.True(vms[0].IsProviderHighRisk);
            Assert.True(vms[1].IsProviderHighRisk);
            Assert.True(vms[2].IsProviderHighRisk);     // untested sibling shares the risk
            Assert.False(vms[3].IsProviderHighRisk);
            Assert.Contains(VPNRouter.Core.Localization.Strings.HealthProviderHighRisk,
                vms[2].HealthTooltip);                   // tooltip explains it
        }
        finally
        {
            ServerHealthStore.ResetForTests();
            VPNRouter.Core.AppPaths.OverrideDataDir(prevDataDir);
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    // ── R5: constructor hydration from the persisted store ──────────────────

    [Fact]
    public void Ctor_HydratesFreshPersistedVerdict()
    {
        var prevDataDir = VPNRouter.Core.AppPaths.DataDir;
        var temp = Path.Combine(Path.GetTempPath(), $"vpnrouter-hyd-{Guid.NewGuid():N}");
        VPNRouter.Core.AppPaths.OverrideDataDir(temp);
        ServerHealthStore.ResetForTests();
        try
        {
            var entry = new VPNRouter.Core.Models.VlessServerEntry
            { Name = "srv", Server = "10.9.9.9", Port = 443, Protocol = "vless" };
            ServerHealthStore.Record(entry, ServerHealthVerdict.ProtocolHandshakeBlockedLikely);

            var vm = new ServerViewModel(entry);
            Assert.Equal(ServerHealthVerdict.ProtocolHandshakeBlockedLikely, vm.HealthVerdict);
            Assert.True(vm.HasHealthVerdict);   // the row explains WHY Auto excludes it
        }
        finally
        {
            ServerHealthStore.ResetForTests();
            VPNRouter.Core.AppPaths.OverrideDataDir(prevDataDir);
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
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
