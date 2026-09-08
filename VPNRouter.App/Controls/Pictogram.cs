using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace VPNRouter.App.Controls;

/// <summary>Accepted UI catalog geometry, drawn in its original 24-unit viewbox.
/// Not an application/brand icon. Foreground inherits the surrounding text token.</summary>
public sealed class Pictogram : Control
{
    public static readonly StyledProperty<string?> IdProperty =
        AvaloniaProperty.Register<Pictogram, string?>(nameof(Id));
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Pictogram>();

    public string? Id { get => GetValue(IdProperty); set => SetValue(IdProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    static Pictogram() => AffectsRender<Pictogram>(IdProperty, ForegroundProperty);
    public Pictogram() { IsHitTestVisible = false; Focusable = false; }
    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Id is null || !Shapes.TryGetValue(Id, out var parts)) return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        if (scale <= 0) return;
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale) *
            Matrix.CreateTranslation((Bounds.Width - 24 * scale) / 2, (Bounds.Height - 24 * scale) / 2));
        foreach (var part in parts)
            context.DrawGeometry(part.Filled ? Foreground : null, part.Filled ? null : new Pen
            {
                Brush = Foreground, Thickness = 1.8, LineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
                DashStyle = part.Dashed ? new DashStyle(new double[] { 2 / 1.8, 4 / 1.8 }, 0) : null
            }, part.Geometry);
    }

    private sealed record Part(Geometry Geometry, bool Filled = false, bool Dashed = false);
    private static Part P(string data) => new(Geometry.Parse(data));
    private static Part C(double x = 12, double y = 12, double radius = 9, bool fill = false, bool dash = false)
        => new(new EllipseGeometry(new Rect(x - radius, y - radius, radius * 2, radius * 2)), fill, dash);

    // Transcribed from opendesign/mockups/ui-icon-catalog/proposed/*.svg.
    // Preserve per-part fill/stroke and SVG dash lengths, not just path bounds.
    private static readonly IReadOnlyDictionary<string, Part[]> Shapes = new Dictionary<string, Part[]>
    {
        ["add"] = [P("M12 5v14M5 12h14")],
        ["add-circle"] = [C(), P("M12 8v8m-4-4h8")],
        ["apply"] = [P("M20 9a8 8 0 1 0 0 6M20 4v5h-5m-7 3 3 3 5-6")],
        ["arrow-down"] = [P("M12 4v16m-6-6 6 6 6-6")],
        ["arrow-left"] = [P("M20 12H4m6-6-6 6 6 6")],
        ["arrow-right"] = [P("M4 12h16m-6-6 6 6-6 6")],
        ["arrow-up"] = [P("M12 20V4m-6 6 6-6 6 6")],
        ["blocked"] = [C(), P("m6 6 12 12")],
        ["check"] = [P("m4 12 5 5L20 6")],
        ["chevron-down"] = [P("m5 9 7 7 7-7")],
        ["chevron-left"] = [P("m15 5-7 7 7 7")],
        ["chevron-right"] = [P("m9 5 7 7-7 7")],
        ["close"] = [P("m6 6 12 12M18 6 6 18")],
        ["delete"] = [P("M4 6h16M9 6V3h6v3M6 6l1 15h10l1-15M10 10v7M14 10v7")],
        ["error"] = [C(), P("m9 9 6 6m0-6-6 6")],
        ["find-working"] = [C(10, 10, 7), P("m15 15 6 6M6 10l3 3 5-6")],
        ["flag"] = [P("M5 21V3m0 1c5-4 9 4 14 0v10c-5 4-9-4-14 0")],
        ["globe"] = [C(), new(new EllipseGeometry(new Rect(8, 3, 8, 18))), P("M3 12h18")],
        ["info"] = [C(), P("M12 11v6m0-10h.01")],
        ["more-horizontal"] = [C(5, 12, 1), C(12, 12, 1), C(19, 12, 1)],
        ["play"] = [P("m8 4 12 8-12 8Z")],
        ["radio-off"] = [C()],
        ["radio-on"] = [C(), C(radius: 4, fill: true)],
        ["refresh"] = [P("M20 10a8 8 0 0 0-14-5L3 8m0-5v5h5M4 14a8 8 0 0 0 14 5l3-3m0 5v-5h-5")],
        ["remove-circle"] = [C(), P("M8 12h8")],
        ["retest"] = [P("M20 9a8 8 0 1 0 0 6M20 4v5h-5m-5 0 5 3-5 3Z")],
        ["search-tab"] = [C(10, 10, 7), P("m15 15 6 6")],
        ["shield"] = [P("m12 3 8 3v6c0 4-4 7-8 9-4-2-8-5-8-9V6l8-3Z")],
        ["shield-bolt"] = [P("m12 3 8 3v6c0 4-4 7-8 9-4-2-8-5-8-9V6l8-3Zm1 4-4 6h3l-1 4 4-6h-3l1-4Z")],
        ["sort"] = [P("M8 20V4m-4 4 4-4 4 4M16 4v16m-4-4 4 4 4-4")],
        ["status"] = [C(radius: 6, fill: true)],
        ["status-off"] = [C(radius: 6)],
        ["switch"] = [P("M8 6H16A6 6 0 0 1 22 12A6 6 0 0 1 16 18H8A6 6 0 0 1 2 12A6 6 0 0 1 8 6Z"), C(16, 12, 3)],
        ["stop"] = [P("M7 5H17A2 2 0 0 1 19 7V17A2 2 0 0 1 17 19H7A2 2 0 0 1 5 17V7A2 2 0 0 1 7 5Z")],
        ["telegram"] = [P("M21 3 3 10l7 3 4 8 7-18Zm0 0L10 13m0 0 4 3")],
        ["timer"] = [C(12, 14, 7), P("M9 2h6m-3 0v5m0 4v4m5-7 2-2")],
        ["untested"] = [C(dash: true)],
        ["verified"] = [P("m2.5 12 4.5 4.5L16 7m-4 9.5L21 7")],
        ["warning"] = [P("m12 3 10 17H2L12 3Zm0 6v5m0 3h.01")]
    };
}
