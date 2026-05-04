using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 Android port — Phase 1.H view (2026-05-04).
///
/// <para>Phase 1.H major UX upgrade: parity with desktop app's main
/// connection flow.
/// <list type="bullet">
///   <item><b>Subscription URL</b> input — paste an https:// link, app
///   fetches the server pool via <see cref="SubscriptionFetcher"/>,
///   shows result in a ListBox. User picks one server → that becomes
///   active.</item>
///   <item><b>Manual <c>vless://</c></b> still supported — same input
///   field auto-detects URI vs URL.</item>
///   <item><b>RU/EN bilingual</b> — labels mirror desktop's
///   <c>Strings.cs</c> patterns. Auto-detects from <see cref="Localization"/>
///   on init, user can flip via top-right toggle.</item>
///   <item><b>Dark theme by default</b> — matches desktop's tuned
///   palette. Light variant exposed via Settings (Phase 1.I).</item>
/// </list></para>
///
/// <para>Phase 1.D state model (intent-only Connect/Disconnect) and
/// Phase 1.G UI scaffolding still apply. Phase 1.I will swap intent
/// for libbox-callback-driven status sync.</para>
/// </summary>
public partial class AndroidApp : Avalonia.Application
{
    private TextBlock? _statusBlock;
    private Avalonia.Controls.Button? _toggleButton;
    private TextBox? _serverInputBox;
    private TextBlock? _serverInputStatus;
    private Avalonia.Controls.Button? _saveServerButton;
    private Avalonia.Controls.Button? _refreshSubButton;
    private ListBox? _serverList;
    private TextBlock? _serverListHeader;
    private Avalonia.Controls.Button? _languageToggle;
    private TextBlock? _titleBlock;
    private TextBlock? _subtitleBlock;
    private TextBlock? _serverHeaderBlock;
    private TextBlock? _hintBlock;

    private List<VlessServerEntry> _cachedServers = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply theme + locale BEFORE building the view so labels are
        // localized correctly on first paint.
        Localization.LoadFromStorage();
        ApplyTheme();

        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = BuildPhase2View();
            MainActivity.IntentChanged += OnIntentChanged;
            UpdateButtonState(MainActivity.IntendedConnected);
            // Restore cached server list from previous session.
            ReloadServerList();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyTheme()
    {
        var pref = AndroidStorage.GetTheme();
        RequestedThemeVariant = pref switch
        {
            "light" => ThemeVariant.Light,
            "system" => ThemeVariant.Default,
            _ => ThemeVariant.Dark, // default for parity with desktop
        };
    }

    // ── Token resolution ───────────────────────────────────────────────

    /// <summary>
    /// Resolve a SolidColorBrush from the merged Tokens.axaml at the
    /// current ActualThemeVariant. Falls back to Brushes.Transparent if
    /// the key is missing — should not happen in practice (every desktop
    /// token has both a Color and a Brush variant under both Light and
    /// Dark dictionaries).
    /// </summary>
    private IBrush GetBrush(string key)
    {
        if (Resources.TryGetResource(key, ActualThemeVariant, out var v) && v is IBrush b)
            return b;
        return Brushes.Transparent;
    }

    private double GetRadius(string key)
    {
        // Tokens.axaml stores radii as `<sys:Double x:Key="RadiusXs">3</sys:Double>`
        // outside ThemeDictionaries (theme-invariant), so the lookup uses
        // ActualThemeVariant but Avalonia's resource resolver still finds
        // the global value.
        if (Resources.TryGetResource(key, ActualThemeVariant, out var v))
        {
            return v switch
            {
                double d => d,
                int i => i,
                _ => 8.0
            };
        }
        return 8.0;
    }

    /// <summary>
    /// v3.0 Phase 2 view (2026-05-04) — desktop visual parity.
    /// Card-based layout with shared Arctic design tokens. Tokens
    /// come from VPNRouter.App/Styles/Tokens.axaml linked at build
    /// time. The whole tree uses semantic brushes (SurfaceAppBrush,
    /// AccentSolidBrush, BorderSubtleBrush, TextMutedBrush, etc.) so
    /// theme variants and palette tweaks propagate automatically.
    ///
    /// <para>Layout: header bar (title + RU/EN toggle) → status card
    /// (status text + Connect button) → server card (input + Save/QR
    /// + Refresh + ListBox) → bottom hint. Each card has padding,
    /// rounded corners (RadiusLg), subtle border. Mimics desktop
    /// SimplePage's card pattern but stacked vertically for narrow
    /// phone screen.</para>
    /// </summary>
    private Control BuildPhase2View()
    {
        var bgBrush = GetBrush("SurfaceAppBrush");
        var cardBrush = GetBrush("SurfaceBaseBrush");
        var subtleBorder = GetBrush("BorderSubtleBrush");
        var defaultBorder = GetBrush("BorderDefaultBrush");
        var textPrimary = GetBrush("TextPrimaryBrush");
        var textSecondary = GetBrush("TextSecondaryBrush");
        var textMuted = GetBrush("TextMutedBrush");
        var accentSolid = GetBrush("AccentSolidBrush");
        var accentOnSolid = GetBrush("AccentOnSolidBrush");
        var accentBgSubtle = GetBrush("AccentBgSubtleBrush");
        var accentBorder = GetBrush("AccentBorderBrush");
        var accentFg = GetBrush("AccentFgBrush");
        var radiusLg = GetRadius("RadiusLg");      // 10
        var radiusMd = GetRadius("RadiusMd");      // 8
        var radiusSm = GetRadius("RadiusSm");      // 6

        // ── Header (title + language toggle) ────────────────────────────
        _titleBlock = new TextBlock
        {
            Text = Localization.Title,
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            Foreground = textPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _languageToggle = new Avalonia.Controls.Button
        {
            Content = Localization.LangToggleLabel,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Padding = new Thickness(14, 6),
            CornerRadius = new CornerRadius(radiusSm),
            Background = accentBgSubtle,
            Foreground = accentFg,
            BorderBrush = accentBorder,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _languageToggle.Click += OnLanguageToggleClicked;

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(20, 24, 20, 0),
        };
        Grid.SetColumn(_titleBlock, 0);
        Grid.SetColumn(_languageToggle, 1);
        headerGrid.Children.Add(_titleBlock);
        headerGrid.Children.Add(_languageToggle);

        _subtitleBlock = new TextBlock
        {
            Text = Localization.Subtitle,
            FontSize = 12,
            Foreground = textMuted,
            Margin = new Thickness(20, 6, 20, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        // ── Status + Connect card ───────────────────────────────────────
        _statusBlock = new TextBlock
        {
            Text = Localization.StatusDisconnected,
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            Foreground = textPrimary,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _toggleButton = new Avalonia.Controls.Button
        {
            Content = Localization.ButtonConnect,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(48, 14),
            CornerRadius = new CornerRadius(radiusMd),
            Background = accentSolid,
            Foreground = accentOnSolid,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
        };
        _toggleButton.Click += OnToggleClicked;

        var statusCard = new Border
        {
            Background = cardBrush,
            BorderBrush = subtleBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusLg),
            Padding = new Thickness(20, 24),
            Margin = new Thickness(20, 16, 20, 0),
            Child = new StackPanel
            {
                Children = { _statusBlock, _toggleButton }
            }
        };

        // ── Server card: input + Save/QR/Refresh + ListBox ──────────────
        _serverHeaderBlock = new TextBlock
        {
            Text = Localization.ServerHeader,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = textSecondary,
            Margin = new Thickness(0, 0, 0, 8),
        };

        _serverInputBox = new TextBox
        {
            Watermark = Localization.ServerInputWatermark,
            FontSize = 13,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64,
            CornerRadius = new CornerRadius(radiusSm),
            BorderBrush = defaultBorder,
            BorderThickness = new Thickness(1),
            // Phase 2 keyboard fix: TextBox is NOT auto-focused on view
            // attach. The Activity-level WindowSoftInputMode=StateHidden
            // is the primary mechanism, but Focusable=true at the property
            // level still allows tap-to-focus when user explicitly chooses.
        };
        var existingSub = AndroidStorage.GetSubscriptionUrl();
        var existingUri = AndroidStorage.GetVlessUri();
        _serverInputBox.Text = existingSub ?? existingUri ?? string.Empty;

        _saveServerButton = StyledSecondaryButton(Localization.ButtonSave);
        _saveServerButton.Click += OnSaveServerClicked;

        _refreshSubButton = StyledSecondaryButton(Localization.ButtonRefresh);
        _refreshSubButton.Margin = new Thickness(8, 0, 0, 0);
        _refreshSubButton.Click += OnRefreshClicked;

        // Phase 2.4 — QR scan button (Bonus). Currently a placeholder
        // that surfaces a "QR scan coming soon" toast — full ZXing
        // integration deferred to Phase 2.5 to keep this iteration
        // shippable. Button is wired and clickable for UX feedback.
        var qrButton = StyledSecondaryButton("📷 QR");
        qrButton.Margin = new Thickness(8, 0, 0, 0);
        qrButton.Click += OnScanQrClicked;

        var buttonRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { _saveServerButton, qrButton, _refreshSubButton },
        };

        _serverInputStatus = new TextBlock
        {
            Text = Localization.ServerInputHintInitial,
            FontSize = 11,
            Foreground = textMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };

        _serverListHeader = new TextBlock
        {
            Text = Localization.AvailableServers,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = textSecondary,
            Margin = new Thickness(0, 20, 0, 8),
            IsVisible = false,
        };

        _serverList = new ListBox
        {
            MaxHeight = 280,
            IsVisible = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        _serverList.SelectionChanged += OnServerSelectionChanged;

        var serverCard = new Border
        {
            Background = cardBrush,
            BorderBrush = subtleBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusLg),
            Padding = new Thickness(20, 18),
            Margin = new Thickness(20, 16, 20, 0),
            Child = new StackPanel
            {
                Children =
                {
                    _serverHeaderBlock,
                    _serverInputBox,
                    buttonRow,
                    _serverInputStatus,
                    _serverListHeader,
                    _serverList,
                }
            }
        };

        // ── Bottom hint ─────────────────────────────────────────────────
        _hintBlock = new TextBlock
        {
            Text = Localization.HintTunnel,
            FontSize = 11,
            Foreground = textMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(20, 24, 20, 24),
            TextWrapping = TextWrapping.Wrap,
        };

        var stack = new StackPanel
        {
            Children =
            {
                headerGrid,
                _subtitleBlock,
                statusCard,
                serverCard,
                _hintBlock,
            }
        };

        var scrollWrapper = new ScrollViewer
        {
            Content = stack,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = bgBrush,
        };

        return scrollWrapper;
    }

    /// <summary>
    /// Style helper for "secondary" buttons (Save, Refresh, QR) — outline
    /// with subtle border, neutral surface bg, semi-bold label. Mirrors
    /// desktop's "secondary action" button pattern.
    /// </summary>
    private Avalonia.Controls.Button StyledSecondaryButton(string label)
    {
        return new Avalonia.Controls.Button
        {
            Content = label,
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceRaisedBrush"),
            Foreground = GetBrush("TextPrimaryBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
        };
    }

    /// <summary>
    /// Phase 2.4 placeholder — full ZXing camera scanner is queued
    /// for Phase 2.5. For now this surfaces a "coming soon" hint
    /// in the input-status line so users discover the planned feature.
    /// </summary>
    private void OnScanQrClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_serverInputStatus is null) return;
        _serverInputStatus.Text = Localization.QrComingSoon;
        _serverInputStatus.Opacity = 0.85;
    }

    // ── Connect / Disconnect ────────────────────────────────────────────

    private void OnToggleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var activity = MainActivity.Instance;
        if (activity is null) return;

        if (MainActivity.IntendedConnected)
            activity.RequestDisconnect();
        else
            activity.RequestConnect();
    }

    private void OnIntentChanged(bool connected)
    {
        Dispatcher.UIThread.Post(() => UpdateButtonState(connected));
    }

    private void UpdateButtonState(bool connected)
    {
        if (_toggleButton is null || _statusBlock is null) return;

        if (connected)
        {
            _toggleButton.Content = Localization.ButtonDisconnect;
            _statusBlock.Text = Localization.StatusConnected;
        }
        else
        {
            _toggleButton.Content = Localization.ButtonConnect;
            _statusBlock.Text = Localization.StatusDisconnected;
        }
    }

    // ── Save / Refresh server input ─────────────────────────────────────

    /// <summary>
    /// Auto-detect input type:
    ///   - <c>vless://...</c> → manual single server (validate + store)
    ///   - <c>http(s)://...</c> → subscription URL (store, then user taps Refresh)
    ///   - empty → clear stored values
    /// Save does NOT fetch — Refresh does. This split lets the user save
    /// a subscription URL without immediately hitting the network.
    /// </summary>
    private void OnSaveServerClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_serverInputBox is null || _serverInputStatus is null) return;

        var raw = (_serverInputBox.Text ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(raw))
        {
            // Clear everything — fall back to placeholder on Connect.
            AndroidStorage.SetVlessUri(null);
            AndroidStorage.SetSubscriptionUrl(null);
            AndroidStorage.SetServers(null);
            AndroidStorage.SetSelectedServerName(null);
            _cachedServers = new List<VlessServerEntry>();
            UpdateServerListView();
            _serverInputStatus.Text = Localization.SaveStatusCleared;
            _serverInputStatus.Opacity = 0.65;
            return;
        }

        if (raw.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
        {
            // Manual single server.
            try
            {
                var parsed = VlessUriParser.Parse(raw);
                if (string.IsNullOrEmpty(parsed.Server) || parsed.Port <= 0)
                {
                    _serverInputStatus.Text = Localization.SaveStatusUriBadHost;
                    _serverInputStatus.Opacity = 0.95;
                    return;
                }
                AndroidStorage.SetVlessUri(raw);
                // Manual URI wins over subscription selection.
                AndroidStorage.SetSubscriptionUrl(null);
                AndroidStorage.SetServers(null);
                AndroidStorage.SetSelectedServerName(null);
                _cachedServers = new List<VlessServerEntry>();
                UpdateServerListView();
                _serverInputStatus.Text = string.Format(Localization.SaveStatusUriOk, parsed.Server, parsed.Port);
                _serverInputStatus.Opacity = 0.65;
            }
            catch (Exception ex)
            {
                _serverInputStatus.Text = string.Format(Localization.SaveStatusUriInvalid, ex.Message);
                _serverInputStatus.Opacity = 0.95;
            }
            return;
        }

        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Subscription URL — just store. Refresh button does the fetch.
            AndroidStorage.SetSubscriptionUrl(raw);
            AndroidStorage.SetVlessUri(null);
            _serverInputStatus.Text = Localization.SaveStatusSubStored;
            _serverInputStatus.Opacity = 0.65;
            return;
        }

        _serverInputStatus.Text = Localization.SaveStatusUnknown;
        _serverInputStatus.Opacity = 0.95;
    }

    /// <summary>
    /// Fetch the stored subscription URL and populate the server list.
    /// No-op if no URL is stored. Async/await stays on the dispatcher
    /// thread thanks to Avalonia's SynchronizationContext.
    /// </summary>
    private async void OnRefreshClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_serverInputStatus is null || _refreshSubButton is null) return;

        var url = AndroidStorage.GetSubscriptionUrl();

        // If user typed a URL but hasn't tapped Save, use the live text.
        if (string.IsNullOrEmpty(url) && _serverInputBox is not null)
        {
            var raw = (_serverInputBox.Text ?? string.Empty).Trim();
            if (raw.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                AndroidStorage.SetSubscriptionUrl(raw);
                url = raw;
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            _serverInputStatus.Text = Localization.RefreshNeedsUrl;
            _serverInputStatus.Opacity = 0.85;
            return;
        }

        _refreshSubButton.IsEnabled = false;
        _serverInputStatus.Text = Localization.RefreshFetching;
        _serverInputStatus.Opacity = 0.65;

        try
        {
            var servers = await SubscriptionFetcher.FetchAsync(url, logger: null, ct: System.Threading.CancellationToken.None).ConfigureAwait(true);
            var list = new List<VlessServerEntry>(servers);

            AndroidStorage.SetServers(list);
            _cachedServers = list;
            UpdateServerListView();

            // Auto-select first server if nothing's currently selected.
            var prevSelected = AndroidStorage.GetSelectedServerName();
            var hasPrev = !string.IsNullOrEmpty(prevSelected) &&
                          list.Exists(s => string.Equals(s.Name, prevSelected, StringComparison.OrdinalIgnoreCase));
            if (!hasPrev && list.Count > 0)
            {
                AndroidStorage.SetSelectedServerName(list[0].Name);
                if (_serverList is not null)
                    _serverList.SelectedIndex = 0;
            }

            _serverInputStatus.Text = string.Format(Localization.RefreshOk, list.Count);
            _serverInputStatus.Opacity = 0.65;
        }
        catch (Exception ex)
        {
            _serverInputStatus.Text = string.Format(Localization.RefreshFailed, ex.Message);
            _serverInputStatus.Opacity = 0.95;
        }
        finally
        {
            _refreshSubButton.IsEnabled = true;
        }
    }

    private void OnServerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_serverList is null || _serverList.SelectedItem is not VlessServerEntry entry) return;
        AndroidStorage.SetSelectedServerName(entry.Name);
        if (_serverInputStatus is not null)
        {
            _serverInputStatus.Text = string.Format(Localization.ServerSelected, entry.Name, entry.Server, entry.Port);
            _serverInputStatus.Opacity = 0.65;
        }
    }

    private void ReloadServerList()
    {
        _cachedServers = AndroidStorage.GetServers();
        UpdateServerListView();
    }

    private void UpdateServerListView()
    {
        if (_serverList is null || _serverListHeader is null) return;

        var visible = _cachedServers.Count > 0;
        _serverList.IsVisible = visible;
        _serverListHeader.IsVisible = visible;

        _serverList.ItemsSource = _cachedServers;
        // ItemTemplate not bound here — VlessServerEntry.ToString fallback
        // is verbose. Configure DisplayMember-style binding via simple
        // template:
        _serverList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<VlessServerEntry>(
            (item, _) =>
            {
                var name = new TextBlock
                {
                    Text = string.IsNullOrEmpty(item?.Name) ? (item?.Server ?? "?") : item.Name,
                    FontSize = 14,
                    FontWeight = FontWeight.Medium,
                };
                var sub = new TextBlock
                {
                    Text = $"{item?.Server}:{item?.Port}  ·  {item?.Protocol ?? "vless"}",
                    FontSize = 11,
                    Opacity = 0.6,
                };
                return new StackPanel
                {
                    Spacing = 2,
                    Margin = new Thickness(8, 6),
                    Children = { name, sub }
                };
            },
            supportsRecycling: true);

        // Restore previous selection.
        var sel = AndroidStorage.GetSelectedServerName();
        if (!string.IsNullOrEmpty(sel))
        {
            for (int i = 0; i < _cachedServers.Count; i++)
            {
                if (string.Equals(_cachedServers[i].Name, sel, StringComparison.OrdinalIgnoreCase))
                {
                    _serverList.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    // ── Language toggle ─────────────────────────────────────────────────

    private void OnLanguageToggleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Localization.ToggleAndPersist();
        // Refresh all visible localized strings.
        if (_titleBlock is not null) _titleBlock.Text = Localization.Title;
        if (_subtitleBlock is not null) _subtitleBlock.Text = Localization.Subtitle;
        if (_serverHeaderBlock is not null) _serverHeaderBlock.Text = Localization.ServerHeader;
        if (_serverInputBox is not null) _serverInputBox.Watermark = Localization.ServerInputWatermark;
        if (_saveServerButton is not null) _saveServerButton.Content = Localization.ButtonSave;
        if (_refreshSubButton is not null) _refreshSubButton.Content = Localization.ButtonRefresh;
        if (_serverListHeader is not null) _serverListHeader.Text = Localization.AvailableServers;
        if (_languageToggle is not null) _languageToggle.Content = Localization.LangToggleLabel;
        if (_hintBlock is not null) _hintBlock.Text = Localization.HintTunnel;
        // Phase 1.H polish: refresh the input-status line too. We can't
        // know if it currently shows the initial hint vs a save/refresh
        // result, so we always reset it to the initial localized hint —
        // a small UX trade (loses last action message) for full RU/EN
        // coverage.
        if (_serverInputStatus is not null)
        {
            _serverInputStatus.Text = Localization.ServerInputHintInitial;
            _serverInputStatus.Opacity = 0.65;
        }
        UpdateButtonState(MainActivity.IntendedConnected);
    }
}
