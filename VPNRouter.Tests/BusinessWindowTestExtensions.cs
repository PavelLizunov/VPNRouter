using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VPNRouter.App.Views;

namespace VPNRouter.Tests;

internal static class BusinessWindowTestExtensions
{
    internal static Flyout OpenBusinessMenu(this BusinessWindow window)
    {
        var button = window.FindControl<Button>("BusinessMenuButton")!;
        var flyout = (Flyout)button.Flyout!;
        if (!flyout.IsOpen) flyout.ShowAt(button);
        Flush(window);
        return flyout;
    }

    internal static void OpenServerPanel(this BusinessWindow window)
    {
        window.OpenBusinessMenu();
        var panel = window.FindControl<Border>("BusinessServerPanel")!;
        if (!panel.IsVisible)
            window.FindControl<Button>("BusinessServerMenuButton")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Flush(window);
        if (!panel.IsVisible) throw new InvalidOperationException("The Server menu item did not open its panel.");
    }

    internal static void OpenModePanel(this BusinessWindow window)
    {
        window.OpenBusinessMenu();
        var panel = window.FindControl<Border>("BusinessModePanel")!;
        if (!panel.IsVisible)
            window.FindControl<Button>("BusinessModeMenuButton")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Flush(window);
        if (!panel.IsVisible) throw new InvalidOperationException("The VPN mode menu item did not open its panel.");
    }

    private static void Flush(BusinessWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        window.FindControl<ScrollViewer>("BusinessMenuScroll")?.UpdateLayout();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
