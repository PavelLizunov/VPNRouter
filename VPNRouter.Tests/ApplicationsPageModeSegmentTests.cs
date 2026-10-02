using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views.Pages;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

// The two segments are ToggleButtons bound to a pair of booleans; pressing the selected one used to uncheck it (the model ignores
// "false"), leaving no segment highlighted although the mode was still set (tester screenshot 9).
public sealed class ApplicationsPageModeSegmentTests
{
    private static (MainWindowViewModel Vm, Window Window, ToggleButton[] Segments) Open(string mode)
    {
        var vm = new MainWindowViewModel(new InMemorySettingsStore());
        vm.IsSplitTunnel = true;
        vm.RoutingAppsMode = mode;
        var page = new ApplicationsPage { DataContext = vm };
        var window = new Window { Width = 520, Height = 700, Content = page };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var segments = page.GetLogicalDescendants().OfType<ToggleButton>().Where(t => t.Classes.Contains("apps-mode-seg")).ToArray();
        return (vm, window, segments);
    }

    [AvaloniaTheory]
    [InlineData("include", 0)]
    [InlineData("exclude", 1)]
    public void PressingTheSelectedSegment_KeepsItSelected(string mode, int index)
    {
        var (vm, window, segments) = Open(mode);
        using (vm)
        {
            Assert.Equal(2, segments.Length);
            Assert.True(segments[index].IsChecked, "precondition: the segment of the current mode starts checked");

            segments[index].IsChecked = false;
            Dispatcher.UIThread.RunJobs();

            Assert.True(segments[index].IsChecked, $"after pressing: segment {index} is checked again (mode={vm.RoutingAppsMode}, other={segments[1 - index].IsChecked})");
            Assert.False(segments[1 - index].IsChecked, "after pressing: the other segment stays unchecked");
            Assert.Equal(mode, vm.RoutingAppsMode);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PressingTheOtherSegment_SwitchesTheMode_AndExactlyOneSegmentStaysSelected()
    {
        var (vm, window, segments) = Open("include");
        using (vm)
        {
            segments[1].IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("exclude", vm.RoutingAppsMode);
            Assert.True(segments[1].IsChecked);
            Assert.False(segments[0].IsChecked);
            window.Close();
        }
    }
}
