using System.Reflection;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels;

namespace VPNRouter.Tests;

public class MainWindowViewModelDeadConfigAlertTests
{
    private static FieldInfo AlertField =>
        typeof(MainWindowViewModel).GetField("_lastConnectionAlert",
            BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("_lastConnectionAlert field missing");

    [AvaloniaFact]
    public void DeadConfigAlert_DowngradesConnectedToWarning_InSimpleMode()
    {
        var vm = new MainWindowViewModel();

        vm.IsConnecting = false;
        vm.IsConnected = true;
        Assert.Equal(Strings.SmpStatusProtected, vm.SimpleStatusTitle);
        Assert.True(vm.SimpleStatusIsOn);
        Assert.False(vm.SimpleStatusIsWarn);

        const string alert = "⚠ Сервер не отвечает, а других в подписке нет.";
        AlertField.SetValue(vm, alert);

        Assert.Equal(Strings.SmpStatusNotConnected, vm.SimpleStatusTitle);
        Assert.Equal(alert, vm.SimpleStatusDescription);
        Assert.True(vm.SimpleStatusIsWarn);
        Assert.False(vm.SimpleStatusIsOn);
        Assert.False(vm.SimpleStatusIsOff);
    }

    [AvaloniaFact]
    public void NewConnectAttempt_ClearsStaleDeadConfigAlert()
    {
        var vm = new MainWindowViewModel();
        const string stale = "⚠ stale dead-config message";
        AlertField.SetValue(vm, stale);
        Assert.Equal(stale, vm.SimpleStatusDescription);

        vm.IsConnecting = true;

        Assert.Null(AlertField.GetValue(vm));
        Assert.NotEqual(stale, vm.SimpleStatusDescription);
    }
}
