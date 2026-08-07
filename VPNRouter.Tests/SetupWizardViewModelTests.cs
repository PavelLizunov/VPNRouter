#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class SetupWizardViewModelTests
{
    [Fact]
    public async Task Restore_UsesDefaultMtu_WithoutForcingRoutingMode()
    {
        var calls = new List<(int Mtu, bool Split)>();
        var vm = Create(1300, initialSplit: false, (mtu, split) => calls.Add((mtu, split)));

        await vm.RestoreSafeSettingsCommand.ExecuteAsync(null);

        Assert.Equal([(TunSettings.DefaultMtu, false)], calls);
        Assert.Equal(TunSettings.DefaultMtu, vm.CurrentMtu);
        Assert.True(vm.CanUndo);
        Assert.True(vm.IsStepFour);
    }

    [Fact]
    public async Task Undo_RestoresOpeningMtuAndRoutingSnapshot()
    {
        var calls = new List<(int Mtu, bool Split)>();
        var vm = Create(1300, initialSplit: true, (mtu, split) => calls.Add((mtu, split)));
        vm.SelectedSplitTunnel = false;

        await vm.RestoreSafeSettingsCommand.ExecuteAsync(null);
        await vm.UndoCommand.ExecuteAsync(null);

        Assert.Equal(
            [(TunSettings.DefaultMtu, false), (1300, true)],
            calls);
        Assert.Equal(1300, vm.CurrentMtu);
        Assert.True(vm.SelectedSplitTunnel);
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public async Task ResetMtu_DoesNotApplyAnUncommittedRoutingChoice()
    {
        var calls = new List<(int Mtu, bool Split)>();
        var vm = Create(1300, initialSplit: true, (mtu, split) => calls.Add((mtu, split)));
        vm.SelectedSplitTunnel = false;

        await vm.ResetMtuCommand.ExecuteAsync(null);

        Assert.Equal([(TunSettings.DefaultMtu, true)], calls);
    }

    [Fact]
    public async Task ApplyFailure_IsShownAndLeavesTheRepairStepUsable()
    {
        var vm = Create(1300, initialSplit: true, (_, _) => throw new System.IO.IOException());
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);

        await vm.RestoreSafeSettingsCommand.ExecuteAsync(null);

        Assert.Equal(Strings.SetupWizardApplyFailed, vm.OperationStatus);
        Assert.True(vm.IsStepThree);
        Assert.False(vm.IsBusy);
        Assert.Equal(1300, vm.CurrentMtu);
    }

    [Fact]
    public void ClosingWithoutApply_DoesNotPersistAnything()
    {
        var calls = 0;
        var vm = Create(1400, initialSplit: true, (_, _) => calls++);

        vm.CloseCommand.Execute(null);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task HealthResults_AreMappedAndSummarized()
    {
        var vm = new SetupWizardViewModel(
            TunSettings.DefaultMtu,
            true,
            (_, _) => { },
            () =>
            [
                new(HealthCheck.Level.Ok, "config ok"),
                new(HealthCheck.Level.Warn, "dns warning"),
                new(HealthCheck.Level.Err, "tun error"),
            ],
            () => Task.CompletedTask);

        await vm.RunChecksCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.CheckResults.Count);
        Assert.Equal(Strings.SetupWizardCheckWarning, vm.CheckResults[1].Level);
        Assert.Equal(Strings.SetupWizardChecksSummary(1, 1), vm.CheckSummary);
    }

    private static SetupWizardViewModel Create(
        int initialMtu,
        bool initialSplit,
        System.Action<int, bool> apply) => new(
            initialMtu,
            initialSplit,
            apply,
            () => [],
            () => Task.CompletedTask);
}
