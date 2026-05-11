using System.ComponentModel;
using System.Reflection;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;

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

    // F-12 SmpToggleConnect_WithUnsaved* tests removed 2026-05-10 as part of
    // the desktop revert to v2.32.0 (commit d9f7027). The Connect-block-on-
    // unsaved-input behaviour those tests pinned was a F-11/F-12 chip
    // addition that's no longer present on desktop; v2.32.0 has the simpler
    // "Connect always enabled" flow. The defense-in-depth backstop
    // (LeakProtection.ValidateAppSettings) stays in Core for any future
    // resurfacing of the silent-flip class — see LeakProtectionAppSettingsTests
    // in UnitTest1.cs which is the still-active invariant pin.
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

    // ─── G-4 (r10 r9 audit) Bug-r10-D regression: server-delete persists through SaveSettings ───
    //
    // User report (brat, 2026-05-11): deleted a VLESS server via row ×
    // button; after restart, the entry was back. Root cause:
    // RemoveServerByEntry only mutated the in-memory ObservableCollection
    // and never called SaveSettings → YAML kept the stale entry. r6 added
    // SaveSettings() call inside RemoveServerByEntry.
    //
    // This pin asserts that AFTER RemoveServerByEntryCommand executes,
    // settings.Vless.Servers reflects the deletion (the in-memory model
    // that SaveSettings persists from).

    /// <summary>
    /// Reflectively invokes the private <c>RemoveServerByEntry</c> command
    /// because <c>[RelayCommand]</c>-generated <c>RemoveServerByEntryCommand</c>
    /// is public, but to avoid coupling to the exact generated signature
    /// we just invoke the underlying method via reflection.
    /// </summary>
    private static void InvokeRemoveServerByEntry(MainWindowViewModel vm, ServerViewModel entry)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "RemoveServerByEntry",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(vm, new object?[] { entry });
    }

    [AvaloniaFact]
    public void RemoveServerByEntry_Persists_BratRegression()
    {
        var vm = new MainWindowViewModel();
        var settings = (AppSettings)typeof(MainWindowViewModel)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm)!;

        // Seed two servers in the VM Servers collection
        var keepEntry = new VlessServerEntry { Name = "keep", Server = "1.2.3.4", Port = 443, Uuid = "u-keep" };
        var dropEntry = new VlessServerEntry { Name = "drop", Server = "5.6.7.8", Port = 443, Uuid = "u-drop" };
        vm.Servers.Clear();
        vm.Servers.Add(new ServerViewModel(keepEntry));
        var dropVm = new ServerViewModel(dropEntry);
        vm.Servers.Add(dropVm);

        // Pin: BEFORE delete, both are in the collection
        Assert.Equal(2, vm.Servers.Count);

        // Act
        InvokeRemoveServerByEntry(vm, dropVm);

        // After delete: removed from in-memory collection
        Assert.Single(vm.Servers);
        Assert.DoesNotContain(vm.Servers, s => s.Name == "drop");

        // r6 fix contract: settings.Vless.Servers reflects the deletion
        // (this is what SaveSettings -> SettingsLoader.Save persists to
        // YAML). Without the r6 fix, settings.Vless.Servers would still
        // contain "drop" until next SaveSettings, and an app close
        // without explicit Apply would lose the deletion.
        Assert.DoesNotContain(settings.Vless.Servers, s => s.Name == "drop");
    }

    // ─── G-6 (r10 r9 audit) Bug-r10-H regression: "Не из подписки" badge consistency ───
    //
    // User report (brat screenshot, 2026-05-12): manual entry added at
    // startup (via YAML on disk) showed the orphan badge, but a manual
    // entry added in the same session via Free Configs → Use did NOT
    // show the badge. Root cause: MarkOrphanServers ran only in
    // LoadSettingsIntoUI + RemoveServerByEntry; other add paths
    // skipped it. r9 wired Servers.CollectionChanged → MarkOrphanServers
    // (with _isLoadingUI guard for bulk reload).
    //
    // This pin asserts the auto-rewire: adding to Servers after ctor
    // (i.e. _isLoadingUI=false) triggers re-evaluation.

    [AvaloniaFact]
    public void AddingServer_AfterCtorLoad_AutoMarksOrphanState_Brat()
    {
        var vm = new MainWindowViewModel();
        var settings = (AppSettings)typeof(MainWindowViewModel)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm)!;

        // Set up: an active subscription with one server
        settings.App.Subscriptions = new List<SubscriptionEntry>
        {
            new()
            {
                Name = "sub-1",
                Url = "https://example.com/sub",
                Enabled = true,
                Servers = new List<VlessServerEntry>
                {
                    new() { Name = "sub-server", Server = "1.1.1.1", Port = 443, Uuid = "sub-uuid" }
                }
            }
        };

        // Clear any preload from ctor, then add manually (post-ctor flow)
        vm.Servers.Clear();

        // Add an entry that IS in the subscription
        var subEntry = new ServerViewModel(new VlessServerEntry
        {
            Name = "sub-server", Server = "1.1.1.1", Port = 443, Uuid = "sub-uuid"
        });
        vm.Servers.Add(subEntry);

        // Add an entry that is NOT in any subscription (Free Config / paste)
        var orphanEntry = new ServerViewModel(new VlessServerEntry
        {
            Name = "⚡ [EE] manual", Server = "77.239.126.152", Port = 7443, Uuid = "orphan-uuid"
        });
        vm.Servers.Add(orphanEntry);

        // r9 fix contract: CollectionChanged → MarkOrphanServers
        // automatically. Sub-matching entry: orphan=false. Free Config
        // entry: orphan=true.
        Assert.False(subEntry.IsOrphanFromSubscription, "subscription-matching entry must NOT be marked orphan");
        Assert.True(orphanEntry.IsOrphanFromSubscription, "non-subscription entry must be marked orphan");
    }
}
