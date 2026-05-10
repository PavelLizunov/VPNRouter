using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Orientation = Avalonia.Layout.Orientation;

namespace VPNRouter.Android;

/// <summary>
/// v2.32.0 (2026-05-07) — multi-subscription management UI, ported from
/// desktop <c>VPNRouter.App/Views/Pages/SubscribePage.axaml</c> + the
/// related VM commands (<c>AddSubscriptionAsync</c>,
/// <c>RemoveSubscription</c>, <c>RefreshSubscriptionAsync</c>,
/// <c>RefreshAllSubscriptionsAsync</c> in MainWindowViewModel.cs:3729-3844).
///
/// <para>Pre-2.32.0 the Android port supported a single subscription URL
/// only (<see cref="AndroidStorage.GetSubscriptionUrl"/>) — desktop has
/// always supported a list. This file adds the same UI surface: cards
/// per subscription with name + URL+Ns+timestamp metadata, per-card
/// refresh / delete (2-tap confirm) / edit-URL-inline, plus an add form
/// at the bottom and a "Refresh all" button. Backed by
/// <see cref="AndroidStorage.GetSubscriptions"/> which migrates the
/// legacy single-URL key on first read.</para>
///
/// <para>Triggered from the existing "Расширенные настройки" card on
/// the SimplePage (v3.0 Phase 3) — pre-2.32.0 that card was a no-op
/// placeholder.</para>
/// </summary>
public partial class AndroidApp
{
    // AND-MIGRATE-OVERLAYS (2026-05-09): the standalone Subscribe overlay
    // is gone — content moves into the Advanced shell as the Subscriptions
    // tab. Field set is the same minus the overlay/title/close widgets the
    // shell now owns.
    private StackPanel? _subsListStack;
    private TextBlock? _subsEmptyHint;
    private TextBox? _subsNewName;
    private TextBox? _subsNewUrl;
    private Avalonia.Controls.Button? _subsAddBtn;
    private Avalonia.Controls.Button? _subsRefreshAllBtn;
    private TextBlock? _subsSectionLabel;
    private TextBlock? _subsRefreshAllStatus;

    // ── AND-ADV-SERVERS-SUBSCRIBE Phase B (2026-05-10) ─────────────────
    // Aggregated server list at the TOP of the Subscribe tab (mirrors
    // desktop SubscribePage rows 109-197): 4-column table aggregating
    // every enabled subscription's servers. Below it sits the middle
    // action row (Test all / Deep verify / Refresh all) before the
    // existing Subscriptions section + add form move down.
    private StackPanel? _subsAggListStack;
    private TextBlock? _subsAggEmptyHint;
    private TextBlock? _subsAggColServer;
    private TextBlock? _subsAggColIp;
    private TextBlock? _subsAggColPing;
    private TextBlock? _subsAggColPort;
    private Avalonia.Controls.Button? _subsAggTestAllBtn;
    private Avalonia.Controls.Button? _subsAggDeepVerifyBtn;
    private TextBlock? _subsAggStatusText;
    private TextBlock? _subsAggSectionHeaderLabel;

    /// <summary>Per-server in-progress test flags for the aggregated
    /// Subscribe-tab list. Separate from the Servers tab's
    /// <c>_srvTestingKeys</c> so the two surfaces don't fight each
    /// other when the user kicks off Test all on both.</summary>
    private readonly HashSet<string> _subsAggTestingKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>In-memory mirror of the persisted server-test results
    /// dict, refreshed on each tab activation. Mutated by
    /// <see cref="OnSubsAggTestAllClicked"/> +
    /// <see cref="TestSingleAggregatedServerAsync"/> so progressive
    /// updates render between flushes — the
    /// <see cref="AndroidStorage.SetServerTestResults"/> persist happens
    /// at end-of-batch (not per-result) to keep SharedPreferences I/O
    /// bounded.</summary>
    private Dictionary<string, AndroidStorage.ServerTestResultDto> _subsAggResults =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cancellation source for the aggregated-list Test all
    /// batch. Cancelled when the shell closes or the Subscribe tab is
    /// switched away from.</summary>
    private CancellationTokenSource? _subsAggTestAllCts;

    /// <summary>
    /// In-memory mirror of the persisted subscription list. Modified by
    /// add / remove / refresh handlers, then flushed via
    /// <see cref="AndroidStorage.SetSubscriptions"/> which also rebuilds
    /// the aggregated server pool keyed by the connect path.
    /// </summary>
    private List<SubscriptionEntry> _subs = new();

    /// <summary>
    /// Per-card view-state. Tracks which card is mid-refresh (for spinner
    /// visibility), which card is mid-delete-confirm (2-tap pattern
    /// matching kebab Reset), and which card has the inline URL editor
    /// open. Indexed by SubscriptionEntry.Id since list reorder/recreate
    /// would invalidate plain indices.
    /// </summary>
    private readonly HashSet<string> _refreshingIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingDeleteId;
    private string? _editingId;
    private DateTime _lastDeleteTapAt = DateTime.MinValue;

    /// <summary>
    /// AND-MIGRATE-OVERLAYS (2026-05-09) + AND-ADV-SERVERS-SUBSCRIBE
    /// (Phase B, 2026-05-10) — body content for the Subscriptions tab
    /// inside the Advanced shell. Layout mirrors desktop SubscribePage
    /// (top → bottom):
    ///   1. Aggregated server list (4-column table from every enabled sub)
    ///   2. Middle action row (Test all + Deep verify + Refresh all)
    ///   3. "Subscriptions" section header
    ///   4. Subscriptions card list (existing per-card UI)
    ///   5. Add-subscription form (Name + URL + Add)
    /// </summary>
    private Control BuildSubscribeTabContent()
    {
        // ── 1. Aggregated server list (TOP) ────────────────────────────
        var aggServerSection = BuildSubscribeAggregatedServerSection();

        // ── 2. Middle action row (Test all + Deep verify + Refresh all)
        var middleActionRow = BuildSubscribeFooterActions();

        // ── 3. Section header — "Subscriptions"  (left, no Refresh all
        //       button anymore — that moved to the middle action row).
        _subsSectionLabel = new TextBlock
        {
            Text = Localization.SubscriptionsSection,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = GetBrush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _subsRefreshAllStatus = new TextBlock
        {
            Text = string.Empty,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var sectionHeaderBorder = new Border
        {
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Background = GetBrush("SurfaceBaseBrush"),
            Padding = new Thickness(10, 6, 10, 6),
            Child = new StackPanel
            {
                Spacing = 0,
                Children = { _subsSectionLabel, _subsRefreshAllStatus },
            },
        };

        // ── 4. Subscriptions card list (existing per-card UI) ──────────
        _subsListStack = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(12, 8, 12, 8),
        };
        _subsEmptyHint = new TextBlock
        {
            Text = Localization.LblAddSubscriptionHint,
            FontSize = 11,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
            IsVisible = false,
        };
        var listRoot = new StackPanel
        {
            Spacing = 0,
            Children = { _subsListStack, _subsEmptyHint },
        };
        var subsListScroller = new ScrollViewer
        {
            Content = listRoot,
            // Cap the card list height so it doesn't push the add-form
            // off-screen on tall sub lists. Mirrors desktop's MaxHeight=130
            // pattern in SubscribePage.axaml line 270.
            MaxHeight = 180,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = GetBrush("SurfaceAppBrush"),
        };

        // ── 5. Add-subscription form (bottom) ──────────────────────────
        //     Mirrors desktop SubscribePage.axaml lines 338-361 (Row 4):
        //     "100,*,Auto" name + URL + Add button with top divider.
        _subsNewName = new TextBox
        {
            Watermark = Localization.AdvSubscribeNameLabel,
            FontSize = 10,
            Padding = new Thickness(6, 4),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
        };
        _subsNewUrl = new TextBox
        {
            Watermark = Localization.AdvSubscribeUrlLabel,
            FontSize = 10,
            FontFamily = new FontFamily("monospace"),
            Padding = new Thickness(6, 4),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
        };
        _subsAddBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AddSubscription,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(12, 5),
            Background = GetBrush("AccentSolidBrush"),
            Foreground = GetBrush("AccentOnSolidBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        };
        _subsAddBtn.Click += OnSubsAddClicked;

        var addFormRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("100,*,Auto"),
            ColumnSpacing = 4,
        };
        Grid.SetColumn(_subsNewName, 0);
        Grid.SetColumn(_subsNewUrl, 1);
        Grid.SetColumn(_subsAddBtn, 2);
        addFormRow.Children.Add(_subsNewName);
        addFormRow.Children.Add(_subsNewUrl);
        addFormRow.Children.Add(_subsAddBtn);

        var addFormBorder = new Border
        {
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = GetBrush("SurfaceBaseBrush"),
            Padding = new Thickness(10, 6, 10, 10),
            Child = addFormRow,
        };

        // ── Compose: aggregated server section fills the top space, the
        //    rest of the chrome docks below it. DockPanel adds the
        //    last child as the fill child, so addFormBorder /
        //    subsListScroller / sectionHeaderBorder / middleActionRow all
        //    dock Bottom-to-Top, then aggServerSection takes the rest.
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(addFormBorder, Dock.Bottom);
        DockPanel.SetDock(subsListScroller, Dock.Bottom);
        DockPanel.SetDock(sectionHeaderBorder, Dock.Bottom);
        DockPanel.SetDock(middleActionRow, Dock.Bottom);
        dock.Children.Add(addFormBorder);
        dock.Children.Add(subsListScroller);
        dock.Children.Add(sectionHeaderBorder);
        dock.Children.Add(middleActionRow);
        dock.Children.Add(aggServerSection);

        return new Border
        {
            Background = GetBrush("SurfaceAppBrush"),
            Child = dock,
        };
    }

    /// <summary>
    /// Aggregated server table at the TOP of the Subscribe tab — desktop
    /// Aggregated server table for the Subscribe tab. Mobile design
    /// 2026-05-11 collapsed the 6-col desktop strip (Server / IP / Ping
    /// / Port) into 4 columns: radio · name+meta · ping · refresh. IP +
    /// port now live in the row's meta-line. Header strip keeps just
    /// "Server" and "Ping" — the other captions were redundant with the
    /// inline meta-line and ate horizontal real estate.
    /// </summary>
    private DockPanel BuildSubscribeAggregatedServerSection()
    {
        // ── Column header strip — matches desktop's tiny SemiBold caps. ──
        _subsAggColServer = new TextBlock
        {
            Text = Localization.ColServer,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _subsAggColPing = new TextBlock
        {
            Text = Localization.ColPing,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(_subsAggColPing, Localization.ColPingTooltip);
        // _subsAggColIp / _subsAggColPort still live on the class as fields
        // for backwards compatibility; the dedicated columns were retired
        // by the mobile redesign so we leave them null-but-instantiated
        // (any incidental ToolTip / IsVisible reads stay safe). Mobile
        // design grid matches the row layout: 14 (radio) · * (name+meta)
        // · Auto (ping) · 24 (refresh).
        _subsAggColIp = new TextBlock { IsVisible = false };
        _subsAggColPort = new TextBlock { IsVisible = false };
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("14,*,Auto,24"),
            ColumnSpacing = 8,
            Margin = new Thickness(8, 2, 8, 4),
        };
        Grid.SetColumn(_subsAggColServer, 1);
        Grid.SetColumn(_subsAggColPing, 2);
        headerGrid.Children.Add(_subsAggColServer);
        headerGrid.Children.Add(_subsAggColPing);

        var headerHost = new Border
        {
            Margin = new Thickness(12, 8, 12, 0),
            Child = headerGrid,
        };

        // ── Server list body + empty state ────────────────────────────
        _subsAggListStack = new StackPanel { Spacing = 0 };
        _subsAggEmptyHint = new TextBlock
        {
            Text = Localization.AdvSubscribeAggregatedEmpty,
            FontSize = 11,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(16, 24, 16, 0),
            IsVisible = false,
        };
        var listRoot = new StackPanel
        {
            Spacing = 0,
            Children = { _subsAggListStack, _subsAggEmptyHint },
        };
        var listScroller = new ScrollViewer
        {
            Content = listRoot,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var listCard = new Border
        {
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceBaseBrush"),
            Margin = new Thickness(12, 0, 12, 4),
            Child = listScroller,
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(headerHost, Dock.Top);
        dock.Children.Add(headerHost);
        dock.Children.Add(listCard);
        return dock;
    }

    /// <summary>
    /// Middle action row for the Subscribe tab — Test all (green) +
    /// Deep verify (accent) + Refresh all (right-aligned, neutral).
    /// Desktop SubscribePage.axaml rows 204-260 parity (where Test all +
    /// Deep verify share the row with the Refresh-all section header).
    /// Phase A may later move this row into a dedicated FooterActions
    /// slot; today it docks inside the tab content.
    /// </summary>
    private Border BuildSubscribeFooterActions()
    {
        // POL-1: Test all + Deep verify use desktop's `Padding="10,4" FontSize="10"`
        // (SubscribePage.axaml lines 210-225 — same shape as ServersPage so
        // both tabs read consistently in the action bar row).
        _subsAggTestAllBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvServersTestAll,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 4),
            Background = GetBrush("SuccessSolidBrush"),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        };
        ToolTip.SetTip(_subsAggTestAllBtn, Localization.SrvTipTestAll);
        _subsAggTestAllBtn.Click += async (_, _) => await OnSubsAggTestAllClicked();

        _subsAggDeepVerifyBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvServersDeepVerify,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 4),
            Background = GetBrush("AccentSolidBrush"),
            Foreground = GetBrush("AccentOnSolidBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        };
        ToolTip.SetTip(_subsAggDeepVerifyBtn, Localization.AdvServersDeepVerifyAndroidNote);
        _subsAggDeepVerifyBtn.Click += async (_, _) => await OnSubsAggDeepVerifyClicked();

        _subsAggStatusText = new TextBlock
        {
            Text = string.Empty,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // POL-1: Refresh all uses desktop's compact "8,3" / FontSize 10
        // pattern from SubscribePage.axaml line 257 (`Padding="8,3" FontSize="10"`).
        // Pre-POL-1 used Padding=10,5 + FontSize=11 — too heavy for a
        // tertiary action sitting next to two primary CTAs.
        _subsRefreshAllBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvSubscribeRefreshAll,
            FontSize = 10,
            Padding = new Thickness(8, 3),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceRaisedBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        _subsRefreshAllBtn.Click += OnSubsRefreshAllClicked;

        // 4-column row: Test all | Deep verify | progress text (1*) | Refresh all
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            ColumnSpacing = 6,
        };
        Grid.SetColumn(_subsAggTestAllBtn, 0);
        Grid.SetColumn(_subsAggDeepVerifyBtn, 1);
        Grid.SetColumn(_subsAggStatusText, 2);
        Grid.SetColumn(_subsRefreshAllBtn, 3);
        grid.Children.Add(_subsAggTestAllBtn);
        grid.Children.Add(_subsAggDeepVerifyBtn);
        grid.Children.Add(_subsAggStatusText);
        grid.Children.Add(_subsRefreshAllBtn);

        return new Border
        {
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = GetBrush("SurfaceBaseBrush"),
            Padding = new Thickness(10, 6, 10, 6),
            Child = grid,
        };
    }

    /// <summary>
    /// Aggregate every enabled subscription's servers and rebuild the
    /// top-of-tab list. Empty state when no enabled sub has any servers.
    /// </summary>
    private void RebuildAggregatedServerList()
    {
        if (_subsAggListStack is null || _subsAggEmptyHint is null) return;
        _subsAggListStack.Children.Clear();

        var aggregated = _subs
            .Where(s => s != null && s.Enabled && s.Servers != null)
            .SelectMany(s => s.Servers)
            .Where(s => !string.IsNullOrWhiteSpace(s?.Server))
            .ToList();
        if (aggregated.Count == 0)
        {
            _subsAggEmptyHint.IsVisible = true;
            return;
        }
        _subsAggEmptyHint.IsVisible = false;

        var activeName = AndroidStorage.GetSelectedServerName();
        foreach (var srv in aggregated)
        {
            _subsAggListStack.Children.Add(BuildAggregatedServerRow(srv, activeName));
        }
    }

    /// <summary>
    /// Per-row template for the aggregated server table. Mirrors
    /// <see cref="BuildServerRow"/> from <c>AndroidApp.ServerList.cs</c>
    /// (radio | name+host | IP | Ping | Port | refresh button) but reads
    /// from the local <c>_subsAggTestingKeys</c> + caller-supplied
    /// results dict so the Subscribe tab's testing state stays separate
    /// from the Servers tab's.
    /// </summary>
    private Control BuildAggregatedServerRow(
        VlessServerEntry srv,
        string? activeServerName)
    {
        var key = AndroidStorage.BuildServerKey(srv);
        var hasResult = _subsAggResults.TryGetValue(key, out var result);
        var isTesting = _subsAggTestingKeys.Contains(key);
        var isActive = !string.IsNullOrEmpty(activeServerName)
                       && string.Equals(srv.Name, activeServerName, StringComparison.OrdinalIgnoreCase);

        // ── Radio dot — filled when active. ──
        var radio = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1.5),
            BorderBrush = isActive ? GetBrush("SuccessSolidBrush") : GetBrush("BorderStrongBrush"),
            Background = isActive ? GetBrush("SuccessSolidBrush") : Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (isActive)
        {
            radio.Child = new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        var displayName = string.IsNullOrWhiteSpace(srv.Name) ? srv.Server : srv.Name;
        var nameText = new TextBlock
        {
            Text = displayName,
            FontSize = 11,
            FontWeight = isActive ? FontWeight.Bold : FontWeight.SemiBold,
            Foreground = isActive ? GetBrush("AccentFgBrush") : GetBrush("TextPrimaryBrush"),
            FontFamily = new FontFamily("monospace"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Mobile design 2026-05-11 — collapse the desktop's separate IP /
        // Port / Protocol columns into a single mono meta-line beneath
        // the name. Format: `104.194.156.93 · :443 · reality` per
        // Mobile.html line 520. Buys ~140 dp of horizontal real estate
        // on a 384 dp phone width — IP+port were getting trimmed before.
        var metaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(srv.Server)) metaParts.Add(srv.Server!);
        if (srv.Port > 0) metaParts.Add(":" + srv.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var transportLabel = BuildHostSubtitle(srv);
        if (!string.IsNullOrEmpty(transportLabel)) metaParts.Add(transportLabel);
        var metaText = new TextBlock
        {
            Text = string.Join(" · ", metaParts),
            FontSize = 9,
            Foreground = GetBrush("TextMutedBrush"),
            FontFamily = new FontFamily("monospace"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        metaText.IsVisible = !string.IsNullOrEmpty(metaText.Text);
        var nameStack = new Border
        {
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { nameText, metaText },
            },
        };
        ToolTip.SetTip(nameStack, Localization.SrvTipSelectServer);
        nameStack.PointerReleased += (_, _) =>
        {
            AndroidStorage.SetSelectedServerName(srv.Name);
            CloseAdvancedShell();
        };

        // Ping pill (Mobile.html `.ping.g/.o/.b/.muted`) — colored bg,
        // white text, mono, min-width so the column doesn't jitter as
        // latency strings change. ResolveLatencyDisplay still owns the
        // value + color decision; we just wrap it in a styled Border.
        var (pingDisplay, _) = ResolveLatencyDisplay(hasResult ? result : null, isTesting);
        var pingBgBrush = ResolveLatencyBadgeBackground(hasResult ? result : null, isTesting);
        // White text rides solid-colour pills (success/warn/danger);
        // the muted SurfaceSunken pill uses TextMuted so it stays low-
        // contrast for the "no data yet" state.
        var pingHasData = hasResult && !isTesting && result is not null;
        var pingTextInside = new TextBlock
        {
            Text = pingDisplay,
            FontSize = 9,
            FontWeight = FontWeight.Bold,
            FontFamily = new FontFamily("monospace"),
            Foreground = pingHasData ? Brushes.White : GetBrush("TextMutedBrush"),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var pingBadge = new Border
        {
            Padding = new Thickness(7, 4),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            Background = pingBgBrush,
            MinWidth = 44,
            VerticalAlignment = VerticalAlignment.Center,
            Child = pingTextInside,
        };
        if (hasResult && !string.IsNullOrEmpty(result?.Error))
            ToolTip.SetTip(pingBadge, result.Error);

        var refreshBtn = new Avalonia.Controls.Button
        {
            Content = "⟳",
            FontSize = 13,
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = GetBrush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !isTesting,
        };
        ToolTip.SetTip(refreshBtn, Localization.SrvTipTestRow);
        refreshBtn.Click += async (_, _) => await TestSingleAggregatedServerAsync(srv);

        // 4-col grid matches Mobile.html `.srv` exactly: radio · name+meta
        // · ping pill · refresh. IP / port collapsed into the meta-line
        // above, so this is one column narrower than the desktop layout.
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("14,*,Auto,24"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(radio, 0);
        Grid.SetColumn(nameStack, 1);
        Grid.SetColumn(pingBadge, 2);
        Grid.SetColumn(refreshBtn, 3);
        grid.Children.Add(radio);
        grid.Children.Add(nameStack);
        grid.Children.Add(pingBadge);
        grid.Children.Add(refreshBtn);

        return new Border
        {
            BorderThickness = new Thickness(0),
            Background = isActive
                ? GetBrush("AccentBgSubtleBrush")
                : Brushes.Transparent,
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            Padding = new Thickness(8, 5),
            Child = grid,
        };
    }

    // ── Aggregated-list test-batch handlers ────────────────────────────

    private async Task OnSubsAggTestAllClicked()
    {
        var aggregated = _subs
            .Where(s => s != null && s.Enabled && s.Servers != null)
            .SelectMany(s => s.Servers)
            .Where(s => !string.IsNullOrWhiteSpace(s?.Server))
            .ToList();
        if (aggregated.Count == 0) return;
        if (_subsAggTestAllCts is not null)
        {
            try { _subsAggTestAllCts.Cancel(); } catch { /* swallow */ }
            return;
        }

        _subsAggTestAllCts = new CancellationTokenSource();
        var ct = _subsAggTestAllCts.Token;
        // Pull a fresh snapshot from storage in case Servers tab tests
        // mutated badges since this tab last activated.
        _subsAggResults = AndroidStorage.GetServerTestResults();

        try
        {
            var total = aggregated.Count;
            var done = 0;
            foreach (var srv in aggregated)
                _subsAggTestingKeys.Add(AndroidStorage.BuildServerKey(srv));
            RebuildAggregatedServerList();
            UpdateAggregatedProgressText(0, total);
            if (_subsAggTestAllBtn is not null)
                _subsAggTestAllBtn.Content = Localization.SrvTesting;

            using var sem = new SemaphoreSlim(4);
            var tasks = aggregated.Select(async srv =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var result = await TcpTlsProbe.ProbeServerAsync(srv, ct);
                    ApplyAggregatedResult(srv, result);
                }
                catch (OperationCanceledException) { /* leave row */ }
                catch
                {
                    ApplyAggregatedResult(srv,
                        new ServerProbeResult(ServerProbeStatus.Unreachable, 0, "probe error"));
                }
                finally
                {
                    sem.Release();
                    var n = Interlocked.Increment(ref done);
                    Dispatcher.UIThread.Post(() => UpdateAggregatedProgressText(n, total));
                }
            });
            await Task.WhenAll(tasks);

            var reachable = aggregated.Count(srv =>
            {
                if (!_subsAggResults.TryGetValue(AndroidStorage.BuildServerKey(srv), out var r)) return false;
                var s = (ServerProbeStatus)r.Status;
                return s is ServerProbeStatus.Ok or ServerProbeStatus.Slow;
            });
            if (_subsAggStatusText is not null)
                _subsAggStatusText.Text = string.Format(Localization.SrvProgressDoneFmt, reachable, total);
        }
        finally
        {
            _subsAggTestingKeys.Clear();
            AndroidStorage.SetServerTestResults(_subsAggResults);
            try { _subsAggTestAllCts?.Dispose(); } catch { /* swallow */ }
            _subsAggTestAllCts = null;
            if (_subsAggTestAllBtn is not null)
                _subsAggTestAllBtn.Content = Localization.AdvServersTestAll;
            RebuildAggregatedServerList();
        }
    }

    /// <summary>Deep verify on Android = same TCP+TLS probe pass as Test
    /// all (sing-box can't be spawned from the app sandbox — see
    /// <c>OnSrvDeepVerifyClicked</c>). Tooltip on the button explains
    /// the platform limitation.</summary>
    private async Task OnSubsAggDeepVerifyClicked() => await OnSubsAggTestAllClicked();

    private async Task TestSingleAggregatedServerAsync(VlessServerEntry srv)
    {
        var key = AndroidStorage.BuildServerKey(srv);
        if (_subsAggTestingKeys.Contains(key)) return;
        _subsAggTestingKeys.Add(key);
        RebuildAggregatedServerList();
        try
        {
            var result = await TcpTlsProbe.ProbeServerAsync(srv, CancellationToken.None);
            ApplyAggregatedResult(srv, result);
            AndroidStorage.SetServerTestResults(_subsAggResults);
        }
        catch
        {
            ApplyAggregatedResult(srv,
                new ServerProbeResult(ServerProbeStatus.Unreachable, 0, "probe error"));
            AndroidStorage.SetServerTestResults(_subsAggResults);
        }
        finally
        {
            _subsAggTestingKeys.Remove(key);
            RebuildAggregatedServerList();
        }
    }

    private void ApplyAggregatedResult(VlessServerEntry srv, ServerProbeResult result)
    {
        var key = AndroidStorage.BuildServerKey(srv);
        _subsAggResults[key] = new AndroidStorage.ServerTestResultDto
        {
            Status = (int)result.Status,
            LatencyMs = result.LatencyMs,
            LastTestedAt = DateTimeOffset.UtcNow,
            Error = result.Error,
        };
        Dispatcher.UIThread.Post(RebuildAggregatedServerList);
    }

    private void UpdateAggregatedProgressText(int done, int total)
    {
        if (_subsAggStatusText is null) return;
        _subsAggStatusText.Text = string.Format(Localization.SrvProgressFmt, done, total);
    }

    /// <summary>
    /// Re-seed Subscriptions tab state from persisted storage. Called by
    /// the Advanced shell on tab activation. Replaces the old
    /// OpenSubsOverlay path (overlay is gone in AND-MIGRATE-OVERLAYS).
    /// </summary>
    private void ReseedSubscribeTabState()
    {
        _subs = AndroidStorage.GetSubscriptions();
        _refreshingIds.Clear();
        _pendingDeleteId = null;
        _editingId = null;
        // Phase B: clear aggregated-list testing state on re-seed so a
        // dangling spinner from a cancelled previous run doesn't survive
        // tab navigation. Refresh the test-result mirror from storage so
        // badges from a Test all run on Servers tab show through here.
        _subsAggTestingKeys.Clear();
        _subsAggResults = AndroidStorage.GetServerTestResults();
        if (_subsAggStatusText is not null) _subsAggStatusText.Text = string.Empty;
        RebuildSubsList();
        RebuildAggregatedServerList();
    }

    private void RebuildSubsList()
    {
        if (_subsListStack is null || _subsEmptyHint is null) return;
        _subsListStack.Children.Clear();

        if (_subs.Count == 0)
        {
            _subsEmptyHint.IsVisible = true;
            // Aggregated list also empties when there are no subs.
            RebuildAggregatedServerList();
            return;
        }
        _subsEmptyHint.IsVisible = false;

        foreach (var sub in _subs)
        {
            _subsListStack.Children.Add(BuildSubCard(sub));
        }
        // Subscription enable / refresh / delete all change which servers
        // appear in the aggregated table — keep the two views in sync.
        RebuildAggregatedServerList();
    }

    /// <summary>
    /// Build a single subscription card. Layout mirrors desktop
    /// <c>SubscribePage.axaml</c> lines 274-330 — the <c>srv-row</c>
    /// template: transparent row (no card chrome), 6-column grid
    /// <c>[chk · name+meta · spinner · ✎ · ↻ · ✕]</c>, monospace
    /// SemiBold name + dot-separated muted metadata, ProgressBar
    /// (40×3, indeterminate) where desktop has its <c>IsRefreshing</c>
    /// indicator, and compact <c>srv-refresh</c>-style icon buttons
    /// (no fixed 32×32 box). Editing toggles an inline TextBox pair.
    /// </summary>
    private Control BuildSubCard(SubscriptionEntry sub)
    {
        // ── Column 0: enabled checkbox (24-px column to match desktop's
        //               v2.25.10 fix — narrower clips the indicator).
        var enabledChk = new Avalonia.Controls.CheckBox
        {
            IsChecked = sub.Enabled,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        enabledChk.IsCheckedChanged += (s, e) =>
        {
            sub.Enabled = enabledChk.IsChecked == true;
            AndroidStorage.SetSubscriptions(_subs);
        };

        // ── Column 1: name (srv-name) + metadata (srv-host).
        //               Both monospace + ellipsis to match desktop.
        var nameText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(sub.Name) ? "(no name)" : sub.Name,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            FontFamily = new FontFamily("monospace"),
            Foreground = GetBrush("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var metadataText = new TextBlock
        {
            Text = FormatSubMetadata(sub),
            FontSize = 9,
            FontFamily = new FontFamily("monospace"),
            Foreground = GetBrush("TextMutedBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        ToolTip.SetTip(metadataText, Localization.TipSubscriptionMetadata);
        // v2.32.0 (AND-4): wrap in a hit-test-friendly Border so the name
        // area is tappable independently of the action buttons. Tap →
        // open per-server testing overlay (drill-down).
        var nameStack = new Border
        {
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { nameText, metadataText },
            },
        };
        nameStack.PointerReleased += (s, e) => OpenServerListOverlay(sub);

        // ── Column 2: indeterminate ProgressBar (40×3) — same shape as
        //               desktop's row spinner; only visible mid-refresh.
        //               Fully qualified — Android.Widget.ProgressBar would
        //               otherwise be a name collision in this assembly.
        var spinner = new Avalonia.Controls.ProgressBar
        {
            IsVisible = _refreshingIds.Contains(sub.Id),
            IsIndeterminate = true,
            Height = 3,
            Width = 40,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Foreground = GetBrush("AccentSolidBrush"),
        };

        // ── Columns 3-5: ✎ edit / ↻ refresh / ✕ delete (srv-refresh
        //                  micro-buttons — transparent, no fixed box).
        var editBtn = StyledRowActionButton("✎", Localization.TipEditSubscription);
        editBtn.Click += (s, e) => StartEditUrl(sub);

        var refreshBtn = StyledRowActionButton("↻", Localization.TipRefreshSubscription);
        refreshBtn.IsEnabled = !_refreshingIds.Contains(sub.Id);
        refreshBtn.Click += async (s, e) => await RefreshOneAsync(sub);

        // 2-tap delete: first tap arms _pendingDeleteId, second tap
        // commits. Auto-disarms after 4 s of inactivity.
        var deleteBtn = StyledRowActionButton("✕", Localization.TipRemoveSubscription);
        if (_pendingDeleteId == sub.Id)
        {
            deleteBtn.Content = "✕?";
            deleteBtn.Foreground = GetBrush("DangerFgBrush");
            ToolTip.SetTip(deleteBtn, Localization.SubsRemoveConfirm);
        }
        deleteBtn.Click += (s, e) => OnDeleteSubClicked(sub);

        var topGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,*,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(enabledChk, 0);
        Grid.SetColumn(nameStack, 1);
        Grid.SetColumn(spinner, 2);
        Grid.SetColumn(editBtn, 3);
        Grid.SetColumn(refreshBtn, 4);
        Grid.SetColumn(deleteBtn, 5);
        topGrid.Children.Add(enabledChk);
        topGrid.Children.Add(nameStack);
        topGrid.Children.Add(spinner);
        topGrid.Children.Add(editBtn);
        topGrid.Children.Add(refreshBtn);
        topGrid.Children.Add(deleteBtn);

        // Inline URL editor — only visible when this card is being edited.
        // Indented to the name column so the editor visually nests under
        // its row instead of competing with the chk gutter.
        Control? editorRow = null;
        if (_editingId == sub.Id)
        {
            var nameBox = new TextBox
            {
                Text = sub.Name,
                FontSize = 11,
                Padding = new Thickness(8, 6),
                CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            };
            var urlBox = new TextBox
            {
                Text = sub.Url,
                FontSize = 11,
                FontFamily = new FontFamily("monospace"),
                Padding = new Thickness(8, 6),
                CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            };
            var saveBtn = StyledSecondaryButton(Localization.SubsSaveEdit);
            saveBtn.Click += (s, e) =>
            {
                var newUrl = (urlBox.Text ?? string.Empty).Trim();
                var newName = (nameBox.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(newUrl)) return;
                sub.Url = newUrl;
                if (!string.IsNullOrEmpty(newName)) sub.Name = newName;
                _editingId = null;
                AndroidStorage.SetSubscriptions(_subs);
                RebuildSubsList();
            };
            var cancelBtn = StyledSecondaryButton(Localization.SubsCancelEdit);
            cancelBtn.Click += (s, e) =>
            {
                _editingId = null;
                RebuildSubsList();
            };
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0),
                Children = { cancelBtn, saveBtn },
            };
            editorRow = new StackPanel
            {
                Spacing = 4,
                Margin = new Thickness(32, 6, 0, 0),
                Children = { nameBox, urlBox, btnRow },
            };
        }

        var content = new StackPanel
        {
            Spacing = 0,
            Children = { topGrid },
        };
        if (editorRow is not null) content.Children.Add(editorRow);

        // Outer Border = desktop's srv-row: transparent, no border,
        // RadiusXs, padding 8,5. The list visually exists via spacing
        // between rows, not card chrome.
        return new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            Padding = new Thickness(8, 5),
            Child = content,
        };
    }

    /// <summary>
    /// Mirror of desktop's <c>SubscriptionViewModel.LastRefreshedDisplay</c>
    /// + the multi-binding <c>{Url} · {N}s · {time}</c> from
    /// SubscribePage.axaml lines 297-306. Truncates URL via TextTrimming
    /// at the control level.
    /// </summary>
    private static string FormatSubMetadata(SubscriptionEntry sub)
    {
        var url = sub.Url ?? string.Empty;
        var n = sub.LastServerCount;
        string time;
        if (sub.LastRefreshedAt is null || sub.LastRefreshedAt.Value.Year < 2000)
        {
            time = Localization.SubsNeverRefreshed;
        }
        else
        {
            time = sub.LastRefreshedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        var nFmt = string.Format(Localization.SubsServersFormat, n);
        return $"{url} · {nFmt} · {time}";
    }

    /// <summary>
    /// Mirrors desktop's <c>Button.srv-refresh</c> style (SubscribePage.axaml
    /// lines 86-98) — transparent, borderless icon button sized to its
    /// glyph + a small touch-friendly padding bump. Pre-rev1 used a fixed
    /// 32×32 box which made the row look like a row of square chips
    /// instead of the lightweight icon trio used on desktop.
    /// <para>POL-1: glyph size + horizontal padding tightened to the
    /// desktop spec — `FontSize="11" Padding="2"` (was 14 + 6,2 which made
    /// each button noticeably larger than the corresponding desktop icon).
    /// Tap target stays touch-friendly via the row Padding="8,5" parent
    /// + the icon's intrinsic height.</para>
    /// </summary>
    private Avalonia.Controls.Button StyledRowActionButton(string glyph, string? tooltip)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = glyph,
            FontSize = 11,
            Padding = new Thickness(2),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = GetBrush("TextMutedBrush"),
        };
        if (!string.IsNullOrEmpty(tooltip)) ToolTip.SetTip(btn, tooltip);
        return btn;
    }

    private void StartEditUrl(SubscriptionEntry sub)
    {
        _editingId = _editingId == sub.Id ? null : sub.Id;
        _pendingDeleteId = null;
        RebuildSubsList();
    }

    private void OnDeleteSubClicked(SubscriptionEntry sub)
    {
        var now = DateTime.UtcNow;
        // Re-arm if user was confirming a different card or the previous
        // confirm timed out.
        var armedRecently = _pendingDeleteId == sub.Id
                            && (now - _lastDeleteTapAt).TotalSeconds < 4;
        if (!armedRecently)
        {
            _pendingDeleteId = sub.Id;
            _lastDeleteTapAt = now;
            RebuildSubsList();
            return;
        }

        // Confirmed — actually remove.
        _pendingDeleteId = null;
        _subs.RemoveAll(s => string.Equals(s.Id, sub.Id, StringComparison.Ordinal));
        AndroidStorage.SetSubscriptions(_subs);
        RebuildSubsList();
    }

    private async void OnSubsAddClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_subsNewUrl is null) return;
        var url = (_subsNewUrl.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(url)) return;

        var name = (_subsNewName?.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name)) name = $"Sub {_subs.Count + 1}";

        var entry = new SubscriptionEntry
        {
            Name = name,
            Url = url,
            Enabled = true,
        };
        _subs.Add(entry);
        AndroidStorage.SetSubscriptions(_subs);

        if (_subsNewName is not null) _subsNewName.Text = string.Empty;
        if (_subsNewUrl is not null) _subsNewUrl.Text = string.Empty;
        RebuildSubsList();

        // Mirror desktop AddSubscriptionAsync: immediately refresh the
        // new entry so the user sees a server count appear without a
        // separate ↻ tap.
        await RefreshOneAsync(entry);
    }

    private async Task RefreshOneAsync(SubscriptionEntry sub)
    {
        if (sub is null || string.IsNullOrWhiteSpace(sub.Url)) return;
        if (_refreshingIds.Contains(sub.Id)) return;

        _refreshingIds.Add(sub.Id);
        RebuildSubsList();
        try
        {
            var count = await Task.Run(() =>
                SubscriptionFetcher.RefreshEntryAsync(sub, logger: null, ct: CancellationToken.None));
            sub.LastServerCount = count;
        }
        catch (Exception ex)
        {
            ShowSubsRefreshAllStatus(string.Format(Localization.SubsRefreshFailed, ex.Message));
        }
        finally
        {
            _refreshingIds.Remove(sub.Id);
            AndroidStorage.SetSubscriptions(_subs);
            RebuildSubsList();
        }
    }

    private async void OnSubsRefreshAllClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var enabled = _subs.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (enabled.Count == 0) return;

        foreach (var s in enabled) _refreshingIds.Add(s.Id);
        RebuildSubsList();
        ShowSubsRefreshAllStatus(Localization.SubsRefreshing);

        var totalServers = 0;
        try
        {
            await Task.WhenAll(enabled.Select(async s =>
            {
                try
                {
                    var count = await Task.Run(() =>
                        SubscriptionFetcher.RefreshEntryAsync(s, logger: null, ct: CancellationToken.None));
                    s.LastServerCount = count;
                    Interlocked.Add(ref totalServers, count);
                }
                catch
                {
                    // Per-entry failure already logged in fetcher; UI shows
                    // last refresh time stays old. Continue with siblings.
                }
            }));
        }
        finally
        {
            foreach (var s in enabled) _refreshingIds.Remove(s.Id);
            AndroidStorage.SetSubscriptions(_subs);
            RebuildSubsList();
            ShowSubsRefreshAllStatus(string.Format(Localization.SubsRefreshAllDone, totalServers));
        }
    }

    private async void ShowSubsRefreshAllStatus(string text)
    {
        if (_subsRefreshAllStatus is null) return;
        _subsRefreshAllStatus.Text = text;
        _subsRefreshAllStatus.IsVisible = true;
        try
        {
            await Task.Delay(4000);
            if (_subsRefreshAllStatus is not null && _subsRefreshAllStatus.Text == text)
            {
                _subsRefreshAllStatus.IsVisible = false;
            }
        }
        catch { /* swallow */ }
    }

    /// <summary>
    /// Refresh localized strings on language toggle. Called from
    /// <see cref="ToggleLanguageAndRefresh"/>.
    /// </summary>
    private void RefreshSubsLocalizedStrings()
    {
        if (_subsSectionLabel is not null) _subsSectionLabel.Text = Localization.SubscriptionsSection;
        if (_subsRefreshAllBtn is not null) _subsRefreshAllBtn.Content = Localization.AdvSubscribeRefreshAll;
        if (_subsAddBtn is not null) _subsAddBtn.Content = Localization.AddSubscription;
        if (_subsNewName is not null) _subsNewName.Watermark = Localization.AdvSubscribeNameLabel;
        if (_subsNewUrl is not null) _subsNewUrl.Watermark = Localization.AdvSubscribeUrlLabel;
        if (_subsEmptyHint is not null) _subsEmptyHint.Text = Localization.LblAddSubscriptionHint;

        // Phase B (AND-ADV-SERVERS-SUBSCRIBE) — aggregated server table
        // headers + middle action row + empty hint.
        if (_subsAggColServer is not null) _subsAggColServer.Text = Localization.ColServer;
        if (_subsAggColIp is not null) _subsAggColIp.Text = Localization.ColIp;
        if (_subsAggColPing is not null)
        {
            _subsAggColPing.Text = Localization.ColPing;
            ToolTip.SetTip(_subsAggColPing, Localization.ColPingTooltip);
        }
        if (_subsAggColPort is not null) _subsAggColPort.Text = Localization.ColPort;
        if (_subsAggEmptyHint is not null)
            _subsAggEmptyHint.Text = Localization.AdvSubscribeAggregatedEmpty;
        if (_subsAggTestAllBtn is not null && _subsAggTestAllCts is null)
            _subsAggTestAllBtn.Content = Localization.AdvServersTestAll;
        if (_subsAggDeepVerifyBtn is not null)
        {
            _subsAggDeepVerifyBtn.Content = Localization.AdvServersDeepVerify;
            ToolTip.SetTip(_subsAggDeepVerifyBtn, Localization.AdvServersDeepVerifyAndroidNote);
        }

        // Card list: cheapest path is full rebuild — strings are per-card
        // (Refreshing… spinner, refresh/delete tooltips, formatted
        // timestamp uses "никогда"/"never"). Skip if Subscriptions tab is
        // not currently mounted in the Advanced shell.
        if (_subsListStack is not null) RebuildSubsList();
    }
}
