using System;
using System.Collections.Generic;
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

    // ── AND-ADV-TOOLS-PUBLIC (2026-05-10) — Phase E ─────────────────────
    //
    // Merged Tools tab. Mirrors desktop ToolsPage.axaml (sub-tab strip
    // hosting Zapret + Telegram proxy detail pages). Phase A will replace
    // the AdvancedTab.{DpiBypass,Telegram} pair with a single
    // AdvancedTab.Tools enum value and dispatch here. Until Phase A
    // lands this method coexists with the standalone DPI bypass + Telegram
    // top-level tabs — the standalone surfaces stay live so users can
    // still reach the engine controls during the parallel-safe rollout.
    //
    // Platform-impossible substitutions (per plan):
    //   • Zapret on desktop = winws.exe Cygwin process with 5-section
    //     side-nav (Status / Strategy / Hosts / Filters / Advanced).
    //     Android has no winws.exe — uses sing-box's native tls_fragment
    //     outbound. We render an explainer + mode picker (off / standard
    //     / aggressive) + footer toggle. Mode flips drive
    //     AndroidDpiBypassInjector via the existing AndroidStorage key.
    //   • Telegram proxy on desktop = full TgProxy daemon. Android routes
    //     Telegram via the main VPN tunnel — no daemon. We render an
    //     explainer + "Open Telegram" deep-link (intent for
    //     org.telegram.messenger, falling through to the Play Store
    //     listing if Telegram isn't installed).

    private int _toolsSelectedSubTab; // 0 = Zapret, 1 = Telegram

    private Avalonia.Controls.Button? _toolsSubTabZapret;
    private Avalonia.Controls.Button? _toolsSubTabTelegram;
    private Control? _toolsZapretBody;
    private Control? _toolsTelegramBody;

    // Zapret body widgets — kept distinct from BuildDpiBypassTabContent's
    // _dpi* fields so the two surfaces (top-level DPI tab + merged Tools
    // sub-tab) can coexist without cross-binding. Both write to the same
    // AndroidStorage key, so flipping mode on either surface immediately
    // reflects on the other on its next reseed.
    private Avalonia.Controls.RadioButton? _toolsZapretModeOff;
    private Avalonia.Controls.RadioButton? _toolsZapretModeStandard;
    private Avalonia.Controls.RadioButton? _toolsZapretModeAggressive;
    private Ellipse? _toolsZapretFooterDot;
    private TextBlock? _toolsZapretFooterText;
    private Avalonia.Controls.Button? _toolsZapretFooterToggleBtn;

    // Tracks "user is mutating storage" so the radio change handler doesn't
    // fight itself when ReseedToolsTabState restores values from storage.
    private bool _toolsLoading;

    /// <summary>
    /// AND-ADV-TOOLS-PUBLIC (2026-05-10) — body content for the merged
    /// Tools tab inside the Advanced shell. Returns the inner sub-tab
    /// strip (Zapret | Telegram proxy) + bodies. The shell provides the
    /// title bar / close button / outer chrome.
    /// </summary>
    private Control BuildToolsTabContent()
    {
        var bg          = GetBrush("SurfaceAppBrush");
        var defaultB    = GetBrush("BorderDefaultBrush");
        var sunken      = GetBrush("SurfaceSunkenBrush");
        var radiusSm    = GetRadius("RadiusSm");
        var accentSolid = GetBrush("AccentSolidBrush");
        var accentOnSolid = GetBrush("AccentOnSolidBrush");
        var textS       = GetBrush("TextSecondaryBrush");

        // ── Sub-tab strip (Zapret | Telegram proxy) ──────────────────────
        _toolsSubTabZapret   = MakeSegmentButton(Localization.AdvToolsSubTabZapret,   active: true,
                                                 (_, _) => SelectToolsSubTab(0));
        _toolsSubTabTelegram = MakeSegmentButton(Localization.AdvToolsSubTabTelegram, active: false,
                                                 (_, _) => SelectToolsSubTab(1));

        var subTabRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 4,
            Margin = new Thickness(10, 8, 10, 0),
        };
        Grid.SetColumn(_toolsSubTabZapret,   0);
        Grid.SetColumn(_toolsSubTabTelegram, 1);
        subTabRow.Children.Add(_toolsSubTabZapret);
        subTabRow.Children.Add(_toolsSubTabTelegram);

        _toolsZapretBody   = BuildToolsZapretBody(bg, sunken, defaultB, accentSolid, accentOnSolid, radiusSm, textS);
        _toolsTelegramBody = BuildToolsTelegramBody(bg, sunken, defaultB, radiusSm, textS);
        _toolsTelegramBody.IsVisible = false;

        var bodyArea = new Grid
        {
            Children = { _toolsZapretBody, _toolsTelegramBody },
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(subTabRow, Dock.Top);
        dock.Children.Add(subTabRow);
        dock.Children.Add(bodyArea);

        UpdateToolsZapretFooterState();

        return new Border
        {
            Background = bg,
            Child = dock,
        };
    }

    /// <summary>
    /// Zapret sub-tab body: platform-impossible explainer banner + mode
    /// picker (off / standard / aggressive radios mirroring
    /// AndroidDpiBypassInjector.Mode) + footer toggle (Turn on / Turn off
    /// flips between off and the last-non-off mode). Layout mirrors the
    /// simplified Android DPI bypass surface — the desktop's 5-section
    /// side-nav doesn't apply since there's no winws.exe.
    /// </summary>
    private Control BuildToolsZapretBody(
        IBrush bg, IBrush sunken, IBrush defaultB,
        IBrush accentSolid, IBrush accentOnSolid,
        double radiusSm, IBrush textS)
    {
        var textP    = GetBrush("TextPrimaryBrush");
        var textM    = GetBrush("TextMutedBrush");
        var card     = GetBrush("SurfaceBaseBrush");
        var subtle   = GetBrush("BorderSubtleBrush");

        // Explainer banner. Mirrors the Status section blurb pattern from
        // BuildDpiBypassTabContent but with a wider, more prominent "this
        // is platform-different on Android" framing.
        var explainerTitle = new TextBlock
        {
            Text = Localization.AdvToolsSubTabZapret,
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Foreground = textP,
        };
        var explainerText = new TextBlock
        {
            Text = Localization.AdvToolsZapretAndroidExplainer,
            FontSize = 11,
            LineHeight = 16,
            Opacity = 0.8,
            Foreground = textS,
            TextWrapping = TextWrapping.Wrap,
        };
        var explainerCard = new Border
        {
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(radiusSm),
            Background = card,
            BorderBrush = subtle,
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { explainerTitle, explainerText },
            },
        };

        // Mode picker (radios). Bound to a single GroupName so flipping
        // one un-checks the others, just like a desktop RadioButton group.
        var currentMode = AndroidStorage.GetDpiBypassMode();
        _toolsZapretModeOff = MakeToolsZapretRadio(
            Localization.SettingsDpiBypassOff,        currentMode == "off",        textP);
        _toolsZapretModeStandard = MakeToolsZapretRadio(
            Localization.SettingsDpiBypassStandard,   currentMode == "standard",   textP);
        _toolsZapretModeAggressive = MakeToolsZapretRadio(
            Localization.SettingsDpiBypassAggressive, currentMode == "aggressive", textP);

        _toolsZapretModeOff.Checked        += OnToolsZapretModeChanged;
        _toolsZapretModeStandard.Checked   += OnToolsZapretModeChanged;
        _toolsZapretModeAggressive.Checked += OnToolsZapretModeChanged;

        var modePicker = new Border
        {
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(radiusSm),
            Background = card,
            BorderBrush = subtle,
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    _toolsZapretModeOff,
                    _toolsZapretModeStandard,
                    _toolsZapretModeAggressive,
                },
            },
        };

        var bodyStack = new StackPanel
        {
            Spacing = 10,
            Children = { explainerCard, modePicker },
        };

        var bodyScroller = new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(12, 10, 12, 12),
                Child = bodyStack,
            },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = bg,
        };

        // Footer (Apply bar). Mirrors BuildDpiBypassTabContent's footer:
        // status indicator (dot + label) on the left, toggle button on the
        // right. Tap the button to flip between off and the last-non-off
        // mode (defaults to standard for first-time toggles).
        _toolsZapretFooterDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _toolsZapretFooterText = new TextBlock
        {
            Text = ZapretStatusLabelForCurrentMode(),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = textS,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var footerStatusRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _toolsZapretFooterDot, _toolsZapretFooterText },
        };

        _toolsZapretFooterToggleBtn = new Avalonia.Controls.Button
        {
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 5),
            CornerRadius = new CornerRadius(radiusSm),
            Background = accentSolid,
            Foreground = accentOnSolid,
            BorderThickness = new Thickness(0),
            Content = currentMode == "off"
                ? Localization.AndroidDpiBypassFooterToggleOn
                : Localization.AndroidDpiBypassFooterToggleOff,
        };
        _toolsZapretFooterToggleBtn.Click += OnToolsZapretFooterToggleClicked;

        var footerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(footerStatusRow, 0);
        Grid.SetColumn(_toolsZapretFooterToggleBtn, 1);
        footerGrid.Children.Add(footerStatusRow);
        footerGrid.Children.Add(_toolsZapretFooterToggleBtn);

        var footer = new Border
        {
            Padding = new Thickness(14, 7, 14, 8),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = defaultB,
            Background = sunken,
            Child = footerGrid,
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(bodyScroller);

        return dock;
    }

    private Avalonia.Controls.RadioButton MakeToolsZapretRadio(
        string label, bool isChecked, IBrush textP)
    {
        return new Avalonia.Controls.RadioButton
        {
            GroupName = "ToolsZapretMode",
            IsChecked = isChecked,
            MinHeight = 0,
            Padding = new Thickness(4, 0),
            Content = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = textP,
                TextWrapping = TextWrapping.Wrap,
            },
        };
    }

    /// <summary>
    /// Telegram sub-tab body: platform-impossible explainer banner +
    /// "Open Telegram" deep-link button (intent for org.telegram.messenger,
    /// falling through to the Play Store if Telegram isn't installed) +
    /// GitHub credit footer.
    /// </summary>
    private Control BuildToolsTelegramBody(
        IBrush bg, IBrush sunken, IBrush defaultB, double radiusSm, IBrush textS)
    {
        var textP  = GetBrush("TextPrimaryBrush");
        var textM  = GetBrush("TextMutedBrush");
        var card   = GetBrush("SurfaceBaseBrush");
        var subtle = GetBrush("BorderSubtleBrush");
        var accentSolid   = GetBrush("AccentSolidBrush");
        var accentOnSolid = GetBrush("AccentOnSolidBrush");
        var accentFg      = GetBrush("AccentFgBrush");

        var explainerTitle = new TextBlock
        {
            Text = Localization.AdvToolsSubTabTelegram,
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Foreground = textP,
        };
        var explainerText = new TextBlock
        {
            Text = Localization.AdvToolsTelegramAndroidExplainer,
            FontSize = 11,
            LineHeight = 16,
            Opacity = 0.8,
            Foreground = textS,
            TextWrapping = TextWrapping.Wrap,
        };
        var explainerCard = new Border
        {
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(radiusSm),
            Background = card,
            BorderBrush = subtle,
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { explainerTitle, explainerText },
            },
        };

        var openTgBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvToolsOpenTelegram,
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Padding = new Thickness(0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = accentSolid,
            Foreground = accentOnSolid,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(radiusSm),
        };
        openTgBtn.Click += OnToolsOpenTelegramClicked;

        var githubBtn = new Avalonia.Controls.Button
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
            Foreground = accentFg,
        };
        githubBtn.Click += (_, _) =>
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

        var bodyStack = new StackPanel
        {
            Spacing = 10,
            Children = { explainerCard, openTgBtn, githubBtn },
        };

        return new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(12, 10, 12, 12),
                Child = bodyStack,
            },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = bg,
        };
    }

    private void SelectToolsSubTab(int index)
    {
        _toolsSelectedSubTab = index;
        if (_toolsZapretBody   is not null) _toolsZapretBody.IsVisible   = index == 0;
        if (_toolsTelegramBody is not null) _toolsTelegramBody.IsVisible = index == 1;
        StyleSegmentButton(_toolsSubTabZapret,   index == 0);
        StyleSegmentButton(_toolsSubTabTelegram, index == 1);
    }

    private void OnToolsZapretModeChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_toolsLoading) return;
        // Only react to the radio that was just checked — the others fire
        // Unchecked which we don't bind to.
        string mode = "off";
        if (_toolsZapretModeStandard?.IsChecked == true)        mode = "standard";
        else if (_toolsZapretModeAggressive?.IsChecked == true) mode = "aggressive";
        else                                                     mode = "off";

        AndroidStorage.SetDpiBypassMode(mode);

        // Mirror the change into the existing Settings overlay's ComboBox
        // and the standalone DPI Bypass tab's ComboBox so all surfaces
        // stay in sync. _settingsLoading + _toolsLoading guards prevent
        // change-handler ping-pong while we update each peer.
        var index = mode switch
        {
            "standard"   => 1,
            "aggressive" => 2,
            _            => 0,
        };
        if (_settingsDpiBypassMode is not null)
        {
            _settingsLoading = true;
            try { _settingsDpiBypassMode.SelectedIndex = index; }
            finally { _settingsLoading = false; }
        }
        if (_dpiStrategyComboBox is not null)
        {
            _dpiStrategyComboBox.SelectedIndex = index;
        }

        UpdateZapretChipFromState();
        UpdateToolsZapretFooterState();
        // Keep the standalone DPI tab's footer in lockstep too — it reads
        // the same AndroidStorage key and renders status text.
        UpdateDpiFooterState();
    }

    private void OnToolsZapretFooterToggleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var current = AndroidStorage.GetDpiBypassMode();
        var next = current == "off" ? "standard" : "off";
        AndroidStorage.SetDpiBypassMode(next);

        _toolsLoading = true;
        try
        {
            if (_toolsZapretModeOff is not null)        _toolsZapretModeOff.IsChecked        = next == "off";
            if (_toolsZapretModeStandard is not null)   _toolsZapretModeStandard.IsChecked   = next == "standard";
            if (_toolsZapretModeAggressive is not null) _toolsZapretModeAggressive.IsChecked = next == "aggressive";
        }
        finally { _toolsLoading = false; }

        var index = next switch
        {
            "standard"   => 1,
            "aggressive" => 2,
            _            => 0,
        };
        if (_settingsDpiBypassMode is not null)
        {
            _settingsLoading = true;
            try { _settingsDpiBypassMode.SelectedIndex = index; }
            finally { _settingsLoading = false; }
        }
        if (_dpiStrategyComboBox is not null)
            _dpiStrategyComboBox.SelectedIndex = index;

        UpdateZapretChipFromState();
        UpdateToolsZapretFooterState();
        UpdateDpiFooterState();
    }

    private void UpdateToolsZapretFooterState()
    {
        var mode = AndroidStorage.GetDpiBypassMode();
        var enabled = mode != "off";
        if (_toolsZapretFooterText is not null)
            _toolsZapretFooterText.Text = ZapretStatusLabelForCurrentMode();
        if (_toolsZapretFooterDot is not null)
            _toolsZapretFooterDot.Fill = GetBrush(enabled
                ? "SuccessSolidBrush" : "TextMutedBrush");
        if (_toolsZapretFooterToggleBtn is not null)
            _toolsZapretFooterToggleBtn.Content = enabled
                ? Localization.AndroidDpiBypassFooterToggleOff
                : Localization.AndroidDpiBypassFooterToggleOn;
    }

    /// <summary>
    /// Re-seed Tools sub-tab state from persistent storage. Called on tab
    /// activation by the AdvancedShell switcher so values stay fresh when
    /// the user mutates DPI bypass mode from the kebab Settings card or
    /// the standalone DPI bypass tab between Tools-tab visits.
    /// </summary>
    private void ReseedToolsTabState()
    {
        var mode = AndroidStorage.GetDpiBypassMode();
        _toolsLoading = true;
        try
        {
            if (_toolsZapretModeOff is not null)        _toolsZapretModeOff.IsChecked        = mode == "off";
            if (_toolsZapretModeStandard is not null)   _toolsZapretModeStandard.IsChecked   = mode == "standard";
            if (_toolsZapretModeAggressive is not null) _toolsZapretModeAggressive.IsChecked = mode == "aggressive";
        }
        finally { _toolsLoading = false; }
        UpdateToolsZapretFooterState();
    }

    /// <summary>
    /// Open the Telegram app. Intent priority:
    ///   1) `PackageManager.GetLaunchIntentForPackage("org.telegram.messenger")`
    ///      — opens the installed Telegram client.
    ///   2) Fallback to `market://details?id=org.telegram.messenger` — opens
    ///      the Play Store listing (or the user's preferred app store via
    ///      ActionView for the market URI).
    ///   3) If neither succeeds (no Play Store either — rare on AOSP roms),
    ///      surface a toast so the user understands why nothing happened.
    /// </summary>
    private void OnToolsOpenTelegramClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var pm  = ctx?.PackageManager;
            var launchIntent = pm?.GetLaunchIntentForPackage("org.telegram.messenger");
            if (launchIntent is not null)
            {
                launchIntent.SetFlags(global::Android.Content.ActivityFlags.NewTask);
                ctx!.StartActivity(launchIntent);
                return;
            }

            // Fallback — Play Store listing.
            ShowMenuFeedback(Localization.AdvToolsTelegramNotInstalled);
            var marketIntent = new global::Android.Content.Intent(
                global::Android.Content.Intent.ActionView,
                global::Android.Net.Uri.Parse("market://details?id=org.telegram.messenger"));
            marketIntent.SetFlags(global::Android.Content.ActivityFlags.NewTask);
            ctx!.StartActivity(marketIntent);
        }
        catch
        {
            // Both attempts failed — non-fatal. The toast above already
            // explains the situation if the launch intent was null.
        }
    }
}
