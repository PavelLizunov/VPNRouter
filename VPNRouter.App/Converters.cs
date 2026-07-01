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

    /// <summary>
    /// v2.31.0-r4 (F-3): now accepts a `ConverterParameter` of the form
    /// "TRUE_GLYPH|FALSE_GLYPH" so each call site can pick the right
    /// orientation. Examples:
    ///   - default (no param): "▲" (true) / "▼" (false)
    ///   - parameter="▽|›":   "▽" (expanded) / "›" (collapsed) — for
    ///     side-anchored chevrons in the Simple-mode "Конфиг·Режим" card.
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var glyphs = (parameter as string)?.Split('|', 2) ?? new[] { "▲", "▼" };
        var trueGlyph = glyphs.Length > 0 ? glyphs[0] : "▲";
        var falseGlyph = glyphs.Length > 1 ? glyphs[1] : "▼";
        return value is bool b && b ? trueGlyph : falseGlyph;
    }
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

// v2.31.6-r9 — removed AppsTabVisibleConverter + EmptyCustomConverter.
// Both were defined with `Instance` singletons but no XAML reference
// anywhere across `Views/`. Iter#4 audit flagged as dead code.
