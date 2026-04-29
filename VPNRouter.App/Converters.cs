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
