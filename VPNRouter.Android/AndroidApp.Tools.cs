using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace VPNRouter.Android;

/// <summary>
/// F-13 (2026-05-09) — Android Tools overlay. Mirrors desktop's
/// <c>VPNRouter.App/Views/Pages/ToolsPage.axaml</c> sub-tab strip layout
/// (DPI bypass + Telegram proxy) collapsed into a single mobile-friendly
/// overlay.
///
/// <para>Visual structure:
/// <list type="number">
///   <item>Title bar (× close + title) — same chrome as Settings/Profiles overlays.</item>
///   <item>Sub-tab strip — two segments (DPI bypass / Telegram proxy)
///   styled by <see cref="StyleSegmentButton"/>.</item>
///   <item>Body — toggled per tab. Each tab shows the equivalent status
///   card the desktop pages render (DpiBypassPage Status pane / TelegramPage
///   description+banner) plus a diagnostics row.</item>
/// </list></para>
///
/// <para>Both underlying engines (Zapret winws.exe, TgProxy daemon) are
/// not ported on Android — DPI bypass uses sing-box's native tls_fragment
/// inside the tunnel, TgProxy is fully unported. The overlay therefore
/// shows the structural shell with status indicators that read from
/// AndroidStorage's mode value, plus an explainer banner for the
/// not-applicable bits. The kebab item that opens it lives in the
/// new "Tools" menu section.</para>
/// </summary>
public partial class AndroidApp
{
    private Border? _toolsOverlay;

    private int _toolsSelectedTab; // 0 = DPI bypass, 1 = TgProxy
    private Avalonia.Controls.Button? _toolsTabZapret;
    private Avalonia.Controls.Button? _toolsTabTgProxy;
    private Control? _toolsZapretBody;
    private Control? _toolsTgProxyBody;

    private TextBlock? _toolsZapretStatusText;
    private Ellipse? _toolsZapretStatusDot;
    private TextBlock? _toolsTitleText;

    private Border BuildToolsOverlay()
    {
        var bg          = GetBrush("SurfaceAppBrush");
        var raised      = GetBrush("SurfaceRaisedBrush");
        var subtle      = GetBrush("BorderSubtleBrush");
        var defaultB    = GetBrush("BorderDefaultBrush");
        var sunken      = GetBrush("SurfaceSunkenBrush");
        var card        = GetBrush("SurfaceBaseBrush");
        var textP       = GetBrush("TextPrimaryBrush");
        var textS       = GetBrush("TextSecondaryBrush");
        var textM       = GetBrush("TextMutedBrush");
        var warningBg   = GetBrush("WarningBgBrush");
        var warningBd   = GetBrush("WarningBorderBrush");
        var warningFg   = GetBrush("WarningFgBrush");
        var radiusSm    = GetRadius("RadiusSm");

        // ── Title bar ─────────────────────────────────────────────────────
        _toolsTitleText = new TextBlock
        {
            Text = Localization.ToolsOverlayTitle,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = textP,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var closeBtn = new Avalonia.Controls.Button
        {
            Content = "✕",
            FontSize = 16,
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = textS,
        };
        closeBtn.Click += OnToolsCloseClicked;

        var titleBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(8, 4),
        };
        Grid.SetColumn(_toolsTitleText, 0);
        Grid.SetColumn(closeBtn, 1);
        titleBar.Children.Add(_toolsTitleText);
        titleBar.Children.Add(closeBtn);

        var titleBarBorder = new Border
        {
            Background = raised,
            BorderBrush = subtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4),
            Child = titleBar,
        };

        // ── Sub-tab strip (DPI bypass / TgProxy) ──────────────────────────
        // Mirrors desktop ToolsPage.axaml ListBox + horizontal StackPanel
        // ItemsPanel — two clickable segments, same style as the FreeConfigs
        // overlay's tab row.
        _toolsTabZapret  = MakeSegmentButton(Localization.ToolsTabZapret,  active: true,
                                             (_, _) => SelectToolsTab(0));
        _toolsTabTgProxy = MakeSegmentButton(Localization.ToolsTabTgProxy, active: false,
                                             (_, _) => SelectToolsTab(1));
        var tabRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 4,
            Margin = new Thickness(10, 8, 10, 0),
        };
        Grid.SetColumn(_toolsTabZapret,  0);
        Grid.SetColumn(_toolsTabTgProxy, 1);
        tabRow.Children.Add(_toolsTabZapret);
        tabRow.Children.Add(_toolsTabTgProxy);

        // ── Tab 1 body — DPI bypass (Zapret) ─────────────────────────────
        // Mirrors DpiBypassPage Status pane:
        //   • status indicator (dot + label)
        //   • description blurb
        //   • warning banner (yellow "⚠ enable only if needed")
        //   • "managed in Settings" cross-link
        _toolsZapretStatusDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _toolsZapretStatusText = new TextBlock
        {
            Text = ZapretStatusLabelForCurrentMode(),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = textP,
        };
        UpdateZapretStatusIndicator();

        var zapretStatusRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Children = { _toolsZapretStatusDot, _toolsZapretStatusText },
        };

        // Status bar — sunken bg, same Border treatment as desktop's
        // DpiBypassPage Status pane indicator (lines 86-93).
        var zapretStatusBar = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = sunken,
            Child = zapretStatusRow,
        };

        var zapretDescription = new TextBlock
        {
            Text = Localization.SettingsDpiBypassHint,
            FontSize = 10,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Foreground = textS,
        };

        // Warning banner — yellow border + bg, same as desktop lines 96-110.
        var warningIcon = new TextBlock
        {
            Text = "⚠",
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = warningFg,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var warningText = new TextBlock
        {
            Text = Localization.SettingsDpiBypassWarning,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Foreground = warningFg,
        };
        var warningGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(warningIcon, 0);
        Grid.SetColumn(warningText, 1);
        warningGrid.Children.Add(warningIcon);
        warningGrid.Children.Add(warningText);
        var warningBanner = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = warningBg,
            BorderBrush = warningBd,
            BorderThickness = new Thickness(1),
            Child = warningGrid,
        };

        // "Open DPI Bypass details" CTA — opens the dedicated DpiBypass
        // overlay (which has the strategy picker + advanced sections).
        var openDpiBtn = new Avalonia.Controls.Button
        {
            Content = Localization.MenuItemOpenDpiBypass,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = GetBrush("AccentSolidBrush"),
            Foreground = GetBrush("AccentOnSolidBrush"),
            BorderThickness = new Thickness(0),
        };
        openDpiBtn.Click += (_, _) =>
        {
            if (_toolsOverlay is not null) _toolsOverlay.IsVisible = false;
            ShowDpiBypassOverlay();
        };

        // "Sections not applicable" explainer card — the desktop sidebar
        // exposes Hosts / Filters / Updates / Advanced sections. Android's
        // Zapret port doesn't run winws.exe so those are non-functional.
        // One-liner blurb matches the structural shell without lying about
        // what's reachable.
        var notApplicableCard = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = sunken,
            BorderBrush = subtle,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = Localization.AndroidZapretSectionNotApplicable,
                FontSize = 10,
                Opacity = 0.7,
                Foreground = textS,
                TextWrapping = TextWrapping.Wrap,
            },
        };

        var zapretSectionTitle = new TextBlock
        {
            Text = Localization.ZapretSecStatus,
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Foreground = textP,
        };

        var zapretBodyStack = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                zapretSectionTitle,
                zapretStatusBar,
                zapretDescription,
                warningBanner,
                openDpiBtn,
                notApplicableCard,
                BuildToolsDiagnosticsRow(),
            },
        };
        _toolsZapretBody = new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(12, 10, 12, 12),
                Child = zapretBodyStack,
            },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = bg,
        };

        // ── Tab 2 body — TgProxy ─────────────────────────────────────────
        // Mirrors desktop TelegramPage description + banner pattern. On
        // Android we don't ship the daemon, so the body is a single
        // status banner (same Border treatment desktop uses for the
        // info banner around line 155-197) explaining the gap, plus an
        // OpenGitHub fallback so curious users can read upstream docs.
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
        _toolsTgProxyBody = new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(12, 10, 12, 12),
                Child = tgBodyStack,
            },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = bg,
            IsVisible = false,
        };

        var bodyArea = new Grid
        {
            Children = { _toolsZapretBody, _toolsTgProxyBody },
        };

        // ── Compose: title + tab strip + body ────────────────────────────
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleBarBorder, Dock.Top);
        DockPanel.SetDock(tabRow, Dock.Top);
        dock.Children.Add(titleBarBorder);
        dock.Children.Add(tabRow);
        dock.Children.Add(bodyArea);

        return new Border
        {
            Background = bg,
            IsVisible = false,
            Child = dock,
        };
    }

    /// <summary>
    /// Diagnostics row — mirrors desktop ToolsPage's footer-button cluster.
    /// Three actions reachable from the Tools overlay: run health check,
    /// open singbox.log, IP-leak check. All three already exist as kebab
    /// menu handlers; this just surfaces them in-page so the user doesn't
    /// have to bounce back to the kebab to diagnose.
    /// </summary>
    private Control BuildToolsDiagnosticsRow()
    {
        var radiusSm = GetRadius("RadiusSm");
        var defaultB = GetBrush("BorderDefaultBrush");
        var textP    = GetBrush("TextPrimaryBrush");

        var header = new TextBlock
        {
            Text = Localization.AndroidToolsDiagnosticsHeader,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = textP,
            Margin = new Thickness(0, 6, 0, 4),
        };

        var healthBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AndroidToolsRunHealthCheck,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(radiusSm),
            Background = Brushes.Transparent,
            BorderBrush = defaultB,
            BorderThickness = new Thickness(1),
            Foreground = textP,
        };
        healthBtn.Click += (s, e) =>
        {
            if (_toolsOverlay is not null) _toolsOverlay.IsVisible = false;
            OnMenuHealthCheckClicked(s, e);
        };

        var openLogBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AndroidToolsOpenLog,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(radiusSm),
            Background = Brushes.Transparent,
            BorderBrush = defaultB,
            BorderThickness = new Thickness(1),
            Foreground = textP,
        };
        openLogBtn.Click += (s, e) =>
        {
            if (_toolsOverlay is not null) _toolsOverlay.IsVisible = false;
            OnMenuOpenLogClicked(s, e);
        };

        var leakBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AndroidToolsCheckLeak,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(radiusSm),
            Background = Brushes.Transparent,
            BorderBrush = defaultB,
            BorderThickness = new Thickness(1),
            Foreground = textP,
        };
        leakBtn.Click += (s, e) =>
        {
            if (_toolsOverlay is not null) _toolsOverlay.IsVisible = false;
            OnMenuCheckLeaksClicked(s, e);
        };

        return new StackPanel
        {
            Spacing = 6,
            Children = { header, healthBtn, openLogBtn, leakBtn },
        };
    }

    private void SelectToolsTab(int index)
    {
        _toolsSelectedTab = index;
        if (_toolsZapretBody  is not null) _toolsZapretBody.IsVisible  = index == 0;
        if (_toolsTgProxyBody is not null) _toolsTgProxyBody.IsVisible = index == 1;
        StyleSegmentButton(_toolsTabZapret,  index == 0);
        StyleSegmentButton(_toolsTabTgProxy, index == 1);
    }

    private void OnToolsCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_toolsOverlay is not null) _toolsOverlay.IsVisible = false;
    }

    private void ShowToolsOverlay()
    {
        if (_toolsOverlay is null) return;
        // Re-seed status indicator since the user may have flipped the
        // mode in Settings since the overlay was last visible.
        if (_toolsZapretStatusText is not null)
            _toolsZapretStatusText.Text = ZapretStatusLabelForCurrentMode();
        UpdateZapretStatusIndicator();
        _toolsOverlay.IsVisible = true;
    }

    private void OnMenuToolsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        ShowToolsOverlay();
    }

    /// <summary>
    /// Lookup string used by both the Tools overlay (status pane) and the
    /// DPI Bypass overlay's footer banner. Keeps the wording consistent
    /// across the two surfaces.
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

    /// <summary>
    /// Re-paint the Zapret status dot. Off → muted grey;
    /// Standard/Aggressive → success green. Mirrors desktop's
    /// <c>BoolToStatusColorConverter</c> driven from <c>ZapretEnabled</c>.
    /// </summary>
    private void UpdateZapretStatusIndicator()
    {
        if (_toolsZapretStatusDot is null) return;
        var enabled = AndroidStorage.GetDpiBypassMode() != "off";
        _toolsZapretStatusDot.Fill = GetBrush(enabled ? "SuccessSolidBrush" : "TextMutedBrush");
    }
}
