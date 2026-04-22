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

        // Collect PropertyChanged hits for our property under test. The VM
        // fires a ton of these during construction (settings load, profile
        // scan, etc.) — we only care about changes AFTER we've latched our
        // starting baseline.
        var notifications = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SmpAutostartChecked))
                notifications.Add($"changed@{vm.SmpAutostartChecked}");
        };

        // Baseline: fresh VM on a clean VM host has no service installed, no
        // AutostartVpn flag → computed must be false.
        Assert.False(vm.SmpAutostartChecked,
            "Initial SmpAutostartChecked should be false on a fresh VM (service not installed)");

        // Case 1 — flip AutostartVpn alone. Not enough on its own (service
        // isn't running), but the computed must re-notify so bindings see
        // the stable-false value. Before the fix this path wouldn't fire at
        // all because SmpAutostartChecked was a plain field.
        notifications.Clear();
        vm.AutostartVpn = true;
        Assert.Contains("changed@", string.Join(",", notifications));
        Assert.False(vm.SmpAutostartChecked, "AutostartVpn alone shouldn't flip Simple on (service not running)");

        // Case 2 — flip ServiceVm.IsInstalled. Still not enough (need IsRunning)
        // but the subscription in the constructor must re-fire PropertyChanged.
        notifications.Clear();
        vm.ServiceVm.IsInstalled = true;
        Assert.Contains("changed@", string.Join(",", notifications));
        Assert.False(vm.SmpAutostartChecked, "IsInstalled alone shouldn't flip Simple on");

        // Case 3 — flip ServiceVm.IsRunning. Now all three signals are true,
        // computed must flip to true AND notify.
        notifications.Clear();
        vm.ServiceVm.IsRunning = true;
        Assert.Contains("changed@True", string.Join(",", notifications));
        Assert.True(vm.SmpAutostartChecked, "All three inputs true → SmpAutostartChecked must be true");

        // Case 4 — flip AutostartVpn back off. Computed drops back to false
        // and notifies. This is the Advanced-mode path where a user unchecks
        // "auto-start VPN" but leaves the service installed (Zapret etc. may
        // still want it); Simple must reflect the VPN-off state.
        notifications.Clear();
        vm.AutostartVpn = false;
        Assert.Contains("changed@False", string.Join(",", notifications));
        Assert.False(vm.SmpAutostartChecked, "AutostartVpn=false → SmpAutostartChecked must be false");
    }
}
