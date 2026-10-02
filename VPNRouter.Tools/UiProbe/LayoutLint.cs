using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
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
        var groups = new Dictionary<string, (int Index, int Count)>();

        // Findings that share a rule and a cause (the same colours, the same glyph, the same size) are listed once with a count.
        void Add(string severity, string rule, Control c, string message, string? group = null)
        {
            var element = Describe.Path(c);
            if (group != null)
            {
                var key = rule + "|" + group;
                if (groups.TryGetValue(key, out var existing))
                {
                    groups[key] = (existing.Index, existing.Count + 1);
                    var first = findings[existing.Index];
                    var baseMessage = first.Message.Contains(" (+") ? first.Message[..first.Message.IndexOf(" (+", StringComparison.Ordinal)] : first.Message;
                    findings[existing.Index] = first with { Message = baseMessage + $" (+{existing.Count} more like it)" };
                    return;
                }
                if (findings.Count >= MaxFindings) return;
                groups[key] = (findings.Count, 1);
                findings.Add(new Finding(severity, rule, element, message));
                return;
            }
            if (findings.Count >= MaxFindings) return;
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
        try
        {
            CheckWindowLevel(window, Add);
        }
        catch (Exception ex)
        {
            findings.Add(new Finding("info", "lint-error", "(window)", ex.GetType().Name + ": " + ex.Message));
        }
        return findings;
    }

    private static Rect? RectIn(Window window, Control c)
    {
        var origin = c.TranslatePoint(new Point(0, 0), window);
        return origin.HasValue ? new Rect(origin.Value, c.Bounds.Size) : null;
    }

    private static bool EffectivelyTransparent(Control c)
    {
        for (Visual? v = c; v != null; v = v.GetVisualParent())
            if (v.Opacity < 0.05) return true;
        return false;
    }

    // Checks that look at the laid-out window as a whole: what is cut off by a container, what overlaps, what touches the edge, uneven card insets.
    private static void CheckWindowLevel(Window window, Action<string, string, Control, string, string?> add)
    {
        var width = window.ClientSize.Width;
        var visible = window.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0 && !EffectivelyTransparent(c))
            .ToList();

        var atoms = new List<(Control Control, Rect Rect)>();
        foreach (var c in visible)
        {
            // A placeholder is clipped by its own text box on purpose.
            if (c.Name is "PART_Placeholder" or "PART_Watermark") continue;
            var isAtom = (c is TextBlock && !string.IsNullOrWhiteSpace(Describe.TextOf(c))) || c is TextBox;
            if (!isAtom) continue;
            var rect = RectIn(window, c);
            if (rect.HasValue) atoms.Add((c, rect.Value));
        }

        // 1. Cut off by a container (a clipping panel, or a scroll viewer that does not scroll sideways).
        foreach (var (c, rect) in atoms.Concat(visible.Where(Describe.IsInteractive).Select(c => (c, RectIn(window, c) ?? default))))
        {
            if (rect.Width <= 0) continue;
            foreach (var ancestor in c.GetVisualAncestors().OfType<Control>())
            {
                bool horizontalOnly;
                if (ancestor is ScrollViewer sv)
                {
                    if (sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled) continue;
                    horizontalOnly = true;
                }
                else if (ancestor.ClipToBounds)
                {
                    horizontalOnly = false;
                }
                else
                {
                    continue;
                }

                var box = RectIn(window, ancestor);
                if (!box.HasValue || box.Value.Width <= 0) continue;
                var left = box.Value.Left - rect.Left;
                var right = rect.Right - box.Value.Right;
                var top = box.Value.Top - rect.Top;
                var bottom = rect.Bottom - box.Value.Bottom;
                var cut = Math.Max(Math.Max(left, right), horizontalOnly ? 0 : Math.Max(top, bottom));
                if (cut > 1.5)
                {
                    add("warn", "cut-off", c,
                        $"cut off by {ancestor.GetType().Name}{(string.IsNullOrEmpty(ancestor.Name) ? string.Empty : "#" + ancestor.Name)} by {cut:0} px",
                        $"{ancestor.GetType().Name}|{(int)(cut / 4)}");
                    break;
                }
            }
        }

        // 2. Two pieces of text (or a text box) on top of each other.
        for (var i = 0; i < atoms.Count; i++)
        {
            for (var j = i + 1; j < atoms.Count; j++)
            {
                var (a, ra) = atoms[i];
                var (b, rb) = atoms[j];
                var inter = ra.Intersect(rb);
                if (inter.Width <= 1 || inter.Height <= 1) continue;
                var smaller = Math.Min(ra.Width * ra.Height, rb.Width * rb.Height);
                if (smaller <= 0 || inter.Width * inter.Height < smaller * 0.25) continue;
                if (a.IsVisualAncestorOf(b) || b.IsVisualAncestorOf(a)) continue;
                add("warn", "overlap", a, $"overlaps \"{Describe.Trim(Describe.TextOf(b) ?? b.GetType().Name, 30)}\" by {inter.Width * inter.Height / smaller:P0}", null);
            }
        }

        // 3. Text glued to the edge of the window.
        foreach (var (c, rect) in atoms)
        {
            if (width > 300 && (rect.Left < 6 || rect.Right > width - 6) && c is TextBlock)
                add("info", "text-at-edge", c, $"text spans x {rect.Left:0}..{rect.Right:0} of a {width:0} px window", null);
        }

        // 4. Cards that are not centred between the window edges (flush ones, like a side pane, are on purpose).
        foreach (var border in visible.OfType<Border>())
        {
            if (border.Background == null && border.BorderBrush == null) continue;
            var rect = RectIn(window, border);
            if (!rect.HasValue || rect.Value.Width < width * 0.5) continue;
            var gapLeft = rect.Value.Left;
            var gapRight = width - rect.Value.Right;
            if (gapLeft >= 3 && gapRight >= 3 && Math.Abs(gapLeft - gapRight) > 3)
                add("info", "uneven-gutter", border, $"card inset is {gapLeft:0} px on the left and {gapRight:0} px on the right", null);
        }
    }

    private static void Check(Window window, Control c, bool dark, Action<string, string, Control, string, string?> add)
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
                        $"text needs {layout.WidthIncludingTrailingWhitespace:0} px but the block is {bounds.Width:0} px wide", null);
            }
            // DesiredSize includes the margin, which the bounds do not: compare the laid-out text itself.
            var neededHeight = tb.TextLayout.Height + tb.Padding.Top + tb.Padding.Bottom;
            if (neededHeight > bounds.Height + 1 && bounds.Height > 0)
                add("warn", "text-clipped-height", c,
                    $"text needs {neededHeight:0} px of height but got {bounds.Height:0}", null);
        }

        if (hasText && (c is TextBlock || c is ContentControl))
        {
            var glyph = FirstSymbolGlyph(text!);
            if (glyph != null)
                add("warn", "symbol-glyph", c, $"uses the text symbol U+{glyph:X4}; a vector Pictogram is expected", $"{glyph:X4}");

            if (Unformatted.IsMatch(text!))
                add("warn", "unformatted-text", c, "text looks unformatted or shows a type name: \"" + Describe.Trim(text!, 60) + "\"", null);

            if (c.IsEffectivelyEnabled)
                CheckContrast(window, c, dark, add);
        }

        if (c is Button or TabItem or ListBoxItem && bounds.Width > 0 && (bounds.Height < 24 || bounds.Width < 24) && c.IsEffectivelyEnabled)
            add("info", "small-target", c, $"interactive control is only {bounds.Width:0}x{bounds.Height:0} px", $"{c.GetType().Name}{bounds.Width:0}x{bounds.Height:0}");

        if ((hasText || Describe.IsInteractive(c)) && bounds.Width <= 0 && bounds.Height <= 0)
            add("warn", "zero-size", c, "visible control has no size", null);

        if ((hasText || Describe.IsInteractive(c)) && bounds.Width > 0 && !InsideHorizontalScroll(c))
        {
            var topLeft = c.TranslatePoint(new Point(0, 0), window);
            var bottomRight = c.TranslatePoint(new Point(bounds.Width, bounds.Height), window);
            if (topLeft.HasValue && bottomRight.HasValue)
            {
                var width = window.ClientSize.Width;
                if (bottomRight.Value.X > width + 1 || topLeft.Value.X < -1)
                    add("warn", "outside-viewport", c,
                        $"spans x {topLeft.Value.X:0}..{bottomRight.Value.X:0} while the window is {width:0} px wide", null);
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

    private static void CheckContrast(Window window, Control c, bool dark, Action<string, string, Control, string, string?> add)
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
            add("warn", "low-contrast", c, $"contrast {ratio:0.0}:1 is under {needed:0.0}:1 (text {fg}, background {background})", $"{fg}|{background}");
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
                ContentPresenter cp => cp.Background,
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
