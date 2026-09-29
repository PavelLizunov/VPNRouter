using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using VPNRouter.App.ViewModels;

namespace VPNRouter.App.Views.Pages;

public partial class NetworkPage : UserControl
{
    private const double NarrowBreakpoint = 700.0;

    public NetworkPage()
    {
        InitializeComponent();
        SizeChanged += OnPageSizeChanged;
        AttachedToVisualTree += (_, _) => UpdateNarrowState();
    }

    private void OnPageSizeChanged(object? sender, Avalonia.Controls.SizeChangedEventArgs e)
    {
        UpdateNarrowState();
    }

    private void UpdateNarrowState()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var width = Bounds.Width;
        if (width <= 0) return;
        vm.IsRulesNarrow = width < NarrowBreakpoint;
    }

    // Close the flyout after the button Command runs: Avalonia raises Click before executing Command.
    private void OnBulkItemClick(object? sender, RoutedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            BulkBtnWide?.Flyout?.Hide();
            BulkBtnNarrow?.Flyout?.Hide();
        });
    }
}
