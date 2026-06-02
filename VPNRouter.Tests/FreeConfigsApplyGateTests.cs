using System.Threading.Tasks;
using Serilog;
using VPNRouter.App.ViewModels.FreeConfigs;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// v2.40.0 (interaction-contracts B4 #4): the desktop Free Configs "Apply"
/// command is connectable-gated — only a deep-Verified row may be adopted +
/// connected. A TCP/TLS-only (Ok) candidate, or a row whose last check failed,
/// must be rejected without invoking the apply callback (which would start the
/// VPN toward an unverified endpoint). Mirrors the Android ApplyFcConnectGate.
/// </summary>
public class FreeConfigsApplyGateTests
{
    private static ILogger SilentLogger => new LoggerConfiguration().CreateLogger();

    private static FreeConfigsPageViewModel MakeVm(out bool[] applyCalledBox)
    {
        var called = new bool[1];
        applyCalledBox = called;
        var vm = new FreeConfigsPageViewModel(
            SilentLogger,
            entry => { called[0] = true; return Task.FromResult(true); },
            getSettings: null,
            settingsStore: new InMemorySettingsStore());
        return vm;
    }

    [Theory]
    [InlineData(FreeConfigStatus.Ok)]
    [InlineData(FreeConfigStatus.TlsFailed)]
    [InlineData(FreeConfigStatus.Timeout)]
    [InlineData(FreeConfigStatus.Unknown)]
    public async Task ApplySelected_NonVerified_RejectedWithoutApply(FreeConfigStatus status)
    {
        var vm = MakeVm(out var applyCalled);
        vm.SelectedItem = new FreeConfigItemViewModel(new FreeConfigEntry
        {
            Status = status, Host = "1.2.3.4", Port = 443, Uuid = "u",
        });

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.False(applyCalled[0]); // gate blocked the non-Verified row
        Assert.Equal(VPNRouter.App.Localization.Strings.FcConnectNeedsVerify, vm.StatusText);
    }

    [Fact]
    public async Task ApplySelected_Verified_InvokesApply()
    {
        var vm = MakeVm(out var applyCalled);
        vm.SelectedItem = new FreeConfigItemViewModel(new FreeConfigEntry
        {
            Status = FreeConfigStatus.Verified, Host = "1.2.3.4", Port = 443, Uuid = "u",
        });

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.True(applyCalled[0]); // verified row passes the gate
    }
}
