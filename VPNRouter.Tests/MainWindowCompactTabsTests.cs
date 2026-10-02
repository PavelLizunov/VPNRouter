using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

// The six main tabs keep their labels only while they fit; below that the unselected ones show their icon (found by the size audit:
// at 360 px "Applications" and "Public" were cut off).
[Collection(SafeModeStateCollection.Name)]
public class MainWindowCompactTabsTests
{
    [Theory]
    [InlineData(360, false, true)]
    [InlineData(509, false, true)]
    [InlineData(520, false, false)]
    [InlineData(1000, false, false)]
    [InlineData(520, true, true)]
    [InlineData(649, true, true)]
    [InlineData(700, true, false)]
    public void Rule_DependsOnWidthAndLanguage(double width, bool russian, bool expected) =>
        Assert.Equal(expected, MainWindow.ShouldCompactTabs(width, russian));

    [AvaloniaTheory]
    [InlineData(360, true)]
    [InlineData(900, false)]
    public void Window_AppliesTheCompactClassByItsWidth(int width, bool compact)
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        vm.IsSimpleMode = false;
        var window = new MainWindow { Width = width, Height = 700, DataContext = vm };
        window.Show();
        try
        {
            window.UpdateLayout();
            var tabs = window.FindControl<ListBox>("MainTabs");
            Assert.NotNull(tabs);
            Assert.Equal(compact, tabs!.Classes.Contains("compact"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SelectingATab_KeepsTheStripLaidOutWithinTheWindow()
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        vm.IsSimpleMode = false;
        var window = new MainWindow { Width = 360, Height = 700, DataContext = vm };
        window.Show();
        try
        {
            for (var tab = 0; tab < 6; tab++)
            {
                vm.SelectedTabIndex = tab;
                window.UpdateLayout();
                var tabs = window.FindControl<ListBox>("MainTabs")!;
                Assert.True(tabs.Bounds.Width <= window.ClientSize.Width + 1,
                    $"tab {tab}: the strip is {tabs.Bounds.Width:0} px wide in a {window.ClientSize.Width:0} px window");
            }
        }
        finally
        {
            window.Close();
        }
    }
}
