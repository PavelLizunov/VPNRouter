using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace VPNRouter.App;

public class BoolToStatusColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true)
            return new SolidColorBrush(Color.FromRgb(34, 197, 94)); // green-500
        return new SolidColorBrush(Color.FromRgb(161, 161, 170));   // zinc-400
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToLangConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? "EN" : "RU";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToFontWeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToChevronConverter : IValueConverter
{
    public static readonly BoolToChevronConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? "▲" : "▼";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// v2.29.0 — full-tunnel Apps page hint. Maps <c>true</c> → 1.0,
/// <c>false</c> → 0.5. Used to dim the apps list when full-tunnel mode
/// is active without disabling it (the visible-but-faded look reads as
/// "not currently used" rather than "broken"). Together with
/// <c>IsHitTestVisible</c> on the same Grid this gives a clean
/// "selection is ignored, see banner above" affordance.
/// </summary>
public class BoolTo10or05Converter : IValueConverter
{
    public static readonly BoolTo10or05Converter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? 1.0 : 0.5;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// v2.30.3-r1 (BUG-36 fix) — converts <c>IsFullTunnel</c> bool into an
/// Avalonia <c>Cursor</c> so disabled-but-visible UI shows a "not allowed"
/// cursor on hover rather than the default arrow. Lets the user feel the
/// disabled state in addition to seeing the opacity fade.
/// </summary>
public class FullTunnelCursorConverter : IValueConverter
{
    public static readonly FullTunnelCursorConverter Instance = new();
    private static readonly Avalonia.Input.Cursor _no =
        new(Avalonia.Input.StandardCursorType.No);
    private static readonly Avalonia.Input.Cursor _arrow =
        new(Avalonia.Input.StandardCursorType.Arrow);
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? _no : _arrow;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// v2.30.0-r2 — action chip color converter for the structured rules
/// list in Network → Rules. Maps the rule action string to a colored
/// SolidColorBrush:
/// <list type="bullet">
/// <item><c>direct</c> → blue (Accent — bypass-VPN affordance)</item>
/// <item><c>proxy</c> → orange (Warning — non-default routing)</item>
/// <item><c>block</c> → red (Danger — destructive)</item>
/// </list>
/// Falls back to gray for unknown actions. Colors are hardcoded to
/// approximate the design tokens — using DynamicResource lookup from
/// inside a converter requires a control reference, which isn't worth
/// the plumbing for this small visual element.
/// </summary>
public class ActionToChipColorConverter : IValueConverter
{
    public static readonly ActionToChipColorConverter Instance = new();
    private static readonly SolidColorBrush DirectBrush = new(Color.FromRgb(0x21, 0x6E, 0xC4));   // accent
    private static readonly SolidColorBrush ProxyBrush  = new(Color.FromRgb(0xC2, 0x6F, 0x05));   // warning-orange
    private static readonly SolidColorBrush BlockBrush  = new(Color.FromRgb(0xC0, 0x33, 0x33));   // danger-red
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromRgb(0x80, 0x80, 0x80));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value as string switch
        {
            "direct" => DirectBrush,
            "proxy"  => ProxyBrush,
            "block"  => BlockBrush,
            _        => DefaultBrush,
        };
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// v2.30.0-r9 — action-chip semantic-token brush lookup. Maps an action
/// string ("direct" / "proxy" / "block") + a role parameter ("Bg" / "Fg"
/// / "Border") to the corresponding theme brush from
/// <see cref="Avalonia.Application.Current"/>'s resources.
/// <para>Used by the Cards view in Network → Rules so chips match the
/// claude.ai/design handoff exactly: light bg + semantic fg + 1px
/// matching-tone border, NOT solid dark bg + white fg (which was the
/// pre-r9 hardcoded look). Theme-aware: each role lookup respects the
/// active <see cref="Avalonia.Styling.ThemeVariant"/> automatically.</para>
/// <para>Mapping:
/// <list type="bullet">
/// <item>direct → SurfaceSunken / TextSecondary / BorderDefault</item>
/// <item>proxy → AccentBgSubtle / AccentFg / AccentBorder</item>
/// <item>block → DangerBg / DangerFg / DangerBorder</item>
/// </list>
/// </para>
/// </summary>
public class ActionToTokenBrushConverter : IValueConverter
{
    public static readonly ActionToTokenBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var action = (value as string ?? "direct").ToLowerInvariant();
        var role = parameter as string ?? "Bg";

        var key = (action, role) switch
        {
            ("direct", "Bg")     => "SurfaceSunkenBrush",
            ("direct", "Fg")     => "TextSecondaryBrush",
            ("direct", "Border") => "BorderDefaultBrush",
            ("proxy", "Bg")      => "AccentBgSubtleBrush",
            ("proxy", "Fg")      => "AccentFgBrush",
            ("proxy", "Border")  => "AccentBorderBrush",
            ("block", "Bg")      => "DangerBgBrush",
            ("block", "Fg")      => "DangerFgBrush",
            ("block", "Border")  => "DangerBorderBrush",
            _ => (string?)null,
        };
        if (key == null) return Avalonia.AvaloniaProperty.UnsetValue;

        var app = Avalonia.Application.Current;
        if (app != null
            && app.TryGetResource(key, app.ActualThemeVariant, out var res)
            && res is IBrush brush)
            return brush;
        return Avalonia.AvaloniaProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// v2.30.0-r7 — segmented-toggle background/foreground swap for the
/// Cards / Edit view-mode selector in Network → Rules. Takes a bool
/// (IsActive) and a <c>ConverterParameter</c> of the form
/// "<c>ActiveResourceKey|InactiveResourceKey</c>" and returns the
/// corresponding theme brush from <see cref="Avalonia.Application.Current"/>'s
/// resources. The reserved key <c>"Transparent"</c> bypasses the resource
/// lookup and returns <see cref="Brushes.Transparent"/> directly.
/// <para>This avoids two-button-with-IsVisible duplication or the
/// MultiBinding-with-trigger-pattern boilerplate. Theme-aware via
/// <see cref="Avalonia.Styling.ThemeVariant.Default"/> — Avalonia
/// resolves the active variant automatically.</para>
/// </summary>
public class BoolToBrushConverter : IValueConverter
{
    public static readonly BoolToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isTrue = value is bool b && b;
        var keys = (parameter as string ?? string.Empty).Split('|');
        if (keys.Length != 2) return Avalonia.AvaloniaProperty.UnsetValue;
        var key = isTrue ? keys[0] : keys[1];
        if (string.Equals(key, "Transparent", StringComparison.Ordinal))
            return Brushes.Transparent;
        var app = Avalonia.Application.Current;
        if (app != null
            && app.TryGetResource(key, app.ActualThemeVariant, out var res)
            && res is IBrush brush)
            return brush;
        return Avalonia.AvaloniaProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class AppsTabVisibleConverter : IMultiValueConverter
{
    public static readonly AppsTabVisibleConverter Instance = new();
    public object? Convert(System.Collections.Generic.IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return false;
        var idx = values[0] is int i ? i : -1;
        var split = values[1] is bool b && b;
        return idx == 2 && split;
    }
}

public class EmptyCustomConverter : IMultiValueConverter
{
    public static readonly EmptyCustomConverter Instance = new();
    public object? Convert(System.Collections.Generic.IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 3) return false;
        var isCustom = values[0] is bool b && b;
        var count = values[1] is int i ? i : 0;
        var expanded = values[2] is bool e && e;
        return isCustom && count == 0 && expanded;
    }
}
