using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace VPNRouter.App.ViewModels.Internals;

// Known flag assets and explicit provider labels. Trusted node metadata can supply
// country codes independently; an unsupported code must never open a missing asset.
internal static class BusinessServerCountry
{
    private static readonly (string Code, string[] Labels)[] Countries =
    [
        ("DE", ["Germany", "Германия", "DE"]), ("IS", ["Iceland", "Исландия", "IS"]),
        ("NL", ["Netherlands", "Нидерланды", "NL"]), ("FI", ["Finland", "Финляндия", "FI"]),
        ("SE", ["Sweden", "Швеция", "SE"]), ("FR", ["France", "Франция", "FR"]),
        ("GB", ["United Kingdom", "Великобритания", "GB"]), ("US", ["United States", "США", "US"]),
        ("CH", ["Switzerland", "Швейцария", "CH"])
    ];
    // Application-lifetime images; refreshing rows does not allocate bitmaps.
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
        if (!Countries.Any(country => country.Code == code)) return null;
        if (!Flags.TryGetValue(code, out var image))
        {
            if (code == "CH")
            {
                image = new DrawingImage
                {
                    Drawing = new DrawingGroup
                    {
                        Children =
                        {
                            new GeometryDrawing { Brush = Brushes.Red, Geometry = Geometry.Parse("M0,0 L16,0 L16,16 L0,16 Z") },
                            new GeometryDrawing { Brush = Brushes.White, Geometry = Geometry.Parse("M6,3 L10,3 L10,6 L13,6 L13,10 L10,10 L10,13 L6,13 L6,10 L3,10 L3,6 L6,6 Z") }
                        }
                    }
                };
                Flags[code] = image;
                return image;
            }
            using var stream = AssetLoader.Open(new Uri($"avares://VPNRouter.App/Assets/Flags/{code}.png"));
            Flags[code] = image = new Bitmap(stream);
        }
        return image;
    }
}
