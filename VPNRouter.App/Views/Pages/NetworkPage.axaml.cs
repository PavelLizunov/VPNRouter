using Avalonia.Controls;
using VPNRouter.App.ViewModels;

namespace VPNRouter.App.Views.Pages;

public partial class NetworkPage : UserControl
{
    /// <summary>
    /// v2.30.0-r13 — narrow-window breakpoint for the Rules section.
    /// 540 px chosen because the wide Add-form needs ~520 px just for its
    /// 5-col grid (130 + 160 + 2×* + Auto button + spacing); 540 gives
    /// a small comfort margin before columns crush each other.
    /// User report 2026-04-29: «Снова при сужении друг на друга наезжают».
    /// </summary>
    private const double NarrowBreakpoint = 540.0;

    public NetworkPage()
    {
        InitializeComponent();
        // Tie the narrow-state flag to actual rendered width. Avalonia
        // doesn't have container-query equivalents, so we drive the
        // IsRulesNarrow VM property from a code-behind size handler.
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
}
