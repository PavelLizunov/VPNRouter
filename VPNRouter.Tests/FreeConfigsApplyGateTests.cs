using System.Threading.Tasks;
using Serilog;
using Avalonia.Headless.XUnit;
using VPNRouter.Core;
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

    [AvaloniaFact]
    public async Task LegacySavedList_RetainsRecheckableProfilesAcrossSaveAndRejectsApply()
    {
        var original = AppPaths.DataDir;
        var dir = Directory.CreateTempSubdirectory("vpnrouter-test-");
        try
        {
            AppPaths.OverrideDataDir(dir.FullName);
            var raw = $"vless://{Guid.Empty:D}@saved.example.com:443?security=tls";
            var recent = DateTime.UtcNow.AddDays(-2);
            var expired = recent.AddDays(-30);
            File.WriteAllText(Path.Combine(dir.FullName, "free_configs_saved.json"), $$"""
                {"schema_version":1,"Configs":[
                  {"Id":"keep","RawUri":"{{raw}}","Status":8,"LastTestedAt":"{{recent:O}}"},
                  {"Id":"remove","RawUri":"{{raw}}","Status":8,"LastTestedAt":"{{recent:O}}"},
                  {"Id":"expired","RawUri":"{{raw}}","Status":8,"LastTestedAt":"{{expired:O}}"},
                  {"Id":"untested","RawUri":"{{raw}}","Status":0}]}
                """);
            using (var vm = MakeVm(out var applyCalled))
            {
                vm.EnsureCacheLoaded();
                Assert.Equal(2, vm.DisplayedSavedConfigs.Count);
                var keep = Assert.Single(vm.DisplayedSavedConfigs, row => row.Id == "keep");
                Assert.True(vm.HasStaleSaved);
                Assert.True(vm.RecheckOneCommand.CanExecute(keep));
                vm.SelectedItem = keep;
                await vm.ApplySelectedCommand.ExecuteAsync(null);
                Assert.False(applyCalled[0]);
                vm.RemoveFromSavedCommand.Execute(Assert.Single(vm.DisplayedSavedConfigs, row => row.Id == "remove"));
            }
            using var reopened = MakeVm(out _);
            reopened.EnsureCacheLoaded();
            var remaining = Assert.Single(reopened.DisplayedSavedConfigs);
            Assert.Equal(raw, remaining.Entry.RawUri);
            Assert.Equal(FreeConfigStatus.Unknown, remaining.Entry.Status);
            Assert.Equal(1, reopened.StaleSavedCount);
        }
        finally
        {
            AppPaths.OverrideDataDir(original);
            dir.Delete(recursive: true);
        }
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
