using System.Threading.Tasks;
using Serilog;
using VPNRouter.App.ViewModels.FreeConfigs;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

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
    [InlineData(FreeConfigStatus.Slow)]
    [InlineData(FreeConfigStatus.Implausible)]
    [InlineData(FreeConfigStatus.Unreachable)]
    [InlineData(FreeConfigStatus.ParseError)]
    public async Task ApplySelected_NonVerified_RejectedWithoutApply(FreeConfigStatus status)
    {
        var vm = MakeVm(out var applyCalled);
        vm.SelectedItem = new FreeConfigItemViewModel(new FreeConfigEntry
        {
            Status = status, Host = "1.2.3.4", Port = 443, Uuid = "u",
        });

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.False(applyCalled[0]);
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

        Assert.True(applyCalled[0]);
    }

    [Fact]
    public async Task ApplySelected_WhileBusy_DoesNotInvokeApply()
    {
        var vm = MakeVm(out var applyCalled);
        vm.SelectedItem = new FreeConfigItemViewModel(new FreeConfigEntry
        {
            Status = FreeConfigStatus.Verified, Host = "1.2.3.4", Port = 443, Uuid = "u",
        });
        vm.IsBusy = true;

        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.False(applyCalled[0]);
    }
}
