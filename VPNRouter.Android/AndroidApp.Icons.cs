using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using VPNRouter.Core.Services;
using VPNRouter.UI.Controls;

namespace VPNRouter.Android;

// Icon helpers for the Android UI (U5). Icons come from UiIcons (Lucide); see Controls/IconView.cs.
public partial class AndroidApp
{
    /// <summary>Icon-only content for a button with a fixed hit area; the icon takes the button's foreground.</summary>
    private static IconView IconContent(string icon, double size = 20) => new(icon, size);

    /// <summary>Icon plus label for a button. Edge symbols that shared strings keep for the desktop are removed.</summary>
    private static IconLabel IconText(string icon, string? label, double size = 16, bool iconAfter = false) =>
        new(icon, UiIcons.StripSymbols(label), UiScale.Ic(size), iconAfter);

    /// <summary>
    /// Puts a status icon in front of a text line. The icon takes the text colour, the row takes the text's margin and
    /// hides with the text, and <see cref="SetStatusIcon"/> picks or hides the icon. Centred rows do not wrap; left
    /// rows wrap the text.
    /// </summary>
    private static Control WithStatusIcon(TextBlock text, out IconView icon, bool centered = false)
    {
        icon = new IconView
        {
            Size = UiScale.Ic(14),
            IsVisible = false,
            VerticalAlignment = centered ? VerticalAlignment.Center : VerticalAlignment.Top,
            Margin = centered ? new Thickness(0) : new Thickness(0, 2, 0, 0),
        };
        icon.Bind(IconView.ForegroundProperty, text.GetObservable(TextBlock.ForegroundProperty));
        Panel row;
        if (centered)
        {
            row = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { icon, text },
            };
        }
        else
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6 };
            Grid.SetColumn(text, 1);
            grid.Children.Add(icon);
            grid.Children.Add(text);
            row = grid;
        }
        row.Margin = text.Margin;
        text.Margin = default;
        row.Bind(Visual.IsVisibleProperty, text.GetObservable(Visual.IsVisibleProperty));
        return row;
    }

    /// <summary>A chevron-right icon that points down when its section is open.</summary>
    private static void SetChevronOpen(IconView? chevron, bool open)
    {
        if (chevron is null) return;
        chevron.RenderTransform = new Avalonia.Media.RotateTransform(open ? 90 : 0);
    }

    private static void SetStatusIcon(IconView? icon, string? name)
    {
        if (icon is null) return;
        icon.Icon = name;
        icon.IsVisible = name is not null;
    }
}
