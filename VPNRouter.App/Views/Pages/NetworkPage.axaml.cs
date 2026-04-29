using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using VPNRouter.App.ViewModels;

namespace VPNRouter.App.Views.Pages;

public partial class NetworkPage : UserControl
{
    /// <summary>
    /// v2.30.0-r13 → r14 → r15: narrow-window breakpoint for the Rules
    /// section.
    /// Bumped to 660 px after r14 user feedback: «теперь кнопки не
    /// залазиют друг на друга а вылазиют за экран». r14 used 620 px +
    /// MinWidth=80 on each * col, but at container widths between
    /// 580–620 (when SizeChanged hadn't yet promoted to narrow) the
    /// MinWidths forced the wide grid to its minimum 560 + padding ≈
    /// 590 px = visible right-edge overflow.
    /// r15: relax MinWidth 80 → 60, raise breakpoint 620 → 660. New
    /// math: wide form fixed = 130 + 160 + 8×4 + button ~80 = 402.
    /// Plus 2 × MinWidth(60) = 120. Total ≈ 522 px content. With
    /// Border padding 14×2 = 28, container ≥ 550 px renders cleanly.
    /// 660 breakpoint gives 110 px buffer — overflow-proof.
    /// </summary>
    private const double NarrowBreakpoint = 660.0;

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
