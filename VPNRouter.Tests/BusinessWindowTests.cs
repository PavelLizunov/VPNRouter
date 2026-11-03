using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Automation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views;
using VPNRouter.Core;
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
        var window = new BusinessWindow { DataContext = vm, Width = width };
        window.Show();
        try
        {
            var controls = window.GetVisualDescendants().ToList();
            Assert.Single(controls.OfType<Button>().Where(b => b.Command == vm.ToggleConnectionCommand));
            Assert.Empty(controls.OfType<TextBox>().Where(t => t.IsEffectivelyVisible));
            Assert.Empty(controls.OfType<TabControl>());
            var selector = Assert.Single(controls.OfType<ComboBox>());
            Assert.False(selector.IsEditable);
            Assert.Equal("BusinessServerSelector", AutomationProperties.GetAutomationId(selector));
            Assert.Equal("Сервер VPN", AutomationProperties.GetName(selector));
            Assert.Same(vm.SelectedServer, selector.SelectedItem);
            Assert.Equal(2, selector.Items.Count);

            var before = store.SaveCount;
            selector.SelectedItem = vm.Servers[1];
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
            var modes = controls.OfType<RadioButton>().ToArray();
            Assert.Equal(2, modes.Length);
            Assert.True(vm.IsSplitTunnel);
            modes[1].IsChecked = true;
            Assert.False(vm.IsSplitTunnel);
            modes[0].IsChecked = true;
            Assert.True(vm.IsSplitTunnel);
            Dispatcher.UIThread.RunJobs();
            Assert.InRange(selector.Bounds.Width, 1, width);
            var scroll = Assert.IsType<ScrollViewer>(window.Content);
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
            Assert.False(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("192.0.2.1") == true));
        }
        finally { window.Close(); }
        var reloaded = store.Load(AppPaths.ConfigYamlPath);
        BusinessDefaults.Apply(reloaded, BusinessDefaultsTests.MultiServerConnection, App.Services.BusinessBootstrap.ReadRules());
        Assert.Equal(BusinessDefaultsTests.ReserveName, reloaded.Vless.ActiveServer);
        // All data is synthetic. No running desktop or live connection is captured.
        using var captureVm = new MainWindowViewModel(store);
        captureVm.StatusText = "VPNRouter Business";
        ScreenshotHelper.Capture(new BusinessWindow { DataContext = captureVm, Width = width }, $"business-{width}");
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
        var window = new BusinessWindow { DataContext = vm };
        window.Show();
        try
        {
            var selector = window.FindControl<ComboBox>("BusinessServerSelector")!;
            Assert.Equal(4, vm.Servers.Count);
            Assert.Equal(new[] { "Primary", "Reserve" }, selector.Items.Cast<ServerViewModel>().Select(s => s.Name));
            selector.SelectedIndex = 1;
            Assert.Equal("Reserve", vm.SelectedServer?.Name);
            Assert.Equal("Reserve", store.Load(AppPaths.ConfigYamlPath).Vless.ActiveServer);
            vm.SelectBusinessServer(vm.Servers[3]);
            Assert.Equal("Reserve", vm.SelectedServer?.Name);
        }
        finally { window.Close(); }
    }

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
