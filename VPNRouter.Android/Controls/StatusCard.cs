using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace VPNRouter.UI.Controls;

public class StatusCard : UserControl
{
    public static readonly StyledProperty<bool> IsOnProperty =
        AvaloniaProperty.Register<StatusCard, bool>(nameof(IsOn));
    public static readonly StyledProperty<bool> IsWarnProperty =
        AvaloniaProperty.Register<StatusCard, bool>(nameof(IsWarn));
    public static readonly StyledProperty<bool> IsOffProperty =
        AvaloniaProperty.Register<StatusCard, bool>(nameof(IsOff), defaultValue: true);
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<StatusCard, string?>(nameof(Title));
    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<StatusCard, string?>(nameof(Subtitle));

    public bool IsOn { get => GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }
    public bool IsWarn { get => GetValue(IsWarnProperty); set => SetValue(IsWarnProperty, value); }
    public bool IsOff { get => GetValue(IsOffProperty); set => SetValue(IsOffProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    private const double RingSize = 112;
    private const double RingThickness = 6;

    private readonly Border _ring;
    private readonly Avalonia.Controls.Shapes.Path _glyph;
    private readonly TextBlock _titleText;
    private readonly TextBlock _subtitleText;
    private CancellationTokenSource? _pulseCts;

    public StatusCard()
    {
        _glyph = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M12,3 L12,12 M7,6.5 A8,8 0 1 0 17,6.5"),
            StrokeThickness = 2.4,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Width = 44,
            Height = 44,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _ring = new Border
        {
            Width = RingSize,
            Height = RingSize,
            CornerRadius = new CornerRadius(RingSize / 2),
            BorderThickness = new Thickness(RingThickness),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = _glyph,
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
        stack.Children.Add(_ring);
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
            if (e.Property == IsOnProperty || e.Property == IsWarnProperty || e.Property == IsOffProperty)
                ApplyState();
            else if (e.Property == TitleProperty)
                _titleText.Text = Title ?? string.Empty;
            else if (e.Property == SubtitleProperty)
                _subtitleText.Text = Subtitle ?? string.Empty;
        };
    }

    private void ApplyState()
    {
        string ringKey, fillKey, glyphKey;
        if (IsOn)
        {
            ringKey = "SuccessSolidBrush";
            fillKey = "SuccessBgBrush";
            glyphKey = "SuccessFgBrush";
        }
        else if (IsWarn)
        {
            ringKey = "WarningSolidBrush";
            fillKey = "WarningBgBrush";
            glyphKey = "WarningFgBrush";
        }
        else
        {
            ringKey = "BorderStrongBrush";
            fillKey = "SurfaceSunkenBrush";
            glyphKey = "TextMutedBrush";
        }

        BindBrush(_ring, Border.BorderBrushProperty, ringKey);
        BindBrush(_ring, Border.BackgroundProperty, fillKey);
        BindBrush(_glyph, Shape.StrokeProperty, glyphKey);

        StopPulse();
        if (IsWarn && !IsOn) StartPulse();
    }

    private void StartPulse()
    {
        var cts = new CancellationTokenSource();
        _pulseCts = cts;
        var pulse = new Animation
        {
            Duration = System.TimeSpan.FromMilliseconds(900),
            IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(OpacityProperty, 1.0) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(OpacityProperty, 0.45) } },
            },
        };
        _ = pulse.RunAsync(_ring, cts.Token);
    }

    private void StopPulse()
    {
        var cts = _pulseCts;
        _pulseCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        cts.Dispose();
        _ring.Opacity = 1.0;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopPulse();
        base.OnDetachedFromVisualTree(e);
    }

    private static void BindBrush(Control target, AvaloniaProperty property, string resourceKey)
    {
        target.Bind(property, target.GetResourceObservable(resourceKey));
    }
}
