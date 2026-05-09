using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VPNRouter.UI.Controls;

/// <summary>
/// v3.0 Phase G step 1 (2026-05-09) — shared status card UserControl.
///
/// <para>Cross-platform Avalonia control extracted from desktop's
/// <c>VPNRouter.App/Views/Pages/SimplePage.axaml</c> so desktop and
/// Android can render the same border, padding, dot indicator, and
/// typography from a single source.</para>
///
/// <para>API: three mutually-exclusive bool styled properties
/// (<see cref="IsOn"/>, <see cref="IsWarn"/>, <see cref="IsOff"/>)
/// drive the dot colour; <see cref="Title"/> and <see cref="Subtitle"/>
/// drive the text content. The bool-trio shape mirrors the existing
/// <c>SimpleStatusIs*</c> VM bindings on desktop, so the desktop XAML
/// substitution is mechanical (no converter layer).</para>
///
/// <para>Visual tokens — border colour, corner radius, success/warning
/// brushes, etc. — resolve via <c>{DynamicResource}</c> against the
/// host app's <c>Tokens.axaml</c>. Both consumers already merge that
/// dictionary into <c>Application.Resources</c>, so nothing extra
/// has to wire up here.</para>
/// </summary>
public partial class StatusCard : UserControl
{
    /// <summary>True when the VPN is connected/active. Renders the green
    /// (Success) dot. Mutually exclusive with <see cref="IsWarn"/> and
    /// <see cref="IsOff"/>; the consumer is responsible for the
    /// invariant. The control just shows whichever ellipse is visible.</summary>
    public static readonly StyledProperty<bool> IsOnProperty =
        AvaloniaProperty.Register<StatusCard, bool>(nameof(IsOn));

    /// <summary>True during transitional states (e.g. connecting,
    /// reconnecting). Renders the amber (Warning) dot.</summary>
    public static readonly StyledProperty<bool> IsWarnProperty =
        AvaloniaProperty.Register<StatusCard, bool>(nameof(IsWarn));

    /// <summary>True when disconnected/idle. Renders the muted-gray dot.
    /// Defaults to <c>true</c> so a freshly placed StatusCard with no
    /// bindings shows a sensible "off" indicator instead of a blank
    /// header row.</summary>
    public static readonly StyledProperty<bool> IsOffProperty =
        AvaloniaProperty.Register<StatusCard, bool>(nameof(IsOff), defaultValue: true);

    /// <summary>Bold heading text shown next to the status dot.</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<StatusCard, string?>(nameof(Title));

    /// <summary>Description shown below the heading. Wraps onto multiple
    /// lines as needed.</summary>
    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<StatusCard, string?>(nameof(Subtitle));

    public bool IsOn
    {
        get => GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public bool IsWarn
    {
        get => GetValue(IsWarnProperty);
        set => SetValue(IsWarnProperty, value);
    }

    public bool IsOff
    {
        get => GetValue(IsOffProperty);
        set => SetValue(IsOffProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public StatusCard()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
