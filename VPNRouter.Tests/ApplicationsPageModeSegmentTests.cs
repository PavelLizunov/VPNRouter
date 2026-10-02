using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views.Pages;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

// The two segments used to be ToggleButtons bound to a pair of booleans: pressing the selected one unchecked it (the model ignores
// "false"), leaving no segment highlighted although the mode was still set (tester screenshot 9). They are radio buttons now.
public sealed class ApplicationsPageModeSegmentTests
{
    private static (MainWindowViewModel Vm, Window Window, RadioButton[] Segments) Open(string mode)
    {
        var vm = new MainWindowViewModel(new InMemorySettingsStore());
        vm.IsSplitTunnel = true;
        vm.RoutingAppsMode = mode;
        var page = new ApplicationsPage { DataContext = vm };
        var window = new Window { Width = 520, Height = 700, Content = page };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var segments = page.GetLogicalDescendants().OfType<RadioButton>().Where(t => t.Classes.Contains("apps-mode-seg")).ToArray();
        return (vm, window, segments);
    }

    private static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
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
            Assert.True(segments[index].IsChecked, "the segment of the current mode starts checked");

            Click(window, segments[index]);

            Assert.True(segments[index].IsChecked, "the pressed segment is still checked");
            Assert.False(segments[1 - index].IsChecked, "the other segment stays unchecked");
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
            Click(window, segments[1]);

            Assert.Equal("exclude", vm.RoutingAppsMode);
            Assert.True(segments[1].IsChecked);
            Assert.False(segments[0].IsChecked);
            window.Close();
        }
    }
}
