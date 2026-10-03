using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Controls;

// One icon of the desktop UI. Ids are the names the XAML uses; most map to a Lucide icon from UiIcons (the set the
// Android app draws), the status dots and the selected radio mark are plain circles. Strokes are 1.8 units of the 24
// grid with round caps and joins, but never thinner than MinStrokePx on screen, so small inline icons stay readable.
public sealed class Pictogram : Control
{
    public static readonly StyledProperty<string?> IdProperty =
        AvaloniaProperty.Register<Pictogram, string?>(nameof(Id));
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Pictogram>();

    public string? Id { get => GetValue(IdProperty); set => SetValue(IdProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private const double Stroke = 1.8;
    private const double MinStrokePx = 1.2;

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
        var pen = new Pen
        {
            Brush = Foreground, Thickness = Math.Max(Stroke, MinStrokePx / scale),
            LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
        };
        foreach (var part in parts)
            context.DrawGeometry(part.Filled ? Foreground : null, part.Filled ? null : pen, part.Geometry);
    }

    private sealed record Part(Geometry Geometry, bool Filled = false);
    private static Part L(string lucide) => new(Geometry.Parse(UiIcons.PathData[lucide]));
    private static Part Dot(double radius) => new(new EllipseGeometry(new Rect(12 - radius, 12 - radius, radius * 2, radius * 2)), true);
    private static Part Ring(double radius) => new(new EllipseGeometry(new Rect(12 - radius, 12 - radius, radius * 2, radius * 2)));

    private static readonly IReadOnlyDictionary<string, Part[]> Shapes = new Dictionary<string, Part[]>
    {
        ["add"] = [L(UiIcons.Plus)],
        ["add-circle"] = [L(UiIcons.CirclePlus)],
        ["apply"] = [L(UiIcons.RefreshCw)],
        ["arrow-down"] = [L(UiIcons.ArrowDown)],
        ["arrow-left"] = [L(UiIcons.ArrowLeft)],
        ["arrow-right"] = [L(UiIcons.ArrowRight)],
        ["arrow-up"] = [L(UiIcons.ArrowUp)],
        ["blocked"] = [L(UiIcons.Ban)],
        ["check"] = [L(UiIcons.Check)],
        ["chevron-down"] = [L(UiIcons.ChevronDown)],
        ["chevron-left"] = [L(UiIcons.ChevronLeft)],
        ["chevron-right"] = [L(UiIcons.ChevronRight)],
        ["close"] = [L(UiIcons.X)],
        ["delete"] = [L(UiIcons.Trash)],
        ["error"] = [L(UiIcons.CircleX)],
        ["find-working"] = [L(UiIcons.SearchCheck)],
        ["flag"] = [L(UiIcons.Flag)],
        ["folder"] = [L(UiIcons.FolderOpen)],
        ["globe"] = [L(UiIcons.Globe)],
        ["info"] = [L(UiIcons.Info)],
        ["link"] = [L(UiIcons.Link)],
        ["running"] = [L(UiIcons.Activity)],
        ["more-horizontal"] = [L(UiIcons.Ellipsis)],
        ["play"] = [L(UiIcons.Play)],
        ["power"] = [L(UiIcons.Power)],
        ["home"] = [L(UiIcons.House)],
        ["radio-off"] = [L(UiIcons.Circle)],
        ["radio-on"] = [L(UiIcons.Circle), Dot(5)],
        ["refresh"] = [L(UiIcons.RefreshCw)],
        ["remove-circle"] = [L(UiIcons.CircleMinus)],
        ["retest"] = [L(UiIcons.RefreshCw)],
        ["search-tab"] = [L(UiIcons.Search)],
        ["shield"] = [L(UiIcons.Shield)],
        ["shield-alert"] = [L(UiIcons.ShieldAlert)],
        ["shield-bolt"] = [L(UiIcons.ShieldCheck)],
        ["shield-check"] = [L(UiIcons.ShieldCheck)],
        ["sort"] = [L(UiIcons.ArrowDownUp)],
        ["status"] = [Dot(6)],
        ["status-off"] = [Ring(6)],
        ["switch"] = [L(UiIcons.ToggleRight)],
        ["tab-apps"] = [L(UiIcons.LayoutGrid)],
        ["tab-public"] = [L(UiIcons.Globe)],
        ["tab-servers"] = [L(UiIcons.Server)],
        ["tab-settings"] = [L(UiIcons.SlidersHorizontal)],
        ["tab-subscribe"] = [L(UiIcons.Rss)],
        ["tab-tools"] = [L(UiIcons.Wrench)],
        ["stop"] = [L(UiIcons.Square)],
        ["telegram"] = [L(UiIcons.Send)],
        ["timer"] = [L(UiIcons.Timer)],
        ["untested"] = [L(UiIcons.CircleDashed)],
        ["verified"] = [L(UiIcons.CheckCheck)],
        ["warning"] = [L(UiIcons.TriangleAlert)]
    };
}
