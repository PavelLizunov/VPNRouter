using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using VPNRouter.Core.Services;

namespace VPNRouter.UI.Controls;

/// <summary>
/// The state hero of the main screen: a ring with the Lucide power icon, a title and a subtitle. States: off (grey),
/// connecting (amber, an arc turns around the ring), connected (green), error (red). The ring and the icon use the same
/// stroke weight; the icon is centred on the ring and lifted by 1 dp because its arc is lower than its line.
/// </summary>
public class StatusCard : UserControl
{
    public static readonly StyledProperty<bool> IsOnProperty =
        AvaloniaProperty.Register<StatusCard, bool>(nameof(IsOn));
    public static readonly StyledProperty<bool> IsWarnProperty =
        AvaloniaProperty.Register<StatusCard, bool>(nameof(IsWarn));
    public static readonly StyledProperty<bool> IsOffProperty =
        AvaloniaProperty.Register<StatusCard, bool>(nameof(IsOff), defaultValue: true);
    public static readonly StyledProperty<bool> IsErrorProperty =
        AvaloniaProperty.Register<StatusCard, bool>(nameof(IsError));
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<StatusCard, string?>(nameof(Title));
    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<StatusCard, string?>(nameof(Subtitle));

    public bool IsOn { get => GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }
    public bool IsWarn { get => GetValue(IsWarnProperty); set => SetValue(IsWarnProperty, value); }
    public bool IsOff { get => GetValue(IsOffProperty); set => SetValue(IsOffProperty, value); }
    public bool IsError { get => GetValue(IsErrorProperty); set => SetValue(IsErrorProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    private const double RingSize = 112;
    private const double Stroke = 4;
    private const double GlyphSize = 52;
    private const double GlyphLift = 1;
    private const double SpinnerSweep = 100;
    // The arc stops after about 45 s even if the state never leaves "connecting" (it stays visible, standing still).
    private const ulong SpinTurns = 40;

    private readonly Grid _ringHost;
    private readonly Ellipse _ringFill;
    private readonly Ellipse _ringTrack;
    private readonly Arc _spinner;
    private readonly IconView _glyph;
    private readonly TextBlock _titleText;
    private readonly TextBlock _subtitleText;
    private CancellationTokenSource? _spinCts;
    private bool _spinning;
    private bool _attached;

    public StatusCard()
    {
        _ringFill = new Ellipse { Margin = new Thickness(Stroke - 0.5) };
        _ringTrack = new Ellipse { StrokeThickness = Stroke };
        _spinner = new Arc
        {
            StrokeThickness = Stroke,
            StrokeLineCap = PenLineCap.Round,
            StartAngle = -90,
            SweepAngle = SpinnerSweep,
            IsVisible = false,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = new RotateTransform(),
        };
        _glyph = new IconView(UiIcons.Power, GlyphSize)
        {
            StrokeWidth = Stroke,
            Margin = new Thickness(0, 0, 0, 2 * GlyphLift),
        };

        _ringHost = new Grid
        {
            Width = RingSize,
            Height = RingSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _ringFill, _ringTrack, _spinner, _glyph },
        };

        _titleText = new TextBlock
        {
            FontSize = VPNRouter.Android.UiScale.Fs(20),
            FontWeight = FontWeight.Bold,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        BindBrush(_titleText, TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _subtitleText = new TextBlock
        {
            FontSize = VPNRouter.Android.UiScale.Fs(11),
            LineHeight = VPNRouter.Android.UiScale.Lh(16),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        BindBrush(_subtitleText, TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(_ringHost);
        stack.Children.Add(_titleText);
        stack.Children.Add(_subtitleText);

        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(18, 22),
            Child = stack,
        };
        BindBrush(card, Border.BorderBrushProperty, "BorderSubtleBrush");
        BindBrush(card, Border.BackgroundProperty, "SurfaceBaseBrush");

        Content = card;

        ApplyState();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsOnProperty || e.Property == IsWarnProperty || e.Property == IsOffProperty
                || e.Property == IsErrorProperty)
                ApplyState();
            else if (e.Property == TitleProperty)
                _titleText.Text = Title ?? string.Empty;
            else if (e.Property == SubtitleProperty)
                _subtitleText.Text = Subtitle ?? string.Empty;
        };
    }

    private void ApplyState()
    {
        string trackKey, fillKey, glyphKey;
        var connecting = false;
        if (IsOn)
        {
            trackKey = "SuccessSolidBrush";
            fillKey = "SuccessBgBrush";
            glyphKey = "SuccessFgBrush";
        }
        else if (IsWarn)
        {
            trackKey = "BorderStrongBrush";
            fillKey = "WarningBgBrush";
            glyphKey = "WarningFgBrush";
            connecting = true;
        }
        else if (IsError)
        {
            trackKey = "DangerSolidBrush";
            fillKey = "DangerBgBrush";
            glyphKey = "DangerFgBrush";
        }
        else
        {
            trackKey = "BorderStrongBrush";
            fillKey = "SurfaceSunkenBrush";
            glyphKey = "TextMutedBrush";
        }

        BindBrush(_ringTrack, Shape.StrokeProperty, trackKey);
        BindBrush(_ringFill, Shape.FillProperty, fillKey);
        BindBrush(_spinner, Shape.StrokeProperty, "WarningSolidBrush");
        BindBrush(_glyph, IconView.ForegroundProperty, glyphKey);

        _spinner.IsVisible = connecting;
        if (connecting) StartSpin();
        else StopSpin();
    }

    private void StartSpin()
    {
        if (_spinning || !_attached) return;
        _spinning = true;
        if (!VPNRouter.Android.UiMotion.Enabled) return;
        var cts = new CancellationTokenSource();
        _spinCts = cts;
        var spin = new Animation
        {
            Duration = System.TimeSpan.FromMilliseconds(1100),
            IterationCount = new IterationCount(SpinTurns),
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(RotateTransform.AngleProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(RotateTransform.AngleProperty, 360d) } },
            },
        };
        _ = spin.RunAsync(_spinner, cts.Token);
    }

    private void StopSpin()
    {
        _spinning = false;
        var cts = _spinCts;
        _spinCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        cts.Dispose();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ApplyState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        StopSpin();
        base.OnDetachedFromVisualTree(e);
    }

    private static void BindBrush(Control target, AvaloniaProperty property, string resourceKey)
    {
        target.Bind(property, target.GetResourceObservable(resourceKey));
    }
}
