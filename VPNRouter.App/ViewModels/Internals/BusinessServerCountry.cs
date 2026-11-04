using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace VPNRouter.App.ViewModels.Internals;

// Provider-advertised country labels, not a lookup of private endpoints.
internal static class BusinessServerCountry
{
    private static readonly (string Code, string[] Labels)[] Countries =
    [
        ("DE", ["Germany", "Германия", "DE"]), ("IS", ["Iceland", "Исландия", "IS"]),
        ("NL", ["Netherlands", "Нидерланды", "NL"]), ("FI", ["Finland", "Финляндия", "FI"]),
        ("SE", ["Sweden", "Швеция", "SE"]), ("FR", ["France", "Франция", "FR"]),
        ("GB", ["United Kingdom", "Великобритания", "GB"]), ("US", ["United States", "США", "US"])
    ];
    // Eight small application-lifetime images; refreshing rows does not allocate bitmaps.
    private static readonly Dictionary<string, IImage> Flags = new();

    internal static string Code(string name)
    {
        name = name.Trim();
        foreach (var (code, labels) in Countries)
            foreach (var label in labels)
                if (name.Equals(label, StringComparison.OrdinalIgnoreCase) ||
                    new[] { " ", "-", "_" }.Any(separator => name.StartsWith(label + separator, StringComparison.OrdinalIgnoreCase)))
                    return code;
        return string.Empty;
    }

    internal static IImage? Flag(string code)
    {
        if (code.Length == 0) return null;
        if (!Flags.TryGetValue(code, out var image))
        {
            using var stream = AssetLoader.Open(new Uri($"avares://VPNRouter.App/Assets/Flags/{code}.png"));
            Flags[code] = image = new Bitmap(stream);
        }
        return image;
    }
}
