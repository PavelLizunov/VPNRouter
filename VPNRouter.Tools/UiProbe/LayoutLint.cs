using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using System.Text;
using System.Text.RegularExpressions;

namespace VPNRouter.Tools.UiProbe;

// Mechanical checks over a laid-out window. Heuristics, not proofs: each finding names the element so a person (or a model) can look.
public static class LayoutLint
{
    private const int MaxFindings = 250;

    private static readonly Regex Unformatted = new(@"\{\d+\}|\{[A-Za-z]+\}|%s|\bnull\b|\bSystem\.[A-Z]\w+|\bVPNRouter\.\w+(\.\w+)+", RegexOptions.Compiled);

    public static List<Finding> Run(Window window)
    {
        var findings = new List<Finding>();
        var seen = new HashSet<string>();

        void Add(string severity, string rule, Control c, string message)
        {
            if (findings.Count >= MaxFindings) return;
            var element = Describe.Path(c);
            if (!seen.Add(rule + "|" + element)) return;
            findings.Add(new Finding(severity, rule, element, message));
        }

        var dark = window.ActualThemeVariant == ThemeVariant.Dark;
        foreach (var visual in window.GetVisualDescendants())
        {
            if (visual is not Control c || !c.IsEffectivelyVisible) continue;
            try
            {
                Check(window, c, dark, Add);
            }
            catch (Exception ex)
            {
                Add("info", "lint-error", c, ex.GetType().Name + ": " + ex.Message);
            }
        }
        return findings;
    }

    private static void Check(Window window, Control c, bool dark, Action<string, string, Control, string> add)
    {
        var text = Describe.TextOf(c);
        var hasText = !string.IsNullOrWhiteSpace(text);
        var bounds = c.Bounds;

        if (c is TextBlock tb && hasText)
        {
            var noTrim = tb.TextTrimming == null || tb.TextTrimming == TextTrimming.None;
            if (tb.TextWrapping == TextWrapping.NoWrap && noTrim && bounds.Width > 0)
            {
                var layout = tb.TextLayout;
                if (layout.WidthIncludingTrailingWhitespace > bounds.Width + 1)
                    add("warn", "text-clipped", c,
                        $"text needs {layout.WidthIncludingTrailingWhitespace:0} px but the block is {bounds.Width:0} px wide");
            }
            if (tb.DesiredSize.Height > bounds.Height + 1 && bounds.Height > 0)
                add("warn", "text-clipped-height", c,
                    $"text needs {tb.DesiredSize.Height:0} px of height but got {bounds.Height:0}");
        }

        if (hasText && (c is TextBlock || c is ContentControl))
        {
            var glyph = FirstSymbolGlyph(text!);
            if (glyph != null)
                add("warn", "symbol-glyph", c, $"uses the text symbol U+{glyph:X4}; a vector Pictogram is expected");

            if (Unformatted.IsMatch(text!))
                add("warn", "unformatted-text", c, "text looks unformatted or shows a type name: \"" + Describe.Trim(text!, 60) + "\"");

            if (c.IsEffectivelyEnabled)
                CheckContrast(window, c, dark, add);
        }

        if (c is Button or TabItem or ListBoxItem && bounds.Width > 0 && (bounds.Height < 24 || bounds.Width < 24) && c.IsEffectivelyEnabled)
            add("info", "small-target", c, $"interactive control is only {bounds.Width:0}x{bounds.Height:0} px");

        if ((hasText || Describe.IsInteractive(c)) && bounds.Width <= 0 && bounds.Height <= 0)
            add("warn", "zero-size", c, "visible control has no size");

        if ((hasText || Describe.IsInteractive(c)) && bounds.Width > 0 && !InsideHorizontalScroll(c))
        {
            var topLeft = c.TranslatePoint(new Point(0, 0), window);
            var bottomRight = c.TranslatePoint(new Point(bounds.Width, bounds.Height), window);
            if (topLeft.HasValue && bottomRight.HasValue)
            {
                var width = window.ClientSize.Width;
                if (bottomRight.Value.X > width + 1 || topLeft.Value.X < -1)
                    add("warn", "outside-viewport", c,
                        $"spans x {topLeft.Value.X:0}..{bottomRight.Value.X:0} while the window is {width:0} px wide");
            }
        }
    }

    private static bool InsideHorizontalScroll(Control c)
    {
        foreach (var ancestor in c.GetVisualAncestors())
        {
            if (ancestor is ScrollViewer sv && sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled)
                return true;
        }
        return false;
    }

    private static int? FirstSymbolGlyph(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            if ((v >= 0x2190 && v <= 0x21FF) || (v >= 0x2300 && v <= 0x23FF) || (v >= 0x25A0 && v <= 0x25FF) ||
                (v >= 0x2600 && v <= 0x27BF) || (v >= 0x2B00 && v <= 0x2BFF) || (v >= 0x1F000 && v <= 0x1FAFF) || v == 0xFE0F)
                return v;
        }
        return null;
    }

    private static void CheckContrast(Window window, Control c, bool dark, Action<string, string, Control, string> add)
    {
        IBrush? foreground = c switch
        {
            TextBlock t => t.Foreground,
            TemplatedControl t => t.Foreground,
            _ => null,
        };
        if (foreground is not ISolidColorBrush solid) return;

        var background = EffectiveBackground(c, window, dark);
        var fg = Blend(Color.FromArgb((byte)(solid.Color.A * solid.Opacity), solid.Color.R, solid.Color.G, solid.Color.B), background);
        var ratio = Ratio(fg, background);
        var size = c is TextBlock tb ? tb.FontSize : (c as TemplatedControl)?.FontSize ?? 14;
        var needed = size >= 18.66 ? 3.0 : 4.5;
        if (ratio < needed)
            add("warn", "low-contrast", c, $"contrast {ratio:0.0}:1 is under {needed:0.0}:1 (text {fg}, background {background})");
    }

    private static Color EffectiveBackground(Control c, Window window, bool dark)
    {
        var layers = new List<Color>();
        for (Visual? v = c; v != null; v = v.GetVisualParent())
        {
            IBrush? brush = v switch
            {
                Border b => b.Background,
                Panel p => p.Background,
                TemplatedControl t => t.Background,
                _ => null,
            };
            if (brush is ISolidColorBrush s && s.Color.A > 0)
            {
                var alpha = (byte)(s.Color.A * s.Opacity);
                layers.Add(Color.FromArgb(alpha, s.Color.R, s.Color.G, s.Color.B));
                if (alpha == 255) break;
            }
        }
        var result = dark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Color.FromRgb(0xFF, 0xFF, 0xFF);
        for (var i = layers.Count - 1; i >= 0; i--) result = Blend(layers[i], result);
        return result;
    }

    private static Color Blend(Color top, Color bottom)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte b) => (byte)Math.Round(t * a + b * (1 - a));
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    private static double Luminance(Color c)
    {
        static double F(byte x)
        {
            var s = x / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
    }

    private static double Ratio(Color a, Color b)
    {
        var l1 = Luminance(a);
        var l2 = Luminance(b);
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    public static string Format(IEnumerable<Finding> findings)
    {
        var sb = new StringBuilder();
        foreach (var f in findings) sb.AppendLine($"[{f.Severity}] {f.Rule}: {f.Element} - {f.Message}");
        return sb.ToString();
    }
}
