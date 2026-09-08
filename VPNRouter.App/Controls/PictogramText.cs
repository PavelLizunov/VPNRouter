using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace VPNRouter.App.Controls;

/// <summary>Opt-in presentation of catalog symbols at known UI consumers only.
/// Shared localization, numeric values, commands and stored user text remain unchanged.</summary>
public sealed class PictogramText : AvaloniaObject
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<PictogramText, TextBlock, string?>("Text");
    public static readonly AttachedProperty<string?> ContentProperty =
        AvaloniaProperty.RegisterAttached<PictogramText, ContentControl, string?>("Content");
    public static readonly AttachedProperty<string?> TipProperty =
        AvaloniaProperty.RegisterAttached<PictogramText, Control, string?>("Tip");
    public static readonly AttachedProperty<string?> IconsProperty =
        AvaloniaProperty.RegisterAttached<PictogramText, Control, string?>("Icons", inherits: true);
    public static readonly AttachedProperty<bool> PrefixOnlyProperty =
        AvaloniaProperty.RegisterAttached<PictogramText, Control, bool>("PrefixOnly", inherits: true);

    public static string? GetText(TextBlock control) => control.GetValue(TextProperty);
    public static void SetText(TextBlock control, string? value) => control.SetValue(TextProperty, value);
    public static string? GetContent(ContentControl control) => control.GetValue(ContentProperty);
    public static void SetContent(ContentControl control, string? value) => control.SetValue(ContentProperty, value);
    public static string? GetTip(Control control) => control.GetValue(TipProperty);
    public static void SetTip(Control control, string? value) => control.SetValue(TipProperty, value);
    public static string? GetIcons(Control control) => control.GetValue(IconsProperty);
    public static void SetIcons(Control control, string? value) => control.SetValue(IconsProperty, value);
    public static bool GetPrefixOnly(Control control) => control.GetValue(PrefixOnlyProperty);
    public static void SetPrefixOnly(Control control, bool value) => control.SetValue(PrefixOnlyProperty, value);

    static PictogramText()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((control, _) =>
        {
            if (!control.IsSet(AutomationProperties.NameProperty))
                control.Bind(AutomationProperties.NameProperty, control.GetObservable(TextProperty));
            Update(control);
        });
        ContentProperty.Changed.AddClassHandler<ContentControl>((control, _) =>
        {
            if (!control.IsSet(AutomationProperties.NameProperty))
                control.Bind(AutomationProperties.NameProperty, control.GetObservable(ContentProperty));
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            SetText(text, GetContent(control));
            control.Content = text;
        });
        TipProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            var text = new TextBlock { MaxWidth = 360, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            SetIcons(text, "retest,check,warning,error,untested,timer");
            SetText(text, GetTip(control));
            ToolTip.SetTip(control, text);
        });
        IconsProperty.Changed.AddClassHandler<TextBlock>((control, _) => Update(control));
        PrefixOnlyProperty.Changed.AddClassHandler<TextBlock>((control, _) => Update(control));
        TextBlock.FontSizeProperty.Changed.AddClassHandler<TextBlock>((control, _) => Update(control));
    }

    // Semantic ids are explicit per consumer. Equal old glyphs (delete/close,
    // refresh/apply/retest, search/play) must never select semantics globally.
    private static readonly IReadOnlyDictionary<string, string[]> Symbols = new Dictionary<string, string[]>
    {
        ["add"] = ["+"], ["add-circle"] = ["\u2295"], ["apply"] = ["\u21bb"],
        ["arrow-down"] = ["\u2193"], ["arrow-up"] = ["\u2191"], ["arrow-left"] = ["\u2190"],
        ["arrow-right"] = ["\u2192"], ["blocked"] = ["\u26d4"], ["check"] = ["\u2713"],
        ["chevron-down"] = ["\u25bd", "\u25be"], ["chevron-left"] = ["\u25c2"],
        ["chevron-right"] = ["\u203a", "\u25b8"], ["close"] = ["\u2715", "X"],
        ["delete"] = ["\u2715"], ["error"] = ["\u2717"], ["find-working"] = ["\u2713\u2713"],
        ["flag"] = ["\u2691"], ["globe"] = ["\U0001f310"], ["info"] = ["\u24d8"],
        ["more-horizontal"] = ["\u22ef"], ["play"] = ["\u25b6"],
        ["refresh"] = ["\u21bb"], ["remove-circle"] = ["\u229d"],
        ["retest"] = ["\u27f3", "\u21bb"], ["search-tab"] = ["\u25b6"],
        ["shield"] = ["\U0001f6e1"], ["sort"] = ["\u2195"],
        ["status"] = ["\u25cf"], ["status-off"] = ["\u25cb"],
        ["stop"] = ["\u23f9", "\u2b1b"], ["timer"] = ["\u23f1"],
        ["untested"] = ["\u25cc"], ["verified"] = ["\u2713\u2713"],
        ["warning"] = ["\u26a0", "!"]
    };

    private static void Update(TextBlock control)
    {
        if (!control.IsSet(TextProperty)) return;
        var source = GetText(control) ?? string.Empty;
        var allowed = (GetIcons(control) ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var inlines = new InlineCollection();
        var start = 0;
        for (var i = 0; i < source.Length; i++)
        {
            string? id = null;
            var length = 0;
            // Connected(mode, name, ip) has one structural arrow after the
            // closing mode bracket. Never scan the subsequent server name.
            var connectionArrow = allowed.Contains("arrow-right") &&
                (source.StartsWith("Connected [", StringComparison.Ordinal) ||
                 source.StartsWith("Подключено [", StringComparison.Ordinal)) &&
                i == source.IndexOf("] → ", StringComparison.Ordinal) + 2 && i > 1;
            // FcStatusDeepVerifyDone is a generated count-only summary;
            // its trailing verification marker is not user-controlled content.
            var verifiedSummary = allowed.Contains("verified") &&
                (source.StartsWith("Готово: найдено ", StringComparison.Ordinal) ||
                 source.StartsWith("Done: ", StringComparison.Ordinal)) &&
                source.EndsWith("(\u2713\u2713)", StringComparison.Ordinal) && i == source.Length - 3;
            if (!GetPrefixOnly(control) || i == 0 || connectionArrow || verifiedSummary)
                foreach (var candidate in allowed)
                    if (Symbols.TryGetValue(candidate, out var symbols))
                        foreach (var symbol in symbols)
                            if (symbol.Length > length && source.AsSpan(i).StartsWith(symbol, StringComparison.Ordinal))
                            {
                                id = candidate;
                                length = symbol.Length;
                            }
            if (id is null) continue;
            if (i > start) inlines.Add(new Run(source[start..i]));
            var icon = new Pictogram { Id = id, Width = control.FontSize, Height = control.FontSize };
            // Explicit observable binding also works before inline logical attachment;
            // theme and status class changes repaint without rebuilding geometry.
            icon.Bind(Pictogram.ForegroundProperty, control.GetObservable(TextBlock.ForegroundProperty));
            inlines.Add(new InlineUIContainer { Child = icon, BaselineAlignment = BaselineAlignment.Center });
            i += length - 1;
            if (i + 1 < source.Length && source[i + 1] == '\ufe0f') i++;
            start = i + 1;
        }
        if (start < source.Length) inlines.Add(new Run(source[start..]));
        control.Inlines = inlines;
    }
}
