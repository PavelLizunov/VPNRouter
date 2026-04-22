using System.ComponentModel;
using Avalonia.Headless.XUnit;
using VPNRouter.App.ViewModels;

namespace VPNRouter.Tests;

/// <summary>
/// Headless regression tests for MainWindowViewModel wiring. These don't open
/// a window — they instantiate the VM directly and probe property behaviour.
/// Anything that needs the Avalonia dispatcher (e.g. async UI-bound work) uses
/// <see cref="AvaloniaFactAttribute"/> so the test runs on the dispatcher
/// thread; pure-data tests can stay on plain <c>[Fact]</c>.
/// </summary>
public class MainWindowViewModelTests
{
    /// <summary>
    /// v2.27 Bug B regression: ticking the Advanced "Enable background
    /// service" master toggle used to leave Simple's "Start with Windows"
    /// checkbox stuck at false because Simple was a one-shot field seeded
    /// from AutostartVpn. Now it's a computed over
    /// (ServiceVm.IsInstalled + ServiceVm.IsRunning + AutostartVpn) and
    /// fires PropertyChanged on every input — this test pins that wiring.
    ///
    /// <para>The VM runs its real constructor (logger → file, settings load,
    /// subscription state), so we don't mock anything. We flip the three
    /// inputs to the computed one at a time and observe the PropertyChanged
    /// stream for SmpAutostartChecked. Catches both the computed-value
    /// regression AND the re-notify regression in one shot.</para>
    /// </summary>
    [AvaloniaFact]
    public void SmpAutostartChecked_ReactsToAllThreeInputs()
    {
        var vm = new MainWindowViewModel();

        // We can't assume an initial state for the three inputs — a dev host
        // may already have the service installed and AutostartVpn set from
        // prior testing. Instead we force each input to a known value and
        // assert the transition fires PropertyChanged AND the computed lines
        // up with the inputs on each step. This still catches the pre-Bug-B
        // regression (plain field, no re-notify on ServiceVm changes) because
        // the test fails the moment one of the three inputs stops notifying.
        var notifications = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SmpAutostartChecked))
                notifications.Add($"changed@{vm.SmpAutostartChecked}");
        };

        // Force all three inputs to false → computed must be false.
        vm.ServiceVm.IsInstalled = false;
        vm.ServiceVm.IsRunning = false;
        vm.AutostartVpn = false;
        Assert.False(vm.SmpAutostartChecked, "All three inputs false → SmpAutostartChecked must be false");

        // Flip AutostartVpn alone. Computed stays false (service off), but
        // the PropertyChanged stream must fire — that's the re-notify pin.
        notifications.Clear();
        vm.AutostartVpn = true;
        Assert.NotEmpty(notifications);
        Assert.False(vm.SmpAutostartChecked, "AutostartVpn alone shouldn't flip Simple on (service not running)");

        // Flip ServiceVm.IsInstalled. Still not enough (need IsRunning) but
        // the ServiceVm.PropertyChanged subscription in the ctor must fire.
        notifications.Clear();
        vm.ServiceVm.IsInstalled = true;
        Assert.NotEmpty(notifications);
        Assert.False(vm.SmpAutostartChecked, "IsInstalled alone shouldn't flip Simple on");

        // Flip ServiceVm.IsRunning. Now all three are true → computed flips
        // to true AND notifies.
        notifications.Clear();
        vm.ServiceVm.IsRunning = true;
        Assert.Contains("changed@True", string.Join(",", notifications));
        Assert.True(vm.SmpAutostartChecked, "All three inputs true → SmpAutostartChecked must be true");

        // Flip AutostartVpn back off. Computed drops back to false and
        // notifies — Advanced-mode path where a user unchecks "auto-start
        // VPN" but leaves the service installed (Zapret etc. may still need
        // it); Simple must reflect the VPN-off state.
        notifications.Clear();
        vm.AutostartVpn = false;
        Assert.Contains("changed@False", string.Join(",", notifications));
        Assert.False(vm.SmpAutostartChecked, "AutostartVpn=false → SmpAutostartChecked must be false");
    }
}
