using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using VPNRouter.App.ViewModels.Internals;
using VPNRouter.App.Views;
using VPNRouter.Core;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class BusinessWindowTests
{
    [AvaloniaTheory]
    [InlineData(420)]
    [InlineData(340)]
    public void Pilot_ContainsConnectionModesAndPersistedServerChoice(int width)
    {
        var store = new InMemorySettingsStore();
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, BusinessDefaultsTests.MultiServerConnection, App.Services.BusinessBootstrap.ReadRules());
        settings.App.Language = "ru";
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = new MainWindowViewModel(store);
        vm.StatusText = "VPNRouter Business";
        var window = CreateWindow(vm, width);
        window.Show();
        try
        {
            var controls = window.GetVisualDescendants().ToList();
            Assert.Single(controls.OfType<Button>().Where(b => b.Command == vm.ToggleConnectionCommand));
            Assert.Empty(controls.OfType<TextBox>().Where(t => t.IsEffectivelyVisible));
            Assert.Empty(controls.OfType<TabControl>());
            Assert.DoesNotContain(controls.OfType<RadioButton>(), radio => radio.IsEffectivelyVisible);

            var menu = (Flyout)window.FindControl<Button>("BusinessMenuButton")!.Flyout!;
            var serverButton = window.FindControl<Button>("BusinessServerMenuButton")!;
            var modeButton = window.FindControl<Button>("BusinessModeMenuButton")!;
            Assert.Contains(serverButton, controls);
            Assert.Contains(modeButton, controls);
            Assert.False(menu.IsOpen);

            var serverPanel = window.FindControl<Border>("BusinessServerPanel")!;
            var modePanel = window.FindControl<Border>("BusinessModePanel")!;
            Assert.False(serverPanel.IsVisible);
            Assert.False(modePanel.IsVisible);
            serverButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(menu.IsOpen);
            Assert.True(serverPanel.IsVisible);
            Assert.False(modePanel.IsVisible);
            var selector = Assert.Single(serverPanel.GetVisualDescendants().OfType<ComboBox>());
            Assert.False(selector.IsEditable);
            Assert.Equal("BusinessServerSelector", AutomationProperties.GetAutomationId(selector));
            Assert.Equal("Сервер VPN", AutomationProperties.GetName(selector));
            Assert.Same(vm.SelectedServer, Assert.IsType<BusinessServerChoiceViewModel>(selector.SelectedItem).Server);
            Assert.Equal(2, selector.Items.Count);

            var before = store.SaveCount;
            selector.SelectedItem = selector.Items[1];
            Assert.Same(vm.Servers[1], vm.SelectedServer);
            Assert.True(store.SaveCount > before);
            Assert.Equal(BusinessDefaultsTests.ReserveName, store.Load(AppPaths.ConfigYamlPath).Vless.ActiveServer);

            vm.IsConnecting = true;
            Assert.False(selector.IsEffectivelyEnabled);
            vm.IsConnecting = false;
            vm.IsConnected = true;
            Assert.False(selector.IsEffectivelyEnabled);
            vm.IsConnected = false;
            Assert.True(selector.IsEffectivelyEnabled);
            Assert.InRange(selector.Bounds.Width, 1, width);
            Assert.DoesNotContain(serverPanel.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text?.Contains("192.0.2.1") == true);

            modeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(menu.IsOpen);
            Assert.False(serverPanel.IsVisible);
            Assert.True(modePanel.IsVisible);
            var modes = modePanel.GetVisualDescendants().OfType<RadioButton>().ToArray();
            Assert.Equal(2, modes.Length);
            Assert.True(vm.IsSplitTunnel);
            Assert.True(modes[0].IsChecked);
            Assert.Contains(modeButton.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsEffectivelyVisible && text.Text == Strings.BusinessSplit);
            modes[1].IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsSplitTunnel);
            Assert.Equal("full", store.Load(AppPaths.ConfigYamlPath).App.RoutingMode);
            Assert.Contains(modeButton.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsEffectivelyVisible && text.Text == Strings.BusinessFullCompact);
            vm.IsConnecting = true;
            Assert.All(modes, mode => Assert.False(mode.IsEffectivelyEnabled));
            vm.IsConnecting = false;
            vm.IsConnected = true;
            Assert.All(modes, mode => Assert.False(mode.IsEffectivelyEnabled));
            vm.IsConnected = false;
            modes[0].IsChecked = true;
            Assert.True(vm.IsSplitTunnel);
            Assert.Equal("split", store.Load(AppPaths.ConfigYamlPath).App.RoutingMode);
            modeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(modePanel.IsVisible);
            var scroll = Assert.IsType<ScrollViewer>(window.Content);
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
        }
        finally { window.Close(); }
        var reloaded = store.Load(AppPaths.ConfigYamlPath);
        BusinessDefaults.Apply(reloaded, BusinessDefaultsTests.MultiServerConnection, App.Services.BusinessBootstrap.ReadRules());
        Assert.Equal(BusinessDefaultsTests.ReserveName, reloaded.Vless.ActiveServer);
        // All data is synthetic. No running desktop or live connection is captured.
        using var captureVm = new MainWindowViewModel(store);
        captureVm.StatusText = "VPNRouter Business";
        ScreenshotHelper.Capture(CreateWindow(captureVm, width), $"business-{width}");
    }

    [AvaloniaFact]
    public async Task HomeServerPanel_ReturnsToAutomaticOrKeepsThePickedServer()
    {
        var store = new InMemorySettingsStore();
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, BusinessDefaultsTests.MultiServerConnection,
            BusinessBootstrap.ReadRules());
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = new MainWindowViewModel(store, null, null, null,
            businessBootstrap: static (_, _) => null)
        {
            ExternalTunnelOwned = static () => false,
        };
        vm.BusinessServerVerifier = (_, _, _) =>
            Task.FromResult(new DeepVerifyResult(true, 12, null, null));
        var window = CreateWindow(vm);
        window.Show();
        try
        {
            window.OpenServerPanel();
            var serverPanel = window.FindControl<Border>("BusinessServerPanel")!;
            var check = serverPanel.GetVisualDescendants().OfType<CheckBox>()
                .Single(box => AutomationProperties.GetAutomationId(box) == "BusinessAutoSelect");
            Assert.True(check.IsChecked);
            Assert.Equal(BusinessServerSelectionMode.Automatic,
                store.Load(AppPaths.ConfigYamlPath).App.BusinessServerSelectionMode);

            check.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(BusinessServerSelectionMode.Manual,
                store.Load(AppPaths.ConfigYamlPath).App.BusinessServerSelectionMode);

            check.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            await vm.WaitForBusinessAutoSelectionAsync(TestContext.Current.CancellationToken);
            Assert.Equal(BusinessServerSelectionMode.Automatic,
                store.Load(AppPaths.ConfigYamlPath).App.BusinessServerSelectionMode);

            var selector = window.FindControl<ComboBox>("BusinessServerSelector")!;
            selector.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(BusinessServerSelectionMode.Manual,
                store.Load(AppPaths.ConfigYamlPath).App.BusinessServerSelectionMode);
            Assert.Equal(BusinessDefaultsTests.ReserveName, vm.SelectedServer?.Name);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ServerSelection_RejectsForeignRowsAndBusyChanges()
    {
        var store = new InMemorySettingsStore();
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, BusinessDefaultsTests.MultiServerConnection, App.Services.BusinessBootstrap.ReadRules());
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = new MainWindowViewModel(store);
        var original = vm.SelectedServer;
        var before = store.SaveCount;

        vm.SelectBusinessServer(new ServerViewModel(new VlessServerEntry { Name = "Foreign", Server = "192.0.2.99" }));
        vm.SelectBusinessServer(null);
        vm.IsConnecting = true;
        vm.SelectBusinessServer(vm.Servers[1]);
        vm.IsConnecting = false;
        vm.IsConnected = true;
        vm.SelectBusinessServer(vm.Servers[1]);

        Assert.Same(original, vm.SelectedServer);
        Assert.Equal(before, store.SaveCount);
        vm.IsConnected = false;
    }

    [AvaloniaFact]
    public void Selector_OffersOneNodePerHostAndRetainsBackingVariants()
    {
        var store = new InMemorySettingsStore();
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, BusinessDefaultsTests.SiblingConnection, App.Services.BusinessBootstrap.ReadRules());
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = new MainWindowViewModel(store);
        var window = CreateWindow(vm);
        window.Show();
        try
        {
            window.OpenServerPanel();
            var selector = window.FindControl<ComboBox>("BusinessServerSelector")!;
            Assert.Equal(4, vm.Servers.Count);
            Assert.Equal(new[] { "Primary", "Reserve" }, selector.Items.Cast<BusinessServerChoiceViewModel>().Select(s => s.DisplayName));
            selector.SelectedIndex = 1;
            Assert.Equal("Reserve", vm.SelectedServer?.Name);
            Assert.Equal("Reserve", store.Load(AppPaths.ConfigYamlPath).Vless.ActiveServer);
            vm.SelectBusinessServer(vm.Servers[3]);
            Assert.Equal("Reserve", vm.SelectedServer?.Name);
        }
        finally { window.Close(); }
    }

    private static BusinessWindow CreateWindow(MainWindowViewModel vm, int width = 420) =>
        new(static (_, _) => Task.FromResult(ServerProbeResult.Unknown)) { DataContext = vm, Width = width };

    [AvaloniaFact]
    public void ServerSelection_WriteFailureKeepsWindowUsableAndReportsGenericError()
    {
        var store = new RejectingSettingsStore();
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, BusinessDefaultsTests.MultiServerConnection, App.Services.BusinessBootstrap.ReadRules());
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = new MainWindowViewModel(store);
        store.RejectWrites = true;

        var error = Record.Exception(() => vm.SelectBusinessServer(vm.Servers[1]));

        Assert.Null(error);
        Assert.Same(vm.Servers[1], vm.SelectedServer);
        Assert.Equal(Core.Localization.Strings.BusinessServerSaveFailed, vm.StatusText);
        Assert.DoesNotContain("synthetic-write-failure", vm.StatusText);
    }

    private sealed class RejectingSettingsStore : ISettingsStore
    {
        private readonly InMemorySettingsStore inner = new();
        public bool RejectWrites { get; set; }
        public AppSettings Load(string? path = null) => inner.Load(path);
        public void Save(AppSettings settings, string? path = null)
        {
            if (RejectWrites) throw new UnauthorizedAccessException("synthetic-write-failure");
            inner.Save(settings, path);
        }
        public string? ResetToDefaults(string? path = null) => inner.ResetToDefaults(path);
        public string? ConsumeRecoveryNotice() => inner.ConsumeRecoveryNotice();
        public (int Count, string AtUtc) ConsumePlaceholderPruneNotice(AppSettings settings) => inner.ConsumePlaceholderPruneNotice(settings);
        public void StartWatching(string? path = null, Action<AppSettings>? onReload = null) => inner.StartWatching(path, onReload);
        public void StopWatching() => inner.StopWatching();
    }
}
