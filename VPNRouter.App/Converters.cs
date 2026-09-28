using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace VPNRouter.App;

public class BoolToStatusColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true)
            return new SolidColorBrush(Color.FromRgb(34, 197, 94));
        return new SolidColorBrush(Color.FromRgb(161, 161, 170));
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
    {
        var glyphs = (parameter as string)?.Split('|', 2) ?? new[] { "▲", "▼" };
        var trueGlyph = glyphs.Length > 0 ? glyphs[0] : "▲";
        var falseGlyph = glyphs.Length > 1 ? glyphs[1] : "▼";
        return value is bool b && b ? trueGlyph : falseGlyph;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolTo10or05Converter : IValueConverter
{
    public static readonly BoolTo10or05Converter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? 1.0 : 0.5;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

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
