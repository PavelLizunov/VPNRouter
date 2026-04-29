using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using VPNRouter.App.ViewModels;

namespace VPNRouter.App.Views.Pages;

public partial class NetworkPage : UserControl
{
    /// <summary>
    /// v2.30.0-r13 → r14 → r15 → r16: narrow-window breakpoint for the
    /// Rules section.
    /// Bumped to 700 px after r15 user feedback: «слово add всё равно
    /// вылазиет за обводку». r15 used 660 px + Auto button column;
    /// at certain mid-widths the Auto col couldn't grow large enough
    /// for the «+ Добавить» content, button text overflowed past its
    /// own column → past Border edge.
    /// r16 fix: button col fixed 100 px (always fits content), button
    /// padding 14,5 → 8,5, Border ClipToBounds=True, breakpoint 700.
    /// New math: 130 + 160 + 8×4 + 100(button) + 2×60(MinWidth) = 542.
    /// + 28 padding = 570 px container minimum. 700 px breakpoint
    /// leaves a 130-px buffer — accommodates ScrollViewer scrollbars
    /// and any layout-pass jitter during fast resize.
    /// </summary>
    private const double NarrowBreakpoint = 700.0;

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
