using System.ComponentModel;
using System.Reflection;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;

namespace VPNRouter.Tests;

public class MainWindowViewModelTests
{
    [AvaloniaFact]
    public void SmpAutostartChecked_ReactsToAllThreeInputs()
    {
        var vm = new MainWindowViewModel();

        var notifications = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SmpAutostartChecked))
                notifications.Add($"changed@{vm.SmpAutostartChecked}");
        };

        vm.ServiceVm.IsInstalled = false;
        vm.ServiceVm.IsRunning = false;
        vm.AutostartVpn = false;
        Assert.False(vm.SmpAutostartChecked, "All three inputs false → SmpAutostartChecked must be false");

        notifications.Clear();
        vm.AutostartVpn = true;
        Assert.NotEmpty(notifications);
        Assert.False(vm.SmpAutostartChecked, "AutostartVpn alone shouldn't flip Simple on (service not running)");

        notifications.Clear();
        vm.ServiceVm.IsInstalled = true;
        Assert.NotEmpty(notifications);
        Assert.False(vm.SmpAutostartChecked, "IsInstalled alone shouldn't flip Simple on");

        notifications.Clear();
        vm.ServiceVm.IsRunning = true;
        Assert.Contains("changed@True", string.Join(",", notifications));
        Assert.True(vm.SmpAutostartChecked, "All three inputs true → SmpAutostartChecked must be true");

        notifications.Clear();
        vm.AutostartVpn = false;
        Assert.Contains("changed@False", string.Join(",", notifications));
        Assert.False(vm.SmpAutostartChecked, "AutostartVpn=false → SmpAutostartChecked must be false");
    }
}

public class AutostartStatusComputationTests
{
    [Fact]
    public void ComputeAutostartStatus_ServiceInstalled_ReturnsBootBadge()
    {
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
        var en = Strings.Lang;
        try
        {
            Strings.Lang = "en";
            var vm = new MainWindowViewModel();

            vm.ServiceVm.IsInstalled = true;
            Assert.Equal(Strings.AutostartStatusBoot, vm.LblAutostartVpnStatus);
            Assert.Equal(Strings.AutostartStatusBoot, vm.LblAutostartZapretStatus);
            Assert.Equal(Strings.AutostartStatusBoot, vm.LblAutostartTgProxyStatus);

            vm.ServiceVm.IsInstalled = false;
            Assert.Equal(Strings.AutostartStatusNoBoot, vm.LblAutostartVpnStatus);
            Assert.Equal(Strings.AutostartStatusNoBoot, vm.LblAutostartZapretStatus);
            Assert.Equal(Strings.AutostartStatusNoBoot, vm.LblAutostartTgProxyStatus);
        }
        finally { Strings.Lang = en; }
    }

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

        var keepEntry = new VlessServerEntry { Name = "keep", Server = "1.2.3.4", Port = 443, Uuid = "u-keep" };
        var dropEntry = new VlessServerEntry { Name = "drop", Server = "5.6.7.8", Port = 443, Uuid = "u-drop" };
        vm.Servers.Clear();
        vm.Servers.Add(new ServerViewModel(keepEntry));
        var dropVm = new ServerViewModel(dropEntry);
        vm.Servers.Add(dropVm);

        Assert.Equal(2, vm.Servers.Count);

        InvokeRemoveServerByEntry(vm, dropVm);

        Assert.Single(vm.Servers);
        Assert.DoesNotContain(vm.Servers, s => s.Name == "drop");

        Assert.DoesNotContain(settings.Vless.Servers, s => s.Name == "drop");
    }

    [AvaloniaFact]
    public void AddingServer_AfterCtorLoad_AutoMarksOrphanState_Brat()
    {
        var vm = new MainWindowViewModel();
        var settings = (AppSettings)typeof(MainWindowViewModel)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm)!;

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

        vm.Servers.Clear();

        var subEntry = new ServerViewModel(new VlessServerEntry
        {
            Name = "sub-server", Server = "1.1.1.1", Port = 443, Uuid = "sub-uuid"
        });
        vm.Servers.Add(subEntry);

        var orphanEntry = new ServerViewModel(new VlessServerEntry
        {
            Name = "⚡ [EE] manual", Server = "77.239.126.152", Port = 7443, Uuid = "orphan-uuid"
        });
        vm.Servers.Add(orphanEntry);

        Assert.False(subEntry.IsOrphanFromSubscription, "subscription-matching entry must NOT be marked orphan");
        Assert.True(orphanEntry.IsOrphanFromSubscription, "non-subscription entry must be marked orphan");
    }
}
