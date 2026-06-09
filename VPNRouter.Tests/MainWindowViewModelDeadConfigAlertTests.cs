using System.Reflection;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels;

namespace VPNRouter.Tests;

/// <summary>
/// v2.41.2-r3 (rectuspc): when the post-start probe / AutoFailover reports the
/// active config dead while the engine still holds <c>IsConnected=true</c> (a
/// silent dead "Connected" — TUN up, no traffic), Simple Mode must downgrade the
/// deceptive green "Protected" to a warning and show the honest message.
///
/// <para>r2 surfaced the message only into classic <c>StatusText</c>, which
/// Simple Mode (the default UI) does NOT bind — so the message was invisible to
/// exactly the single-server users it targets. This pins the Simple-Mode
/// surfacing: the alert wins over the connected/idle text, flips the status
/// title + dot to a warning, and a fresh connect attempt clears it.</para>
/// </summary>
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

        // Baseline: silent dead "Connected" — TUN up, no transition in flight.
        vm.IsConnecting = false;
        vm.IsConnected = true;
        Assert.Equal(Strings.SmpStatusProtected, vm.SimpleStatusTitle);
        Assert.True(vm.SimpleStatusIsOn);
        Assert.False(vm.SimpleStatusIsWarn);

        // AutoFailover reports the active config dead → alert set.
        const string alert = "⚠ Сервер не отвечает, а других в подписке нет.";
        AlertField.SetValue(vm, alert);

        // The card must now read as a warning, NOT a green "Protected", even
        // though IsConnected is still true (the lie we're correcting).
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

        // A fresh connect attempt (IsConnecting -> true) must clear the stale
        // alert so the card reflects the new attempt, not the last failure.
        vm.IsConnecting = true;

        Assert.Null(AlertField.GetValue(vm));
        Assert.NotEqual(stale, vm.SimpleStatusDescription);
    }
}
