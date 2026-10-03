using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using VPNRouter.Core.Services;

namespace VPNRouter.UI.Controls;

/// <summary>
/// The state hero of the main screen: a ring with the Lucide power icon, a title and a subtitle. States: off (grey),
/// connecting (amber, an arc turns around the ring), connected (green), error (red). The ring and the icon use the same
/// stroke weight; the icon is centred on the ring and lifted by 1 dp because its arc is lower than its line.
/// Colours fade between states and the ring settles with a short scale when it turns green; no motion when Android
/// animations are off.
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
    public static readonly StyledProperty<IImage?> EmblemProperty =
        AvaloniaProperty.Register<StatusCard, IImage?>(nameof(Emblem));

    public bool IsOn { get => GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }
    public bool IsWarn { get => GetValue(IsWarnProperty); set => SetValue(IsWarnProperty, value); }
    public bool IsOff { get => GetValue(IsOffProperty); set => SetValue(IsOffProperty, value); }
    public bool IsError { get => GetValue(IsErrorProperty); set => SetValue(IsErrorProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    /// <summary>The picture in the ring (the mascot). Without it the ring shows the power icon, as before.</summary>
    public IImage? Emblem { get => GetValue(EmblemProperty); set => SetValue(EmblemProperty, value); }

    /// <summary>Raised when the ring is tapped. The ring is the main connect/disconnect target of the screen.</summary>
    public event System.EventHandler? Clicked;

    private const double RingSize = 136;
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
    private readonly Image _emblemImage;
    private readonly Border _emblemDisc;
    private readonly Border _badge;
    private readonly IconView _badgeGlyph;
    private bool _pressed;
    private readonly TextBlock _titleText;
    private readonly TextBlock _subtitleText;
    private CancellationTokenSource? _spinCts;
    private bool _wasOn;
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

        // The mascot sits on a disc of its own (the art is dark line work): white in every theme, like the tile around it on the desktop.
        _emblemImage = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(6), IsHitTestVisible = false };
        RenderOptions.SetBitmapInterpolationMode(_emblemImage, BitmapInterpolationMode.HighQuality);
        _emblemDisc = new Border
        {
            Margin = new Thickness(Stroke + 8),
            CornerRadius = new CornerRadius(RingSize),
            ClipToBounds = true,
            Background = Brushes.White,
            IsVisible = false,
            IsHitTestVisible = false,
            Child = _emblemImage,
        };

        _badgeGlyph = new IconView(UiIcons.Power, 16) { StrokeWidth = 2.5, Foreground = Brushes.White };
        _badge = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, -10),
            IsVisible = false,
            IsHitTestVisible = false,
            Child = _badgeGlyph,
        };

        _ringHost = new Grid
        {
            Width = RingSize,
            Height = RingSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 0, 0, 10),
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = new ScaleTransform(1, 1),
            Children = { _ringFill, _ringTrack, _spinner, _glyph, _emblemDisc, _badge },
        };
        AutomationProperties.SetControlTypeOverride(_ringHost, AutomationControlType.Button);
        _ringHost.PointerPressed += OnRingPressed;
        _ringHost.PointerReleased += OnRingReleased;
        _ringHost.PointerCaptureLost += (_, _) => SetPressed(false);
        if (VPNRouter.Android.UiMotion.Enabled)
        {
            var fade = System.TimeSpan.FromMilliseconds(250);
            _ringTrack.Transitions = new Transitions { new BrushTransition { Property = Shape.StrokeProperty, Duration = fade } };
            _ringFill.Transitions = new Transitions { new BrushTransition { Property = Shape.FillProperty, Duration = fade } };
            _glyph.Transitions = new Transitions { new BrushTransition { Property = IconView.ForegroundProperty, Duration = fade } };
        }

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

        // The hero sits straight on the page (no card around it), like the home screen on the desktop.
        Content = new Border { Padding = new Thickness(8, 14, 8, 6), Child = stack };

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
            else if (e.Property == EmblemProperty)
                ApplyEmblem();
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
        BindBrush(_badge, Border.BackgroundProperty, connecting ? "WarningSolidBrush" : trackKey);
        AutomationProperties.SetName(_ringHost, IsOn ? VPNRouter.Android.Localization.ButtonDisconnect : VPNRouter.Android.Localization.ButtonConnect);

        _spinner.IsVisible = connecting;
        if (connecting) StartSpin();
        else StopSpin();

        if (IsOn && !_wasOn && _attached) Settle();
        _wasOn = IsOn;
    }

    private void ApplyEmblem()
    {
        var emblem = Emblem;
        _emblemImage.Source = emblem;
        var has = emblem is not null;
        _emblemDisc.IsVisible = has;
        _badge.IsVisible = has;
        _glyph.IsVisible = !has;
    }

    private void OnRingPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsWarn) return;
        SetPressed(true);
        e.Pointer.Capture(_ringHost);
        e.Handled = true;
    }

    private void OnRingReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed) return;
        SetPressed(false);
        e.Pointer.Capture(null);
        var p = e.GetPosition(_ringHost);
        if (p.X >= 0 && p.Y >= 0 && p.X <= _ringHost.Bounds.Width && p.Y <= _ringHost.Bounds.Height)
            Clicked?.Invoke(this, System.EventArgs.Empty);
        e.Handled = true;
    }

    private void SetPressed(bool pressed)
    {
        _pressed = pressed;
        if (_ringHost.RenderTransform is ScaleTransform scale)
        {
            var s = pressed ? 0.95 : 1.0;
            scale.ScaleX = s;
            scale.ScaleY = s;
        }
    }

    // One short scale of the ring when the state turns green: 0.94 -> 1.03 -> 1.
    private void Settle()
    {
        if (!VPNRouter.Android.UiMotion.Enabled) return;
        var settle = new Animation
        {
            Duration = System.TimeSpan.FromMilliseconds(420),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(ScaleTransform.ScaleXProperty, 0.94), new Setter(ScaleTransform.ScaleYProperty, 0.94) } },
                new KeyFrame { Cue = new Cue(0.55d), Setters = { new Setter(ScaleTransform.ScaleXProperty, 1.03), new Setter(ScaleTransform.ScaleYProperty, 1.03) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(ScaleTransform.ScaleXProperty, 1.0), new Setter(ScaleTransform.ScaleYProperty, 1.0) } },
            },
        };
        _ = settle.RunAsync(_ringHost, CancellationToken.None);
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
