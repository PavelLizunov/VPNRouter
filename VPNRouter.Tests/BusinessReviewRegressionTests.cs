using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using VPNRouter.App.ViewModels.Internals;
using VPNRouter.App.Views;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class BusinessReviewRegressionTests
{
    [Theory]
    [InlineData("Office node")]
    [InlineData("OFFICE NODE")]
    public void Refresh_PreservesRetainedPhysicalHost_WhenAnotherHostReusesItsOldLabel(string reusedLabel)
    {
        var previous = new VlessConfig
        {
            ActiveServer = "Office node",
            Servers =
            [
                Server("Office node", "192.0.2.10"),
                Server("Reserve node", "192.0.2.20")
            ]
        };
        var incoming = new List<VlessServerEntry>
        {
            Server(reusedLabel, "192.0.2.20"),
            Server("Renamed retained node", "192.0.2.10")
        };

        var refreshed = BusinessDefaults.CreateServerPool(incoming, previous);

        Assert.Equal("Renamed retained node", refreshed.ActiveServer);
        Assert.Equal("192.0.2.10", Assert.Single(refreshed.GetActiveServers()).Server);
        Assert.Equal(2, refreshed.Servers.Count);
    }

    [AvaloniaFact]
    public void ClearingWindowContext_ReleasesBothActionPanels_AndReattachmentCreatesANewController()
    {
        var store = new InMemorySettingsStore();
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, BusinessDefaultsTests.MultiServerConnection, BusinessBootstrap.ReadRules());
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = new MainWindowViewModel(store);
        var window = new BusinessWindow(static (_, _) => Task.FromResult(ServerProbeResult.Unknown))
        {
            DataContext = vm
        };
        window.Show();
        try
        {
            var diagnostics = window.FindControl<Border>("BusinessActions")!;
            var subscription = window.FindControl<StackPanel>("BusinessSubscriptionActions")!;
            var oldController = Assert.IsType<BusinessServicesViewModel>(diagnostics.DataContext);
            Assert.Same(oldController, subscription.DataContext);
            Assert.True(oldController.CollectCommand.CanExecute(null));

            window.DataContext = null;
            Dispatcher.UIThread.RunJobs();

            Assert.Null(diagnostics.DataContext);
            Assert.Null(subscription.DataContext);
            Assert.False(oldController.CollectCommand.CanExecute(null));

            window.DataContext = vm;
            var newController = Assert.IsType<BusinessServicesViewModel>(diagnostics.DataContext);
            Assert.NotSame(oldController, newController);
            Assert.Same(newController, subscription.DataContext);
            Assert.True(newController.CollectCommand.CanExecute(null));
        }
        finally { window.Close(); }
    }

    private static VlessServerEntry Server(string name, string host) => new()
    {
        Name = name,
        Server = host,
        Port = 443,
        Protocol = "vless",
        Uuid = "synthetic-fixture-identity"
    };
}
