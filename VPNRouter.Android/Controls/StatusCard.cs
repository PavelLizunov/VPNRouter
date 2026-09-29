using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

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

    private readonly Ellipse _dotOn;
    private readonly Ellipse _dotWarn;
    private readonly Ellipse _dotOff;
    private readonly TextBlock _titleText;
    private readonly TextBlock _subtitleText;

    public StatusCard()
    {
        _dotOn = new Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        _dotWarn = new Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        _dotOff = new Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        BindBrush(_dotOn, Shape.FillProperty, "SuccessSolidBrush");
        BindBrush(_dotWarn, Shape.FillProperty, "WarningSolidBrush");
        BindBrush(_dotOff, Shape.FillProperty, "TextMutedBrush");

        _titleText = new TextBlock { FontSize = 14, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        BindBrush(_titleText, TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _subtitleText = new TextBlock { FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 0, 0, 0), LineHeight = 13 };
        BindBrush(_subtitleText, TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var headerRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        headerRow.Children.Add(_dotOn);
        headerRow.Children.Add(_dotWarn);
        headerRow.Children.Add(_dotOff);
        headerRow.Children.Add(_titleText);

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(headerRow);
        stack.Children.Add(_subtitleText);

        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 11),
            Child = stack,
        };
        BindBrush(border, Border.BorderBrushProperty, "BorderDefaultBrush");
        BindBrush(border, Border.BackgroundProperty, "SurfaceBaseBrush");

        Content = border;

        SyncDots();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsOnProperty || e.Property == IsWarnProperty || e.Property == IsOffProperty)
                SyncDots();
            else if (e.Property == TitleProperty)
                _titleText.Text = Title ?? string.Empty;
            else if (e.Property == SubtitleProperty)
                _subtitleText.Text = Subtitle ?? string.Empty;
        };
    }

    private void SyncDots()
    {
        _dotOn.IsVisible = IsOn;
        _dotWarn.IsVisible = IsWarn;
        _dotOff.IsVisible = IsOff;
    }

    private static void BindBrush(Control target, AvaloniaProperty property, string resourceKey)
    {
        target.Bind(property, target.GetResourceObservable(resourceKey));
    }
}
