using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace VPNRouter.App.ViewModels.Internals;

internal static class ServerCountryFlags
{
    private static readonly (string Code, string[] Labels)[] Countries =
    [
        ("DE", ["Germany", "Германия", "DE"]), ("IS", ["Iceland", "Исландия", "IS"]),
        ("NL", ["Netherlands", "Нидерланды", "NL"]), ("FI", ["Finland", "Финляндия", "FI"]),
        ("SE", ["Sweden", "Швеция", "SE"]), ("FR", ["France", "Франция", "FR"]),
        ("GB", ["United Kingdom", "Великобритания", "GB", "UK"]), ("US", ["United States", "США", "US"]),
        ("CH", ["Switzerland", "Швейцария", "CH"]), ("RO", ["Romania", "Румыния", "RO"]),
        ("IT", ["Italy", "Италия", "IT"]), ("AL", ["Albania", "Албания", "AL"]),
    ];

    private static readonly Dictionary<string, IImage> Flags = new(StringComparer.Ordinal);

    internal static string Code(string? name)
    {
        name = name?.TrimStart() ?? string.Empty;
        var indicator = LeadingIndicatorCode(name);
        if (indicator != null) return indicator;

        foreach (var (code, labels) in Countries)
            foreach (var label in labels)
                if (name.StartsWith(label, StringComparison.OrdinalIgnoreCase) &&
                    (name.Length == label.Length || name[label.Length] is ' ' or '-' or '_'))
                    return code;
        return string.Empty;
    }

    internal static string DisplayName(string name)
    {
        var label = name.TrimStart();
        if (LeadingIndicatorCode(label) == null) return name;
        var remainder = label[4..].TrimStart();
        return remainder.Length > 0 ? remainder : name;
    }

    internal static bool MatchesCountry(string code, string query)
    {
        if (code.Length == 0) return false;
        if (code.Equals(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (Countries.Any(country => country.Code == code &&
            country.Labels.Any(label => label.Contains(query, StringComparison.OrdinalIgnoreCase))))
            return true;

        try
        {
            var region = new RegionInfo(code);
            return region.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                region.EnglishName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                region.NativeName.Contains(query, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    private static string? LeadingIndicatorCode(string name)
    {
        const int firstIndicator = 0x1F1E6;
        if (name.Length < 4 || !char.IsSurrogatePair(name, 0) || !char.IsSurrogatePair(name, 2)) return null;
        var first = char.ConvertToUtf32(name, 0) - firstIndicator;
        var second = char.ConvertToUtf32(name, 2) - firstIndicator;
        return first is >= 0 and < 26 && second is >= 0 and < 26
            ? string.Concat((char)('A' + first), (char)('A' + second))
            : null;
    }

    internal static IImage? Flag(string code)
    {
        if (code.Length != 2 || !code.All(char.IsAsciiLetter)) return null;
        code = code.ToUpperInvariant();
        if (Flags.TryGetValue(code, out var image)) return image;

        var uri = new Uri($"avares://VPNRouter.App/Assets/Flags/ISO/{code}.png");
        if (!AssetLoader.Exists(uri)) return null;
        using var stream = AssetLoader.Open(uri);
        return Flags[code] = new Bitmap(stream);
    }
}
