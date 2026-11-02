using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
    public void Pilot_ContainsOneActionTwoModesAndNoSettings(int width)
    {
        var store = new InMemorySettingsStore();
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, BusinessDefaultsTests.SampleConnection, App.Services.BusinessBootstrap.ReadRules());
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = new MainWindowViewModel(store);
        vm.StatusText = "VPNRouter Business";
        var window = new BusinessWindow { DataContext = vm, Width = width };
        window.Show();
        try
        {
            var controls = window.GetVisualDescendants().ToList();
            Assert.Single(controls.OfType<Button>().Where(b => b.Command == vm.ToggleConnectionCommand));
            Assert.Empty(controls.OfType<TextBox>());
            Assert.Empty(controls.OfType<TabControl>());
            var modes = controls.OfType<RadioButton>().ToArray();
            Assert.Equal(2, modes.Length);
            Assert.True(vm.IsSplitTunnel);
            modes[1].IsChecked = true;
            Assert.False(vm.IsSplitTunnel);
            modes[0].IsChecked = true;
            Assert.True(vm.IsSplitTunnel);
            Assert.False(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("192.0.2.1") == true));
        }
        finally { window.Close(); }
        // All data is synthetic. No running desktop or live connection is captured.
        using var captureVm = new MainWindowViewModel(store);
        captureVm.StatusText = "VPNRouter Business";
        ScreenshotHelper.Capture(new BusinessWindow { DataContext = captureVm, Width = width }, $"business-{width}");
    }
}
