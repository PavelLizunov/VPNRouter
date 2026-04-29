using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using VPNRouter.App.ViewModels;

namespace VPNRouter.App.Views.Pages;

public partial class NetworkPage : UserControl
{
    /// <summary>
    /// v2.30.0-r13 → r14: narrow-window breakpoint for the Rules section.
    /// Bumped 540 → 620 px after r13 user feedback: «при сужении и
    /// расширении value и comment накладываются друг на друга». The wide
    /// Add-form's 5-col grid (130 + 160 + 2×* + Auto button ~80 + 8×4
    /// spacing = 402 px fixed/spacing) needs at least 562 px content
    /// area for the two * cols to each have ~80 px (enough for
    /// placeholders without overlap). 620 px window width gives ~580 px
    /// content area after our padding, leaving ~178 px for the 2 stars
    /// = ~89 px each — comfortable.
    /// </summary>
    private const double NarrowBreakpoint = 620.0;

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

    /// <summary>v2.30.0-r14 — close the parent Flyout after a bulk-popover
    /// item button has fired its Command. Avalonia's <see cref="Flyout"/>
    /// doesn't auto-close on inner-Button clicks (unlike MenuFlyout +
    /// MenuItem), so we explicitly hide the named ⋯ buttons' Flyouts.
    /// The closed-already case is a no-op (Hide on a hidden flyout is
    /// safe). Two named buttons exist (wide + narrow toolbars); we hide
    /// both — only the currently-open one actually closes.</summary>
    private void OnBulkItemClick(object? sender, RoutedEventArgs e)
    {
        BulkBtnWide?.Flyout?.Hide();
        BulkBtnNarrow?.Flyout?.Hide();
    }
}
