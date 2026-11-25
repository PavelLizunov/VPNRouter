using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VPNRouter.App.Views;

namespace VPNRouter.Tests;

internal static class BusinessWindowTestExtensions
{
    internal static void OpenServerPanel(this BusinessWindow window)
    {
        var panel = window.FindControl<Border>("BusinessServerPanel")!;
        if (!panel.IsVisible)
            window.FindControl<Button>("BusinessServerMenuButton")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Flush(window);
        if (!panel.IsVisible) throw new InvalidOperationException("The Server button did not open its panel.");
    }

    internal static void OpenModePanel(this BusinessWindow window)
    {
        var panel = window.FindControl<Border>("BusinessModePanel")!;
        if (!panel.IsVisible)
            window.FindControl<Button>("BusinessModeMenuButton")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Flush(window);
        if (!panel.IsVisible) throw new InvalidOperationException("The VPN mode button did not open its panel.");
    }

    private static void Flush(BusinessWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
