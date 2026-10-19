using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Controls;

// The coloured disc in front of a server name: the protocol group's icon on a tint of the same colour. Colours come
// from the Proto* tokens, so the disc follows the light and dark theme.
public sealed class ProtocolDisc : Border
{
    public static readonly StyledProperty<ProtocolGroup> GroupProperty =
        AvaloniaProperty.Register<ProtocolDisc, ProtocolGroup>(nameof(Group));

    public ProtocolGroup Group { get => GetValue(GroupProperty); set => SetValue(GroupProperty, value); }

    private readonly Pictogram _glyph = new()
    {
        Width = 15, Height = 15,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    private IDisposable? _background;
    private IDisposable? _foreground;

    public ProtocolDisc()
    {
        Width = 26;
        Height = 26;
        CornerRadius = new CornerRadius(13);
        VerticalAlignment = VerticalAlignment.Center;
        Child = _glyph;
        Apply();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GroupProperty) Apply();
    }

    private void Apply()
    {
        var prefix = ProtocolGroups.TokenPrefix(Group);
        _background?.Dispose();
        _foreground?.Dispose();
        _background = Bind(BackgroundProperty, this.GetResourceObservable(prefix + "BgBrush"));
        _foreground = _glyph.Bind(Pictogram.ForegroundProperty, this.GetResourceObservable(prefix + "FgBrush"));
        _glyph.Id = ProtocolGroups.Icon(Group);
    }
}
