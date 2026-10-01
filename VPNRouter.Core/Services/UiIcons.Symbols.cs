namespace VPNRouter.Core.Services;

public static partial class UiIcons
{
    // Text symbols that some shared strings carry as a button glyph ("↻ Apply", "Advanced ▸", "+ Add"). The desktop
    // UI still shows them; the Android UI draws a vector icon instead and removes the symbol from the ends of the label.
    private const string LabelSymbols = "✓✔✕✗↻⟳▶⏹⬛▾▸◂●⋮+";

    /// <summary>Removes button symbols and the spaces next to them from both ends of a label. Symbols in the middle of
    /// the text (arrows in a sentence, "Wi-Fi ↔ cellular") stay.</summary>
    public static string StripSymbols(string? label)
    {
        if (string.IsNullOrEmpty(label)) return string.Empty;
        int start = 0, end = label.Length;
        while (start < end && (char.IsWhiteSpace(label[start]) || LabelSymbols.Contains(label[start]))) start++;
        while (end > start && (char.IsWhiteSpace(label[end - 1]) || LabelSymbols.Contains(label[end - 1]))) end--;
        return label[start..end];
    }

    /// <summary>True when the name is in the registry.</summary>
    public static bool Exists(string? name) => name is not null && PathData.ContainsKey(name);
}
