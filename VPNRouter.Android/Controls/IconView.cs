using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using VPNRouter.Core.Services;

namespace VPNRouter.UI.Controls;

/// <summary>
/// One Lucide icon from <see cref="UiIcons"/>, drawn as a stroke on the 24 x 24 grid scaled to <see cref="Size"/>.
/// The stroke width is in dp and does not scale with the size, like the bottom navigation icons (1.8 dp).
/// <see cref="Foreground"/> is inherited like text colour, so an icon inside a button follows the button's colour,
/// its pressed and disabled states and the theme. <see cref="Reveal"/> (0 to 1) shows the icon from left to right, for
/// a check mark that draws itself in.
/// </summary>
public sealed class IconView : Control
{
    public const double DefaultStroke = 1.8;

    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<IconView, string?>(nameof(Icon));
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<IconView>();
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<IconView, double>(nameof(Size), 20);
    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<IconView, double>(nameof(StrokeWidth), DefaultStroke);
    public static readonly StyledProperty<double> RevealProperty =
        AvaloniaProperty.Register<IconView, double>(nameof(Reveal), 1.0);

    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double StrokeWidth { get => GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    public double Reveal { get => GetValue(RevealProperty); set => SetValue(RevealProperty, value); }

    private static readonly Dictionary<string, Geometry> Cache = new(System.StringComparer.Ordinal);

    static IconView()
    {
        AffectsRender<IconView>(IconProperty, ForegroundProperty, StrokeWidthProperty, RevealProperty);
        AffectsMeasure<IconView>(SizeProperty);
    }

    public IconView()
    {
        IsHitTestVisible = false;
        Focusable = false;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
    }

    public IconView(string icon, double size) : this()
    {
        Icon = icon;
        Size = size;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var geometry = GetGeometry(Icon);
        var brush = Foreground;
        if (geometry is null || brush is null || Reveal <= 0) return;
        var side = System.Math.Min(Bounds.Width, Bounds.Height);
        var scale = side / 24.0;
        if (scale <= 0) return;
        var origin = Matrix.CreateTranslation((Bounds.Width - side) / 2, (Bounds.Height - side) / 2);
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale) * origin);
        var pen = new Pen
        {
            Brush = brush,
            Thickness = StrokeWidth / scale,
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (Reveal >= 1)
        {
            context.DrawGeometry(null, pen, geometry);
            return;
        }
        using var clip = context.PushClip(new Rect(-2, -2, 28 * Reveal, 28));
        context.DrawGeometry(null, pen, geometry);
    }

    /// <summary>Draws the icon in from left to right once (a check mark that writes itself). Skipped when Android
    /// animations are off.</summary>
    public void PlayReveal(int milliseconds = 320)
    {
        if (!VPNRouter.Android.UiMotion.Enabled) return;
        var reveal = new Animation
        {
            Duration = System.TimeSpan.FromMilliseconds(milliseconds),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(RevealProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(RevealProperty, 1d) } },
            },
        };
        _ = reveal.RunAsync(this, System.Threading.CancellationToken.None);
    }

    internal static Geometry? GetGeometry(string? name)
    {
        if (name is null) return null;
        if (Cache.TryGetValue(name, out var cached)) return cached;
        if (!UiIcons.PathData.TryGetValue(name, out var data)) return null;
        var geometry = Geometry.Parse(data);
        Cache[name] = geometry;
        return geometry;
    }
}

/// <summary>An icon followed (or preceded) by a text label, the content of a button that had a text symbol.</summary>
public sealed class IconLabel : StackPanel
{
    private readonly TextBlock _text;

    public IconLabel(string icon, string text, double iconSize, bool iconAfter = false)
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal;
        Spacing = 6;
        VerticalAlignment = VerticalAlignment.Center;
        IconView = new IconView(icon, iconSize);
        _text = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (iconAfter)
        {
            Children.Add(_text);
            Children.Add(IconView);
        }
        else
        {
            Children.Add(IconView);
            Children.Add(_text);
        }
    }

    public IconView IconView { get; }

    public TextBlock TextBlock => _text;

    public string? Text { get => _text.Text; set => _text.Text = value; }
}
