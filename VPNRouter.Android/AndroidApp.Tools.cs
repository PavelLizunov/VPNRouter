using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace VPNRouter.Android;

/// <summary>
/// F-13 (2026-05-09) — Android Tools partial. Originally hosted a
/// fullscreen overlay with a sub-tab strip (DPI bypass + Telegram proxy)
/// that mirrored desktop's <c>ToolsPage.axaml</c>.
///
/// <para>AND-MIGRATE-OVERLAYS (2026-05-09) split those two sub-tabs into
/// top-level Advanced-shell tabs (DPI bypass / Telegram), retiring the
/// combined hub. Only the Telegram-tab body builder + the shared Zapret
/// status-label helper remain here — the DPI bypass tab uses
/// <see cref="BuildDpiBypassTabContent"/> from
/// <c>AndroidApp.DpiBypass.cs</c>.</para>
///
/// <para>Both underlying engines (Zapret winws.exe, TgProxy daemon) are
/// not ported on Android — DPI bypass uses sing-box's native tls_fragment
/// inside the tunnel, TgProxy is fully unported. The Telegram tab
/// therefore renders an explainer banner with a GitHub link rather than
/// a daemon control surface.</para>
/// </summary>
public partial class AndroidApp
{
    /// <summary>
    /// AND-MIGRATE-OVERLAYS (2026-05-09) — body content for the Telegram
    /// tab inside the Advanced shell. Mirrors the old Tools overlay's
    /// TgProxy section: title + info banner + description + GitHub link.
    /// </summary>
    private Control BuildTelegramTabContent()
    {
        var bg          = GetBrush("SurfaceAppBrush");
        var subtle      = GetBrush("BorderSubtleBrush");
        var defaultB    = GetBrush("BorderDefaultBrush");
        var card        = GetBrush("SurfaceBaseBrush");
        var textP       = GetBrush("TextPrimaryBrush");
        var textS       = GetBrush("TextSecondaryBrush");
        var textM       = GetBrush("TextMutedBrush");
        var radiusSm    = GetRadius("RadiusSm");

        var tgDescription = new TextBlock
        {
            Text = Localization.AndroidTgProxyNotApplicable,
            FontSize = 11,
            LineHeight = 16,
            Opacity = 0.8,
            Foreground = textS,
            TextWrapping = TextWrapping.Wrap,
        };

        var tgInfoDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = textM,
        };
        var tgInfoText = new TextBlock
        {
            Text = Localization.AutostartTgProxyNotPorted,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = textS,
        };
        var tgInfoRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { tgInfoDot, tgInfoText },
        };
        var tgInfoBanner = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = card,
            BorderBrush = subtle,
            BorderThickness = new Thickness(1),
            Child = tgInfoRow,
        };

        var tgGithubBtn = new Avalonia.Controls.Button
        {
            Content = "GitHub: Flowseal/tg-ws-proxy",
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = Brushes.Transparent,
            BorderBrush = defaultB,
            BorderThickness = new Thickness(1),
            Foreground = GetBrush("AccentFgBrush"),
        };
        tgGithubBtn.Click += (_, _) =>
        {
            try
            {
                var intent = new global::Android.Content.Intent(
                    global::Android.Content.Intent.ActionView,
                    global::Android.Net.Uri.Parse("https://github.com/Flowseal/tg-ws-proxy"));
                intent.SetFlags(global::Android.Content.ActivityFlags.NewTask);
                global::Android.App.Application.Context.StartActivity(intent);
            }
            catch { /* user has no browser — non-fatal */ }
        };

        var tgSectionTitle = new TextBlock
        {
            Text = Localization.ToolsTabTgProxy,
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Foreground = textP,
        };

        var tgBodyStack = new StackPanel
        {
            Spacing = 10,
            Children = { tgSectionTitle, tgInfoBanner, tgDescription, tgGithubBtn },
        };

        return new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(12, 10, 12, 12),
                Child = tgBodyStack,
            },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = bg,
        };
    }

    /// <summary>
    /// Lookup string used by the DPI bypass tab status bar + footer label.
    /// Reads the persisted DPI-bypass mode value and returns the matching
    /// localized status caption.
    /// </summary>
    private static string ZapretStatusLabelForCurrentMode()
    {
        return AndroidStorage.GetDpiBypassMode() switch
        {
            "standard"   => Localization.AndroidZapretStatusStandard,
            "aggressive" => Localization.AndroidZapretStatusAggressive,
            _            => Localization.AndroidZapretStatusOff,
        };
    }
}
