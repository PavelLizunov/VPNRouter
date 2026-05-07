using System.ComponentModel;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Localization;
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

    /// <summary>
    /// v2.31.6-r12 (Phase H, iter#5): MainWindowViewModel.Dispose pin —
    /// idempotent, doesn't NRE when called twice, and clears the
    /// internal _disposed flag so subsequent Dispose calls return
    /// immediately. Pre-r12 the VM had no IDisposable surface — this
    /// test pins the new contract.
    ///
    /// <para>We can't easily verify the unhook side-effects (engine
    /// StatusChanged, FreeConfigsVm.Dispose) without mocks the test
    /// harness doesn't have, but the public contract — "calling Dispose
    /// is safe and repeatable" — is verifiable end-to-end. Reflection
    /// peeks at the _disposed flag to confirm state.</para>
    /// </summary>
    [AvaloniaFact]
    public void Dispose_IsIdempotent()
    {
        var vm = new MainWindowViewModel();
        var disposedField = vm.GetType().GetField(
            "_disposed",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        Assert.False((bool)disposedField.GetValue(vm)!, "Fresh VM must not be disposed");

        vm.Dispose();
        Assert.True((bool)disposedField.GetValue(vm)!, "After first Dispose, flag must be true");

        // Second call must NOT throw (idempotent guard).
        vm.Dispose();
        Assert.True((bool)disposedField.GetValue(vm)!, "Flag stays true after second Dispose");
    }
}

/// <summary>
/// v2.31.10 (autostart UX clarity): pin the
/// <c>MainWindowViewModel.ComputeAutostartStatus</c> three-state dispatch
/// table. This is a pure-function regression — no Avalonia harness, no VM
/// constructor, no filesystem — so it runs in the cheapest possible test
/// shape and catches a renamed string or a flipped branch immediately.
///
/// <para>Three states correspond to the three badge colours (green ✓ /
/// amber ⚠ / red ⛔) shown beneath each VPN/Zapret/TgProxy CheckBox in
/// Network → Autostart. Background: a user reported that toggling
/// "Auto-start with Windows" for tgproxy did nothing — the per-component
/// flag is read by VPNRouter.Service at boot, and the service must be
/// installed for that to happen. The badges close the silent-no-op gap.</para>
/// </summary>
public class AutostartStatusComputationTests
{
    [Fact]
    public void ComputeAutostartStatus_ServiceInstalled_ReturnsBootBadge()
    {
        // Service installed, regardless of HasAppBootstrap → green ✓ "via
        // service" wording. The Service path is what handles all three
        // components today, so once the service is installed the badge is
        // unambiguously good.
        var en = Strings.Lang;
        try
        {
            Strings.Lang = "en";
            Assert.Equal(Strings.AutostartStatusBoot,
                MainWindowViewModel.ComputeAutostartStatus(
                    isServiceInstalled: true, hasAppBootstrap: false));
            Assert.Equal(Strings.AutostartStatusBoot,
                MainWindowViewModel.ComputeAutostartStatus(
                    isServiceInstalled: true, hasAppBootstrap: true));
        }
        finally { Strings.Lang = en; }
    }

    [Fact]
    public void ComputeAutostartStatus_NoServiceWithAppBootstrap_ReturnsLoginFallback()
    {
        // After DBG-2 lands an App-side bootstrap for any of the three
        // components, that component flips its HasAppBootstrap flag to true.
        // Without a service, the badge becomes amber ⚠ "fires after App
        // login" — still not boot, but no longer a silent no-op.
        var en = Strings.Lang;
        try
        {
            Strings.Lang = "en";
            Assert.Equal(Strings.AutostartStatusLoginFallback,
                MainWindowViewModel.ComputeAutostartStatus(
                    isServiceInstalled: false, hasAppBootstrap: true));
        }
        finally { Strings.Lang = en; }
    }

    [Fact]
    public void ComputeAutostartStatus_NeitherServiceNorBootstrap_ReturnsNoBoot()
    {
        // Pre-DBG-2 state for vpn/zapret/tgproxy: no service, no App-side
        // bootstrap. Toggling the CheckBox on does nothing. Show red ⛔
        // "won't fire without service" so the user understands.
        var en = Strings.Lang;
        try
        {
            Strings.Lang = "en";
            Assert.Equal(Strings.AutostartStatusNoBoot,
                MainWindowViewModel.ComputeAutostartStatus(
                    isServiceInstalled: false, hasAppBootstrap: false));
        }
        finally { Strings.Lang = en; }
    }

    [Fact]
    public void ComputeAutostartStatus_BilingualParity()
    {
        // RU and EN must both deliver a non-empty string for each of the
        // three states (D1 rule from VPNRouter.App/CLAUDE.md). Catches a
        // future copy edit that removes one branch by accident.
        var en = Strings.Lang;
        try
        {
            foreach (var lang in new[] { "en", "ru" })
            {
                Strings.Lang = lang;
                foreach (var (svc, app) in new[]
                {
                    (true, false), (true, true), (false, true), (false, false)
                })
                {
                    var s = MainWindowViewModel.ComputeAutostartStatus(svc, app);
                    Assert.False(string.IsNullOrWhiteSpace(s),
                        $"Empty status for lang={lang} svc={svc} app={app}");
                }
            }
        }
        finally { Strings.Lang = en; }
    }
}

/// <summary>
/// v2.31.10 (autostart UX clarity): integration-flavour test that wires the
/// real MainWindowViewModel and pins the IsAutostart{Vpn,Zapret,TgProxy}
/// Status{Good,Warn,Bad} flag triplets + per-component LblAutostart*Status
/// labels respond to ServiceVm.IsInstalled flips. The constructor's
/// PropertyChanged handler is the wiring under test — losing it would
/// leave the badges stale until the user navigates away and back, which
/// is exactly the v2.27 Bug B class of regression we're guarding against
/// here for a different surface.
/// </summary>
public class AutostartStatusBindingTests
{
    [AvaloniaFact]
    public void StatusFlags_ReactToServiceInstalledFlip()
    {
        var vm = new MainWindowViewModel();
        var notifications = new HashSet<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName?.StartsWith("LblAutostart") == true ||
                e.PropertyName?.StartsWith("IsAutostart") == true)
                notifications.Add(e.PropertyName);
        };

        // Force IsInstalled=false → expect Bad triplet true, Good+Warn false
        // for all three components (HasAppBootstrap*=false in v2.31.10).
        vm.ServiceVm.IsInstalled = false;
        Assert.False(vm.IsAutostartVpnStatusGood);
        Assert.False(vm.IsAutostartVpnStatusWarn);
        Assert.True(vm.IsAutostartVpnStatusBad);
        Assert.False(vm.IsAutostartZapretStatusGood);
        Assert.False(vm.IsAutostartZapretStatusWarn);
        Assert.True(vm.IsAutostartZapretStatusBad);
        Assert.False(vm.IsAutostartTgProxyStatusGood);
        Assert.False(vm.IsAutostartTgProxyStatusWarn);
        Assert.True(vm.IsAutostartTgProxyStatusBad);

        // Flip IsInstalled=true → expect Good triplet true, Bad+Warn false.
        // The handler in MainWindowViewModel.ctor must re-fire all 12
        // bindings (3 labels + 9 flags). We assert via a HashSet of names
        // observed in the PropertyChanged stream so a missing entry shows
        // up as a clear xUnit failure.
        notifications.Clear();
        vm.ServiceVm.IsInstalled = true;
        Assert.True(vm.IsAutostartVpnStatusGood);
        Assert.False(vm.IsAutostartVpnStatusWarn);
        Assert.False(vm.IsAutostartVpnStatusBad);
        Assert.True(vm.IsAutostartZapretStatusGood);
        Assert.False(vm.IsAutostartZapretStatusWarn);
        Assert.False(vm.IsAutostartZapretStatusBad);
        Assert.True(vm.IsAutostartTgProxyStatusGood);
        Assert.False(vm.IsAutostartTgProxyStatusWarn);
        Assert.False(vm.IsAutostartTgProxyStatusBad);

        Assert.Contains(nameof(MainWindowViewModel.LblAutostartVpnStatus), notifications);
        Assert.Contains(nameof(MainWindowViewModel.LblAutostartZapretStatus), notifications);
        Assert.Contains(nameof(MainWindowViewModel.LblAutostartTgProxyStatus), notifications);
        Assert.Contains(nameof(MainWindowViewModel.IsAutostartVpnStatusGood), notifications);
        Assert.Contains(nameof(MainWindowViewModel.IsAutostartVpnStatusWarn), notifications);
        Assert.Contains(nameof(MainWindowViewModel.IsAutostartVpnStatusBad), notifications);
        Assert.Contains(nameof(MainWindowViewModel.IsAutostartZapretStatusGood), notifications);
        Assert.Contains(nameof(MainWindowViewModel.IsAutostartZapretStatusWarn), notifications);
        Assert.Contains(nameof(MainWindowViewModel.IsAutostartZapretStatusBad), notifications);
        Assert.Contains(nameof(MainWindowViewModel.IsAutostartTgProxyStatusGood), notifications);
        Assert.Contains(nameof(MainWindowViewModel.IsAutostartTgProxyStatusWarn), notifications);
        Assert.Contains(nameof(MainWindowViewModel.IsAutostartTgProxyStatusBad), notifications);
    }

    [AvaloniaFact]
    public void StatusLabels_BoundToExpectedStrings()
    {
        // Service installed → all three labels read AutostartStatusBoot
        // (matches green ✓ branch). Source-pin so a renamed string in
        // Strings.cs surfaces here, not as a runtime UI regression.
        var en = Strings.Lang;
        try
        {
            Strings.Lang = "en";
            var vm = new MainWindowViewModel();

            vm.ServiceVm.IsInstalled = true;
            Assert.Equal(Strings.AutostartStatusBoot, vm.LblAutostartVpnStatus);
            Assert.Equal(Strings.AutostartStatusBoot, vm.LblAutostartZapretStatus);
            Assert.Equal(Strings.AutostartStatusBoot, vm.LblAutostartTgProxyStatus);

            // Service NOT installed, no app-bootstrap (v2.31.10 default for
            // all three) → red ⛔ branch.
            vm.ServiceVm.IsInstalled = false;
            Assert.Equal(Strings.AutostartStatusNoBoot, vm.LblAutostartVpnStatus);
            Assert.Equal(Strings.AutostartStatusNoBoot, vm.LblAutostartZapretStatus);
            Assert.Equal(Strings.AutostartStatusNoBoot, vm.LblAutostartTgProxyStatus);
        }
        finally { Strings.Lang = en; }
    }
}
