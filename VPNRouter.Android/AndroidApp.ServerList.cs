using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
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

namespace VPNRouter.Android;

/// <summary>
/// v2.32.0 (AND-4, 2026-05-07) — per-server testing UI inside a subscription
/// drill-down. Mirrors desktop <c>VPNRouter.App/Views/Pages/ServersPage.axaml</c>
/// (per-row Test button, latency badge, sort by latency) but lives as a
/// fullscreen Border overlay (same pattern as the AND-1..AND-3 overlays in
/// the SubscribePage / Free Configs / Settings partials).
///
/// <para>Tap on a subscription card's name area opens this overlay; the
/// card's <c>↻</c> / <c>✎</c> / <c>✕</c> action buttons keep their existing
/// roles (refresh / edit URL / delete). "Test all" runs a 4-way concurrent
/// TCP+TLS probe via <see cref="TcpTlsProbe.ProbeServerAsync"/>; results
/// persist via <see cref="AndroidStorage.SetServerTestResults"/> keyed by
/// <c>Server:Port:Uuid:Flow</c> so badges survive app restart.</para>
///
/// <para>Concurrency = 4 — see <c>plans/v2.32.0-android-server-testing.md</c>
/// "Concurrency choice" for the full rationale (mobile NAT/CPU/battery
/// budget vs desktop's MaxConcurrency=80 free-config bulk path).</para>
/// </summary>
public partial class AndroidApp
{
    // AND-MIGRATE-OVERLAYS (2026-05-09): the standalone Server-list overlay
    // is gone — the per-subscription server detail UI is now the
    // "Servers" tab inside the Advanced shell. The drill-in entry point
    // (sub-card name area tap) deeplinks to the shell on Servers tab with
    // the tapped sub set as _srvCurrentSub.
    //
    // _srvTitle remains because the body header still reads
    // "Servers · <sub name>" so the user knows which sub they're looking
    // at when the shell title says "Advanced settings".
    private TextBlock? _srvTitle;
    private Avalonia.Controls.Button? _srvTestAllBtn;
    private Avalonia.Controls.Button? _srvSortToggle;
    private TextBlock? _srvStatusText;
    private StackPanel? _srvListStack;
    private TextBlock? _srvEmptyHint;
    private TextBlock? _srvColServer;
    private TextBlock? _srvColIp;
    private TextBlock? _srvColPing;
    private TextBlock? _srvColPort;

    // ── AND-ADV-SERVERS-SUBSCRIBE (Phase B, 2026-05-10) ────────────────
    // Sub-tab segmented control (Servers / Custom Config JSON), the two
    // sub-panels, and the footer action row (Test all / Deep verify /
    // vless URI input / Remove / Add Server(s)). All hosted inside the
    // Advanced shell's Servers tab content. Phase A may later relocate
    // _srvFooterActionsRow into a dedicated FooterActions slot above the
    // persistent connect/disconnect footer; for now it docks at the bottom
    // of the tab content itself so users see the action surface
    // immediately.
    private string _srvSubTab = "servers";
    private Avalonia.Controls.Button? _srvSubTabServersBtn;
    private Avalonia.Controls.Button? _srvSubTabCustomJsonBtn;
    private StackPanel? _srvSubTabRow;
    private DockPanel? _srvServersSubPanel;
    private StackPanel? _srvCustomJsonSubPanel;
    private Border? _srvFooterActionsRow;
    private Avalonia.Controls.Button? _srvDeepVerifyBtn;
    private TextBox? _srvVlessUriInput;
    private Avalonia.Controls.Button? _srvAddBtn;
    private Avalonia.Controls.Button? _srvRemoveBtn;

    // Custom Config (JSON) sub-tab body. Owns its own TextBox + status —
    // independent from the Simple page's _ccCustomInput so navigation
    // between the two surfaces doesn't cause control-already-parented
    // errors. Both surfaces read/write the same AndroidStorage key
    // (CustomConfigJson), so the UI stays in sync via re-seed on tab
    // activation.
    private TextBox? _srvCustomJsonInput;
    private TextBlock? _srvCustomJsonStatus;
    private Avalonia.Controls.Button? _srvCustomJsonSaveBtn;
    private Avalonia.Controls.Button? _srvCustomJsonClearBtn;
    private Avalonia.Controls.Button? _srvCustomJsonValidateBtn;
    private TextBlock? _srvCustomJsonExplainer;

    /// <summary>The subscription whose servers we're showing. Set on open.</summary>
    private SubscriptionEntry? _srvCurrentSub;

    /// <summary>
    /// In-memory mirror of the persisted side-table — keyed by
    /// <c>Server:Port:Uuid:Flow</c>. Modified during Test all flows; flushed
    /// to <see cref="AndroidStorage.SetServerTestResults"/> on each result
    /// apply (so a kill -9 mid-batch still saves what we got).
    /// </summary>
    private Dictionary<string, AndroidStorage.ServerTestResultDto> _srvResults = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tracks per-server in-progress probes — disables the row Test button + spinner glyph.</summary>
    private readonly HashSet<string> _srvTestingKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>0 = original order, 1 = ascending by latency (untested at end).</summary>
    private bool _srvSortByLatency = false;

    /// <summary>Cancellation source for the current Test all batch (Stop button / overlay close).</summary>
    private CancellationTokenSource? _srvTestAllCts;

    /// <summary>
    /// AND-MIGRATE-OVERLAYS (2026-05-09) — body content for the Servers
    /// tab inside the Advanced shell. Updated AND-ADV-SERVERS-SUBSCRIBE
    /// (Phase B, 2026-05-10) to mirror desktop ServersPage chrome:
    /// segmented sub-tab control (Servers / Custom Config JSON), per
    /// sub-tab body, and a persistent footer action row (Test all /
    /// Deep verify / vless URI input / Remove / Add Server(s)) docked
    /// at the bottom. Phase A may later move _srvFooterActionsRow into
    /// a dedicated shell-owned FooterActions slot above the persistent
    /// connect/disconnect footer; today the row docks inside the tab
    /// content so the surface is visible immediately.
    /// </summary>
    private Control BuildServersTabContent()
    {
        // ── Sub-tab segmented control (top of tab body) ───────────────
        _srvSubTabRow = BuildServersSubTabBar();

        // ── Servers sub-panel: existing subscription header + action row
        //    + column headers + scrollable list. Pulled into its own
        //    builder so the sub-tab toggle just flips IsVisible on two
        //    sibling Controls inside the content host.
        _srvServersSubPanel = BuildServersListSubPanel();

        // ── Custom Config (JSON) sub-panel: explainer + textarea +
        //    Validate / Save / Clear (mirrors the Simple-page custom
        //    section but is independent so both can be active in
        //    different navigation paths without parenting collisions).
        _srvCustomJsonSubPanel = BuildCustomJsonSubPanel();

        // ── Footer action row: Test all / Deep verify / vless URI /
        //    Remove / Add Server(s). Visible only on the Servers
        //    sub-tab — the Custom Config sub-tab has its own action
        //    buttons inside its sub-panel.
        _srvFooterActionsRow = BuildServersFooterActions();

        // Compose the sub-panels into a single content host so toggling
        // the sub-tab flips one IsVisible bit per panel.
        var contentHost = new Grid();
        contentHost.Children.Add(_srvServersSubPanel);
        contentHost.Children.Add(_srvCustomJsonSubPanel);

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_srvSubTabRow, Dock.Top);
        DockPanel.SetDock(_srvFooterActionsRow, Dock.Bottom);
        dock.Children.Add(_srvSubTabRow);
        dock.Children.Add(_srvFooterActionsRow);
        dock.Children.Add(contentHost);

        // Initialize sub-tab to "servers" — paints segments + flips
        // panel + footer visibility.
        ApplyServersSubTabVisuals();

        return new Border
        {
            Background = GetBrush("SurfaceAppBrush"),
            Child = dock,
        };
    }

    /// <summary>
    /// Segmented control at the top of the Servers tab — desktop
    /// ServersPage.axaml lines 117-131 (ListBox sub-tab strip). Active
    /// segment uses the AccentBgSubtle pill style; inactive segments stay
    /// neutral. Click flips _srvSubTab + repaints + toggles panel
    /// visibility.
    /// </summary>
    private StackPanel BuildServersSubTabBar()
    {
        // POL-1: chip Padding/FontSize match desktop ServersPage.axaml line
        // 129 (`Padding="10,4" FontSize="11"`); StackPanel margin matches
        // the page's outer padding (`Margin="6,2"`). Pre-POL-1 used
        // Padding=12,6 + Margin=12,8,12,6 — chips read taller than desktop.
        _srvSubTabServersBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvServersSubTabServers,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 4),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            BorderThickness = new Thickness(1),
        };
        _srvSubTabServersBtn.Click += (_, _) => SetServersSubTab("servers");

        _srvSubTabCustomJsonBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvServersSubTabCustomJson,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 4),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            BorderThickness = new Thickness(1),
        };
        _srvSubTabCustomJsonBtn.Click += (_, _) => SetServersSubTab("custom");

        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(6, 4, 6, 4),
            Children = { _srvSubTabServersBtn, _srvSubTabCustomJsonBtn },
        };
        return row;
    }

    /// <summary>
    /// Build the Servers sub-panel — wraps the existing subscription
    /// header + Test all / sort row + column headers + scrollable list
    /// inside a DockPanel so the parent contentHost can toggle visibility
    /// on a single Control. Field assignments (_srvTitle, _srvTestAllBtn,
    /// etc.) match the pre-Phase-B contract so RebuildServerList /
    /// OnSrvTestAllClicked / OnSrvSortToggleClicked stay unchanged.
    /// </summary>
    private DockPanel BuildServersListSubPanel()
    {
        // Subscription label sits inline at the top of the body so the
        // user can see which sub they're inspecting (the shell's outer
        // title says "Advanced settings", which is sub-agnostic).
        _srvTitle = new TextBlock
        {
            Text = string.Empty,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 4, 12, 0),
        };

        // ── In-list Sort + status row (Test all / Deep verify moved to
        //    the footer per Phase B; sort toggle stays here because it's
        //    list-affordance, not action-row affordance).
        _srvSortToggle = new Avalonia.Controls.Button
        {
            Content = Localization.SrvSortByOriginal,
            FontSize = 10,
            Padding = new Thickness(8, 5),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Foreground = GetBrush("TextSecondaryBrush"),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
        };
        ToolTip.SetTip(_srvSortToggle, Localization.SrvSortToggleHint);
        _srvSortToggle.Click += (s, e) => OnSrvSortToggleClicked();

        _srvStatusText = new TextBlock
        {
            Text = string.Empty,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var sortRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 8,
            Margin = new Thickness(12, 6, 12, 4),
        };
        Grid.SetColumn(_srvSortToggle, 0);
        Grid.SetColumn(_srvStatusText, 1);
        sortRow.Children.Add(_srvSortToggle);
        sortRow.Children.Add(_srvStatusText);

        // ── Column header strip — mobile design 2026-05-11 collapsed the
        // desktop 4-col (Server / IP / Ping / Port) into 2 visible
        // captions: Server + Ping. IP+Port now live in the row's
        // meta-line. ColIp/ColPort fields stay null-but-instantiated to
        // keep refresh and tooltip helpers null-safe.
        _srvColServer = new TextBlock
        {
            Text = Localization.ColServer,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _srvColPing = new TextBlock
        {
            Text = Localization.ColPing,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(_srvColPing, Localization.ColPingTooltip);
        _srvColIp = new TextBlock { IsVisible = false };
        _srvColPort = new TextBlock { IsVisible = false };
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("14,*,Auto,24"),
            ColumnSpacing = 8,
            Margin = new Thickness(8, 2, 8, 4),
        };
        Grid.SetColumn(_srvColServer, 1);
        Grid.SetColumn(_srvColPing, 2);
        headerGrid.Children.Add(_srvColServer);
        headerGrid.Children.Add(_srvColPing);

        var headerHost = new Border
        {
            Margin = new Thickness(12, 4, 12, 0),
            Child = headerGrid,
        };

        // ── Body: scrollable per-server card list ──
        _srvListStack = new StackPanel
        {
            Spacing = 0,
        };
        _srvEmptyHint = new TextBlock
        {
            Text = Localization.SrvEmptyHint,
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
            Children = { _srvListStack, _srvEmptyHint },
        };

        var listScroller = new ScrollViewer
        {
            Content = listRoot,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        // Wrap the scroller in a bordered card to match desktop's
        // ListBox.srv-list (BorderSubtleBrush + RadiusSm + SurfaceBaseBrush).
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
        DockPanel.SetDock(_srvTitle, Dock.Top);
        DockPanel.SetDock(sortRow, Dock.Top);
        DockPanel.SetDock(headerHost, Dock.Top);
        dock.Children.Add(_srvTitle);
        dock.Children.Add(sortRow);
        dock.Children.Add(headerHost);
        dock.Children.Add(listCard);
        return dock;
    }

    /// <summary>
    /// Build the Custom Config (JSON) sub-panel — explainer text + paste
    /// textarea + Validate / Save / Clear buttons + status banner.
    /// Independent from the Simple page's _ccCustomInput surface so the
    /// two can be navigated to in any order without Avalonia
    /// already-has-a-parent errors. Both surfaces persist to the same
    /// AndroidStorage.CustomConfigJson key — re-seed on tab activation
    /// keeps them aligned.
    /// </summary>
    private StackPanel BuildCustomJsonSubPanel()
    {
        _srvCustomJsonExplainer = new TextBlock
        {
            Text = Localization.AdvServersCustomJsonExplainer,
            FontSize = 11,
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16,
            Margin = new Thickness(0, 0, 0, 8),
        };

        _srvCustomJsonInput = new TextBox
        {
            Watermark = Localization.CcCustomWatermark,
            FontFamily = new FontFamily("monospace"),
            FontSize = 10,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 200,
            Padding = new Thickness(8, 6),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
        };

        _srvCustomJsonValidateBtn = new Avalonia.Controls.Button
        {
            Content = Localization.CcValidateButton,
            FontSize = 11,
            Padding = new Thickness(12, 6),
            Background = GetBrush("SurfaceRaisedBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Foreground = GetBrush("TextPrimaryBrush"),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        };
        _srvCustomJsonValidateBtn.Click += OnSrvCustomJsonValidateClicked;

        _srvCustomJsonSaveBtn = new Avalonia.Controls.Button
        {
            Content = Localization.CcSaveButton,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(14, 6),
            Background = GetBrush("AccentSolidBrush"),
            Foreground = GetBrush("AccentOnSolidBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        };
        _srvCustomJsonSaveBtn.Click += OnSrvCustomJsonSaveClicked;

        _srvCustomJsonClearBtn = new Avalonia.Controls.Button
        {
            Content = Localization.CcClearButton,
            FontSize = 11,
            Padding = new Thickness(12, 6),
            Background = GetBrush("SurfaceRaisedBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Foreground = GetBrush("TextSecondaryBrush"),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        };
        _srvCustomJsonClearBtn.Click += OnSrvCustomJsonClearClicked;

        var btnRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { _srvCustomJsonValidateBtn, _srvCustomJsonSaveBtn, _srvCustomJsonClearBtn },
        };

        _srvCustomJsonStatus = new TextBlock
        {
            Text = string.Empty,
            FontSize = 10,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            IsVisible = false,
        };

        var stack = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(12, 4, 12, 12),
            Children = { _srvCustomJsonExplainer, _srvCustomJsonInput, btnRow, _srvCustomJsonStatus },
        };
        return stack;
    }

    /// <summary>
    /// Footer action row for the Servers tab — desktop ServersPage.axaml
    /// lines 354-419 parity. Test all (green) + Deep verify (accent) +
    /// vless URI input (1*) + Remove + Add Server(s). Visible only on the
    /// Servers sub-tab; hidden when Custom Config (JSON) is active so the
    /// sub-tab's own Validate/Save/Clear isn't competing for footer
    /// real-estate.
    ///
    /// Phase A (later) may relocate this row into a dedicated
    /// FooterActions slot above the persistent connect/disconnect footer.
    /// For Phase B the row docks at the bottom of the tab content itself
    /// so the surface is visible immediately.
    /// </summary>
    private Border BuildServersFooterActions()
    {
        // POL-1: Test all + Deep verify use desktop's `Padding="10,4" FontSize="10"`
        // (ServersPage.axaml lines 357-369). Pre-POL-1 used FontSize=11
        // Padding=10,5 — buttons looked heavier than desktop.
        _srvTestAllBtn = new Avalonia.Controls.Button
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
        ToolTip.SetTip(_srvTestAllBtn, Localization.SrvTipTestAll);
        _srvTestAllBtn.Click += async (_, _) => await OnSrvTestAllClicked();

        _srvDeepVerifyBtn = new Avalonia.Controls.Button
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
        // On Android the per-app sandbox can't spawn sing-box as a
        // subprocess (see plans/vpnrouter-android-research.md §3 — libbox
        // runs inside the VPN service, not the app process). Deep verify
        // therefore degrades to the same TCP+TLS probe Test all uses,
        // surfaced via tooltip so the user understands the platform
        // limitation without the button feeling broken.
        ToolTip.SetTip(_srvDeepVerifyBtn, Localization.AdvServersDeepVerifyAndroidNote);
        _srvDeepVerifyBtn.Click += async (_, _) => await OnSrvDeepVerifyClicked();

        _srvVlessUriInput = new TextBox
        {
            Watermark = Localization.WmVlessUri,
            FontFamily = new FontFamily("monospace"),
            FontSize = 10,
            Padding = new Thickness(6, 4),
            AcceptsReturn = false,
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
        };

        // POL-1: Remove + Add buttons use desktop's `Padding="14,5"` from
        // ServersPage.axaml lines 399-409 (matches the `LblAddServers`
        // primary CTA + `LblRemove` neutral). Pre-POL-1 used Padding 10,5 +
        // 12,5 — narrower than desktop.
        _srvRemoveBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvServersRemove,
            FontSize = 11,
            Padding = new Thickness(14, 5),
            Background = GetBrush("SurfaceRaisedBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Foreground = GetBrush("TextSecondaryBrush"),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            IsEnabled = false,
        };
        ToolTip.SetTip(_srvRemoveBtn, Localization.TipDeleteServer);
        _srvRemoveBtn.Click += (_, _) => OnSrvRemoveClicked();

        _srvAddBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvServersAddServers,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(14, 5),
            Background = GetBrush("AccentSolidBrush"),
            Foreground = GetBrush("AccentOnSolidBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        };
        _srvAddBtn.Click += (_, _) => OnSrvAddClicked();

        // Two-row layout matches desktop's stacked DockPanel pattern:
        //   row 0: [Test all] [Deep verify]  (left-aligned)
        //   row 1: [vless input    ] [Remove] [Add Server(s)]
        // On narrow phones the second row's URI input shrinks via the
        // grid '1*' column; the action chips stay compact.
        var actionTopRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            Children = { _srvTestAllBtn, _srvDeepVerifyBtn },
        };
        var actionBottomRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 6,
            Margin = new Thickness(0, 6, 0, 0),
        };
        Grid.SetColumn(_srvVlessUriInput, 0);
        Grid.SetColumn(_srvRemoveBtn, 1);
        Grid.SetColumn(_srvAddBtn, 2);
        actionBottomRow.Children.Add(_srvVlessUriInput);
        actionBottomRow.Children.Add(_srvRemoveBtn);
        actionBottomRow.Children.Add(_srvAddBtn);

        var stack = new StackPanel
        {
            Spacing = 0,
            Children = { actionTopRow, actionBottomRow },
        };
        return new Border
        {
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = GetBrush("SurfaceBaseBrush"),
            Padding = new Thickness(10, 6, 10, 8),
            Child = stack,
        };
    }

    /// <summary>
    /// Switch between the Servers and Custom Config (JSON) sub-tabs.
    /// Re-seeds the Custom JSON textarea from storage on activation so
    /// the two surfaces stay in sync.
    /// </summary>
    private void SetServersSubTab(string sub)
    {
        if (sub != "servers" && sub != "custom") return;
        if (_srvSubTab == sub) return;
        _srvSubTab = sub;
        ApplyServersSubTabVisuals();
        if (sub == "custom") ReseedCustomJsonSubPanel();
    }

    /// <summary>Repaint the segmented sub-tab control + flip sub-panel
    /// + footer-action visibility to match _srvSubTab.</summary>
    private void ApplyServersSubTabVisuals()
    {
        StyleSegment(_srvSubTabServersBtn, _srvSubTab == "servers");
        StyleSegment(_srvSubTabCustomJsonBtn, _srvSubTab == "custom");
        if (_srvServersSubPanel is not null)
            _srvServersSubPanel.IsVisible = _srvSubTab == "servers";
        if (_srvCustomJsonSubPanel is not null)
            _srvCustomJsonSubPanel.IsVisible = _srvSubTab == "custom";
        if (_srvFooterActionsRow is not null)
            _srvFooterActionsRow.IsVisible = _srvSubTab == "servers";
    }

    /// <summary>Pull the current Custom Config JSON from storage into
    /// the textarea so opening the sub-tab shows the saved value.</summary>
    private void ReseedCustomJsonSubPanel()
    {
        if (_srvCustomJsonInput is null) return;
        var stored = AndroidStorage.GetCustomConfigJson() ?? string.Empty;
        _srvCustomJsonInput.Text = stored;
        if (_srvCustomJsonStatus is not null)
        {
            _srvCustomJsonStatus.Text = string.Empty;
            _srvCustomJsonStatus.IsVisible = false;
        }
    }

    // ── Custom JSON sub-tab handlers ───────────────────────────────────

    private void OnSrvCustomJsonValidateClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_srvCustomJsonInput is null || _srvCustomJsonStatus is null) return;
        var raw = (_srvCustomJsonInput.Text ?? string.Empty).Trim();
        _srvCustomJsonStatus.IsVisible = true;

        if (string.IsNullOrEmpty(raw))
        {
            _srvCustomJsonStatus.Text = Localization.CcSaveStatusEmpty;
            _srvCustomJsonStatus.Foreground = GetBrush("DangerFgBrush");
            return;
        }

        try
        {
            var (isValid, errors) = VPNRouter.Core.Services.CustomConfigInjector.Validate(raw);
            if (!isValid)
            {
                _srvCustomJsonStatus.Text = string.Format(
                    Localization.CcValidationFailed,
                    string.Join("; ", errors));
                _srvCustomJsonStatus.Foreground = GetBrush("DangerFgBrush");
                return;
            }
            var (protocols, server) = VPNRouter.Core.Services.CustomConfigInjector.ParseConfigInfo(raw);
            _srvCustomJsonStatus.Text = string.Format(Localization.CcValidationOk, protocols, server);
            _srvCustomJsonStatus.Foreground = GetBrush("SuccessFgBrush");
        }
        catch (Exception ex)
        {
            _srvCustomJsonStatus.Text = string.Format(Localization.CcValidationParseError, ex.Message);
            _srvCustomJsonStatus.Foreground = GetBrush("DangerFgBrush");
        }
    }

    private void OnSrvCustomJsonSaveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_srvCustomJsonInput is null || _srvCustomJsonStatus is null) return;
        var raw = (_srvCustomJsonInput.Text ?? string.Empty).Trim();
        _srvCustomJsonStatus.IsVisible = true;

        if (string.IsNullOrEmpty(raw))
        {
            _srvCustomJsonStatus.Text = Localization.CcSaveStatusEmpty;
            _srvCustomJsonStatus.Foreground = GetBrush("DangerFgBrush");
            return;
        }

        var (isValid, errors) = VPNRouter.Core.Services.CustomConfigInjector.Validate(raw);
        AndroidStorage.SetCustomConfigJson(raw);
        AndroidStorage.SetConfigMode("custom");
        // Keep the Simple-page _ccMode mirror in sync so the segmented
        // mode selector there reflects the just-applied "custom" choice.
        _ccMode = "custom";
        UpdateConfigSummary();

        if (!isValid)
        {
            _srvCustomJsonStatus.Text = string.Format(
                Localization.CcSaveStatusInvalid + " ({0})",
                string.Join("; ", errors));
            _srvCustomJsonStatus.Foreground = GetBrush("WarningFgBrush");
            return;
        }
        _srvCustomJsonStatus.Text = Localization.CcSaveStatusOk;
        _srvCustomJsonStatus.Foreground = GetBrush("SuccessFgBrush");
    }

    private void OnSrvCustomJsonClearClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_srvCustomJsonInput is not null) _srvCustomJsonInput.Text = string.Empty;
        if (_srvCustomJsonStatus is not null) _srvCustomJsonStatus.IsVisible = false;
        AndroidStorage.SetCustomConfigJson(null);
        UpdateConfigSummary();
    }

    // ── Footer action handlers ─────────────────────────────────────────

    /// <summary>
    /// Deep verify on Android = same TCP+TLS probe pass as Test all
    /// (the per-app process sandbox can't spawn sing-box as a separate
    /// process — libbox runs inside VpnRouterService, not the app
    /// process). Tooltip explains the limitation. Reusing
    /// OnSrvTestAllClicked keeps progress / status text consistent.
    /// </summary>
    private async Task OnSrvDeepVerifyClicked() => await OnSrvTestAllClicked();

    /// <summary>
    /// Remove the highlighted active server from the current sub's list
    /// (or the legacy single-VLESS-URI manual server). Greyed out by
    /// default; flips to enabled in RebuildServerList when the active
    /// server is one of the listed entries.
    /// </summary>
    private void OnSrvRemoveClicked()
    {
        var sub = _srvCurrentSub;
        if (sub is null || sub.Servers is null || sub.Servers.Count == 0) return;
        var activeName = AndroidStorage.GetSelectedServerName();
        if (string.IsNullOrEmpty(activeName)) return;

        var before = sub.Servers.Count;
        sub.Servers.RemoveAll(s =>
            string.Equals(s.Name, activeName, StringComparison.OrdinalIgnoreCase));
        if (sub.Servers.Count == before) return;

        // Clear active selection if we just removed the active server.
        AndroidStorage.SetSelectedServerName(null);
        // Persist the mutated subscription list back to storage so the
        // SimplePage reflection of server count + active-server flag
        // stays in sync.
        var subs = AndroidStorage.GetSubscriptions();
        var idx = subs.FindIndex(s =>
            string.Equals(s.Id, sub.Id, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            subs[idx] = sub;
            AndroidStorage.SetSubscriptions(subs);
        }
        RebuildServerList();
        if (_srvRemoveBtn is not null) _srvRemoveBtn.IsEnabled = false;
    }

    /// <summary>
    /// Parse the URI input as a VLESS / hy2 / tuic / ss share-link and
    /// append the resulting server to the current sub. Falls back to
    /// adding the entry under a synthetic "Manual" sub if no
    /// _srvCurrentSub exists yet (first-launch case).
    /// </summary>
    private void OnSrvAddClicked()
    {
        if (_srvVlessUriInput is null) return;
        var raw = (_srvVlessUriInput.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;
        if (!ServerUriParser.IsSupportedScheme(raw)) return;

        VlessServerEntry parsed;
        try
        {
            parsed = ServerUriParser.Parse(raw);
        }
        catch
        {
            return;
        }
        if (string.IsNullOrEmpty(parsed.Server) || parsed.Port <= 0) return;

        var subs = AndroidStorage.GetSubscriptions();
        var sub = _srvCurrentSub;
        if (sub is null)
        {
            // Synthesize a "Manual" sub if none is active. Keeps the
            // multi-protocol entries persistent across launches without
            // requiring the user to set up a subscription first.
            sub = subs.FirstOrDefault(s =>
                string.Equals(s.Name, "Manual", StringComparison.OrdinalIgnoreCase));
            if (sub is null)
            {
                sub = new SubscriptionEntry { Name = "Manual", Url = string.Empty, Enabled = true };
                subs.Add(sub);
            }
            _srvCurrentSub = sub;
        }
        sub.Servers ??= new List<VlessServerEntry>();
        sub.Servers.Add(parsed);

        var idx = subs.FindIndex(s =>
            string.Equals(s.Id, sub.Id, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            subs[idx] = sub;
        else
            subs.Add(sub);
        AndroidStorage.SetSubscriptions(subs);

        _srvVlessUriInput.Text = string.Empty;
        RebuildServerList();
    }

    /// <summary>
    /// Open the overlay against a specific subscription. Pulls fresh test
    /// history from storage + rebuilds the rows. Triggered from the
    /// SubscribePage card's name-area tap (see <c>BuildSubCard</c>).
    /// </summary>
    public void OpenServerListOverlay(SubscriptionEntry sub)
    {
        // AND-MIGRATE-OVERLAYS (2026-05-09): drill-in from a sub card now
        // deeplinks to the Advanced shell on the Servers tab with the
        // tapped sub set as _srvCurrentSub. Closing the shell triggers
        // the same SimplePage refresh the old close handler did.
        _srvCurrentSub = sub;
        _srvResults = AndroidStorage.GetServerTestResults();
        _srvTestingKeys.Clear();
        _srvSortByLatency = false;
        OpenAdvancedShell(AdvancedTab.Servers);
    }

    /// <summary>
    /// Re-seed Servers tab state from persisted storage. Called by the
    /// Advanced shell on tab activation.
    /// </summary>
    private void ReseedServersTabState()
    {
        if (_srvCurrentSub is null)
        {
            // No sub drilled-in yet — fall back to the first enabled sub
            // (matches desktop ServersPage behaviour where the active
            // sub's servers render by default).
            var subs = AndroidStorage.GetSubscriptions();
            _srvCurrentSub = subs.FirstOrDefault(s => s.Enabled)
                              ?? subs.FirstOrDefault();
        }
        _srvResults = AndroidStorage.GetServerTestResults();
        _srvTestingKeys.Clear();
        if (_srvSortToggle is not null)
            _srvSortToggle.Content = _srvSortByLatency
                ? Localization.SrvSortByLatencyAsc
                : Localization.SrvSortByOriginal;
        if (_srvTitle is not null)
        {
            _srvTitle.Text = _srvCurrentSub is null
                ? string.Empty
                : string.Format(Localization.ServerListTitleFmt,
                    string.IsNullOrWhiteSpace(_srvCurrentSub.Name) ? "(no name)" : _srvCurrentSub.Name);
        }
        if (_srvStatusText is not null)
            _srvStatusText.Text = string.Empty;
        // Phase B (AND-ADV-SERVERS-SUBSCRIBE): reset transient input field
        // + re-seed Custom JSON sub-panel from storage so the sub-tab
        // shows the saved value if the user flips to it later.
        if (_srvVlessUriInput is not null) _srvVlessUriInput.Text = string.Empty;
        ReseedCustomJsonSubPanel();
        RebuildServerList();
    }

    /// <summary>
    /// Cancel any in-flight Test all batch when the Advanced shell closes
    /// or switches off the Servers tab. Mirrors the old close handler.
    /// </summary>
    private void StopServersTabBackgroundWork()
    {
        try { _srvTestAllCts?.Cancel(); } catch { /* swallow */ }
    }

    private void RebuildServerList()
    {
        if (_srvListStack is null || _srvEmptyHint is null) return;
        _srvListStack.Children.Clear();

        var servers = _srvCurrentSub?.Servers ?? new List<VlessServerEntry>();
        var activeName = AndroidStorage.GetSelectedServerName();
        if (servers.Count == 0)
        {
            _srvEmptyHint.IsVisible = true;
            UpdateRemoveButtonEnabled(servers, activeName);
            return;
        }
        _srvEmptyHint.IsVisible = false;

        var ordered = OrderServers(servers);
        foreach (var srv in ordered)
        {
            _srvListStack.Children.Add(BuildServerRow(srv, activeName));
        }
        UpdateRemoveButtonEnabled(servers, activeName);
    }

    /// <summary>Footer Remove button: enabled only when the active
    /// server is one of the currently-listed entries.</summary>
    private void UpdateRemoveButtonEnabled(IReadOnlyList<VlessServerEntry> servers, string? activeName)
    {
        if (_srvRemoveBtn is null) return;
        if (string.IsNullOrEmpty(activeName))
        {
            _srvRemoveBtn.IsEnabled = false;
            return;
        }
        _srvRemoveBtn.IsEnabled = servers.Any(s =>
            string.Equals(s.Name, activeName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Order the rows. When sort-by-latency is active we put status
    /// priority first (Ok/Slow → reachable failures → untested), then
    /// LatencyMs ascending. This matches desktop's
    /// <c>SortByLatency</c> mode behaviour.
    /// </summary>
    private List<VlessServerEntry> OrderServers(IReadOnlyList<VlessServerEntry> source)
    {
        if (!_srvSortByLatency) return new List<VlessServerEntry>(source);

        return source
            .Select(s => new
            {
                Srv = s,
                Result = _srvResults.TryGetValue(AndroidStorage.BuildServerKey(s), out var r) ? r : null,
            })
            .OrderBy(x => StatusPriority((ServerProbeStatus?)x.Result?.Status))
            .ThenBy(x => x.Result?.LatencyMs > 0 ? x.Result.LatencyMs : int.MaxValue)
            .Select(x => x.Srv)
            .ToList();
    }

    private static int StatusPriority(ServerProbeStatus? status)
    {
        return status switch
        {
            ServerProbeStatus.Ok => 0,
            ServerProbeStatus.Slow => 1,
            ServerProbeStatus.TlsFailed => 2,
            ServerProbeStatus.Unreachable => 3,
            ServerProbeStatus.Timeout => 3,
            ServerProbeStatus.Implausible => 4,
            ServerProbeStatus.SkippedNotApplicable => 5,
            _ => 6,
        };
    }

    /// <summary>
    /// Per-row template — mirrors desktop's <c>Border.srv-row</c> pattern
    /// from <c>ServersPage.axaml</c> lines 178-238 — radio | name+host
    /// subtitle | IP | Ping | Port | refresh button. The "host subtitle"
    /// is the protocol/security pair (e.g. "tcp + reality") taken from
    /// <see cref="BuildHostSubtitle"/>; the IP column shows the raw
    /// <see cref="VlessServerEntry.Server"/> field separately. Tap on the
    /// row body (NOT the refresh button) selects this server as the
    /// active one.
    /// </summary>
    private Control BuildServerRow(VlessServerEntry srv, string? activeServerName)
    {
        var key = AndroidStorage.BuildServerKey(srv);
        var hasResult = _srvResults.TryGetValue(key, out var result);
        var isTesting = _srvTestingKeys.Contains(key);
        var isActive = !string.IsNullOrEmpty(activeServerName)
                       && string.Equals(srv.Name, activeServerName, StringComparison.OrdinalIgnoreCase);

        // ── Radio dot (12×12) — filled when active. ──
        var radioOuter = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1.5),
            BorderBrush = isActive
                ? GetBrush("SuccessSolidBrush")
                : GetBrush("BorderStrongBrush"),
            Background = isActive ? GetBrush("SuccessSolidBrush") : Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (isActive)
        {
            radioOuter.Child = new Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        // ── Name + protocol subtitle stack ──
        // Desktop uses `DisplayName` (Name fallback to Server) and
        // `HostSubtitle` (e.g. "tcp + reality") — same here.
        var displayName = string.IsNullOrWhiteSpace(srv.Name) ? srv.Server : srv.Name;
        var hostSubtitle = BuildHostSubtitle(srv);

        var nameText = new TextBlock
        {
            Text = displayName,
            FontSize = 11,
            FontWeight = isActive ? FontWeight.Bold : FontWeight.SemiBold,
            Foreground = isActive ? GetBrush("AccentFgBrush") : GetBrush("TextPrimaryBrush"),
            FontFamily = new FontFamily("monospace"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var hostText = new TextBlock
        {
            Text = hostSubtitle,
            FontSize = 9,
            Foreground = GetBrush("TextMutedBrush"),
            FontFamily = new FontFamily("monospace"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsVisible = !string.IsNullOrEmpty(hostSubtitle),
        };
        // Wrap the name stack in a Border so the tap-to-select hit area is
        // explicitly bounded (excludes the row's refresh button). Pre-rev1
        // we used PointerReleased on the full card and walked the visual
        // tree to filter button presses — simpler to bound the hit area.
        var nameStack = new Border
        {
            Background = Brushes.Transparent,  // hit-test-friendly
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { nameText, hostText },
            },
        };
        ToolTip.SetTip(nameStack, Localization.SrvTipSelectServer);
        nameStack.PointerReleased += (s, e) => SelectServerAndClose(srv);

        // Mobile design 2026-05-11 — name+meta+ping+refresh 4-col layout.
        // The IP and Port cells collapsed into the meta-line under the
        // server name (see hostText override below). Comment kept so the
        // diff vs desktop's 6-col is traceable.
        _ = hostText; // hostText is replaced below with the meta-line
        var metaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(srv.Server)) metaParts.Add(srv.Server!);
        if (srv.Port > 0) metaParts.Add(":" + srv.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(hostSubtitle)) metaParts.Add(hostSubtitle);
        hostText.Text = string.Join(" · ", metaParts);
        hostText.IsVisible = !string.IsNullOrEmpty(hostText.Text);

        // ── Ping pill — colored Border with white text on status bg ──
        var (pingDisplay, _) = ResolveLatencyDisplay(hasResult ? result : null, isTesting);
        var pingBgBrush = ResolveLatencyBadgeBackground(hasResult ? result : null, isTesting);
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

        // ── Per-row Test button (⟳) ──
        var testBtn = new Avalonia.Controls.Button
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
            IsEnabled = !isTesting,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(testBtn, Localization.SrvTipTestRow);
        testBtn.Click += async (s, e) => await TestSingleServerAsync(srv);

        // Mobile design 2026-05-11 — 4-col row (radio · name+meta · ping
        // pill · refresh). IP and Port collapsed into the meta-line above.
        // Mirrors Mobile.html `.srv` grid-template-columns:16px 1fr auto auto.
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("14,*,Auto,24"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(radioOuter, 0);
        Grid.SetColumn(nameStack, 1);
        Grid.SetColumn(pingBadge, 2);
        Grid.SetColumn(testBtn, 3);
        grid.Children.Add(radioOuter);
        grid.Children.Add(nameStack);
        grid.Children.Add(pingBadge);
        grid.Children.Add(testBtn);

        // Compact, table-like row — desktop uses Padding="8,5"
        // CornerRadius="3" (RadiusXs) and NO per-row border. The outer
        // list card holds the visible border. Active row tints
        // AccentBgSubtleBrush; inactive lets SurfaceBaseBrush (the card
        // background) show through.
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

    /// <summary>
    /// Mirrors desktop <see cref="VPNRouter.App.ViewModels.ServerViewModel.HostSubtitle"/>.
    /// Returns "tcp + reality" / "hysteria2 + salamander" / "tuic + bbr" /
    /// "ss + chacha20-poly1305" / etc — the compact protocol+security
    /// subtitle shown beneath the server name. Empty string for legacy
    /// VLESS entries with no transport set so the row hides the subtitle.
    /// </summary>
    private static string BuildHostSubtitle(VlessServerEntry srv)
    {
        var protocol = (srv.Protocol ?? "vless").ToLowerInvariant();
        var parts = new List<string>();

        switch (protocol)
        {
            case "hysteria2":
                parts.Add("hysteria2");
                if (!string.IsNullOrWhiteSpace(srv.ObfsType))
                    parts.Add(srv.ObfsType.ToLowerInvariant());
                break;

            case "tuic":
                parts.Add("tuic");
                if (!string.IsNullOrWhiteSpace(srv.CongestionControl))
                    parts.Add(srv.CongestionControl.ToLowerInvariant());
                break;

            case "shadowsocks":
            case "ss":
                parts.Add("ss");
                if (!string.IsNullOrWhiteSpace(srv.Method))
                    parts.Add(srv.Method.ToLowerInvariant());
                if (!string.IsNullOrWhiteSpace(srv.Plugin))
                    parts.Add(srv.Plugin.ToLowerInvariant());
                break;

            default:
                // VLESS — keep desktop's "transport + security" format.
                var transport = srv.Transport?.Type;
                if (!string.IsNullOrWhiteSpace(transport))
                    parts.Add(transport!.ToLowerInvariant());
                if (!string.IsNullOrWhiteSpace(srv.Security) &&
                    !srv.Security.Equals("none", StringComparison.OrdinalIgnoreCase))
                    parts.Add(srv.Security.ToLowerInvariant());
                break;
        }

        return string.Join(" + ", parts);
    }

    /// <summary>
    /// Resolve a stored test result + testing flag to a (display text,
    /// status-coloured brush) pair. Mirrors desktop
    /// <c>ServerViewModel.PingDisplay</c> + <c>StatusDotBrush</c>: text is
    /// the same compact set ("42 ms" / "—" / "×" / "TLS ×" / "&lt;5 ms"),
    /// and the brush comes from the design tokens (SuccessSolidBrush /
    /// WarningSolidBrush / DangerSolidBrush / TextMutedBrush) so theme
    /// switches recolour without code changes.
    /// </summary>
    private (string text, IBrush brush) ResolveLatencyDisplay(AndroidStorage.ServerTestResultDto? result, bool isTesting)
    {
        if (isTesting)
            return ("…", GetBrush("TextMutedBrush"));
        if (result is null)
            return ("—", GetBrush("TextMutedBrush"));

        var status = (ServerProbeStatus)result.Status;
        var ms = result.LatencyMs;
        return status switch
        {
            ServerProbeStatus.Ok                   => ($"{ms} ms", GetBrush("SuccessSolidBrush")),
            ServerProbeStatus.Slow                 => ($"{ms} ms", GetBrush("WarningSolidBrush")),
            ServerProbeStatus.Implausible          => ("<5 ms", GetBrush("WarningSolidBrush")),
            ServerProbeStatus.TlsFailed            => ("TLS ×", GetBrush("DangerSolidBrush")),
            ServerProbeStatus.Unreachable          => ("×", GetBrush("DangerSolidBrush")),
            ServerProbeStatus.Timeout              => ("×", GetBrush("DangerSolidBrush")),
            ServerProbeStatus.SkippedNotApplicable => ("—", GetBrush("TextMutedBrush")),
            _                                      => ("—", GetBrush("TextMutedBrush")),
        };
    }

    /// <summary>
    /// Mobile-design ping-pill background. Mirrors
    /// <see cref="ResolveLatencyDisplay"/>'s status mapping but yields
    /// the solid-fill colour used as the pill's Background. The pill
    /// text is rendered white on top (see callers in
    /// <c>BuildAggregatedServerRow</c>) for the colored variants; the
    /// muted "—" / "…" branches return SurfaceSunken so the pill blends
    /// with the row background instead of showing a coloured chip when
    /// there's no real data yet.
    /// </summary>
    private IBrush ResolveLatencyBadgeBackground(AndroidStorage.ServerTestResultDto? result, bool isTesting)
    {
        if (isTesting || result is null)
            return GetBrush("SurfaceSunkenBrush");
        var status = (ServerProbeStatus)result.Status;
        return status switch
        {
            ServerProbeStatus.Ok                   => GetBrush("SuccessSolidBrush"),
            ServerProbeStatus.Slow                 => GetBrush("WarningSolidBrush"),
            ServerProbeStatus.Implausible          => GetBrush("WarningSolidBrush"),
            ServerProbeStatus.TlsFailed            => GetBrush("DangerSolidBrush"),
            ServerProbeStatus.Unreachable          => GetBrush("DangerSolidBrush"),
            ServerProbeStatus.Timeout              => GetBrush("DangerSolidBrush"),
            ServerProbeStatus.SkippedNotApplicable => GetBrush("SurfaceSunkenBrush"),
            _                                      => GetBrush("SurfaceSunkenBrush"),
        };
    }

    private void SelectServerAndClose(VlessServerEntry srv)
    {
        // AND-MIGRATE-OVERLAYS (2026-05-09): selecting a server in the
        // Servers tab closes the entire Advanced shell — same UX shape
        // as the old per-overlay close (return to Simple page so the
        // user can hit Connect on the freshly-active server).
        AndroidStorage.SetSelectedServerName(srv.Name);
        CloseAdvancedShell();
    }

    // ── Test-all batch ─────────────────────────────────────────────────────

    private async Task OnSrvTestAllClicked()
    {
        var sub = _srvCurrentSub;
        if (sub is null || sub.Servers.Count == 0) return;
        if (_srvTestAllCts is not null)
        {
            // Already running — treat as Stop.
            try { _srvTestAllCts.Cancel(); } catch { /* swallow */ }
            return;
        }

        _srvTestAllCts = new CancellationTokenSource();
        var ct = _srvTestAllCts.Token;

        try
        {
            var servers = sub.Servers.ToList();
            var total = servers.Count;
            var done = 0;

            // Mark all as testing up-front so the UI shows spinner state
            // even before the semaphore admits the probe.
            foreach (var srv in servers)
            {
                _srvTestingKeys.Add(AndroidStorage.BuildServerKey(srv));
            }
            RebuildServerList();
            UpdateProgressText(0, total);
            if (_srvTestAllBtn is not null)
                _srvTestAllBtn.Content = Localization.SrvTesting;

            // Concurrency = 4 (see plan doc — mobile NAT/CPU/battery budget).
            using var sem = new SemaphoreSlim(4);
            var tasks = servers.Select(async srv =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var result = await TcpTlsProbe.ProbeServerAsync(srv, ct);
                    ApplyResult(srv, result);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled — leave the row as-is (testing flag clears in finally below).
                }
                catch
                {
                    // Catastrophic — apply Unreachable so the row at least
                    // shows × instead of an indefinite spinner.
                    ApplyResult(srv, new ServerProbeResult(ServerProbeStatus.Unreachable, 0, "probe error"));
                }
                finally
                {
                    sem.Release();
                    var n = Interlocked.Increment(ref done);
                    Dispatcher.UIThread.Post(() => UpdateProgressText(n, total));
                }
            });
            await Task.WhenAll(tasks);

            // Finalize summary + reachable count.
            var reachable = sub.Servers.Count(srv =>
            {
                if (!_srvResults.TryGetValue(AndroidStorage.BuildServerKey(srv), out var r)) return false;
                var s = (ServerProbeStatus)r.Status;
                return s is ServerProbeStatus.Ok or ServerProbeStatus.Slow;
            });
            if (_srvStatusText is not null)
                _srvStatusText.Text = string.Format(Localization.SrvProgressDoneFmt, reachable, total);
        }
        finally
        {
            // Clear all testing flags + persist results once at the end so
            // we don't hammer SharedPreferences for each individual probe.
            _srvTestingKeys.Clear();
            AndroidStorage.SetServerTestResults(_srvResults);
            try { _srvTestAllCts?.Dispose(); } catch { /* swallow */ }
            _srvTestAllCts = null;
            if (_srvTestAllBtn is not null)
                _srvTestAllBtn.Content = Localization.AdvServersTestAll;
            RebuildServerList();
        }
    }

    private async Task TestSingleServerAsync(VlessServerEntry srv)
    {
        var key = AndroidStorage.BuildServerKey(srv);
        if (_srvTestingKeys.Contains(key)) return;
        _srvTestingKeys.Add(key);
        RebuildServerList();
        try
        {
            var result = await TcpTlsProbe.ProbeServerAsync(srv, CancellationToken.None);
            ApplyResult(srv, result);
            AndroidStorage.SetServerTestResults(_srvResults);
        }
        catch
        {
            ApplyResult(srv, new ServerProbeResult(ServerProbeStatus.Unreachable, 0, "probe error"));
            AndroidStorage.SetServerTestResults(_srvResults);
        }
        finally
        {
            _srvTestingKeys.Remove(key);
            RebuildServerList();
        }
    }

    private void ApplyResult(VlessServerEntry srv, ServerProbeResult result)
    {
        var key = AndroidStorage.BuildServerKey(srv);
        _srvResults[key] = new AndroidStorage.ServerTestResultDto
        {
            Status = (int)result.Status,
            LatencyMs = result.LatencyMs,
            LastTestedAt = DateTimeOffset.UtcNow,
            Error = result.Error,
        };
        // Re-render only the affected row by rebuilding the whole list —
        // ListBox would be cheaper but we use plain StackPanel to keep
        // the per-row Border.PointerReleased handler simple.
        Dispatcher.UIThread.Post(RebuildServerList);
    }

    private void UpdateProgressText(int done, int total)
    {
        if (_srvStatusText is null) return;
        _srvStatusText.Text = string.Format(Localization.SrvProgressFmt, done, total);
    }

    // ── Sort toggle ────────────────────────────────────────────────────────

    private void OnSrvSortToggleClicked()
    {
        _srvSortByLatency = !_srvSortByLatency;
        if (_srvSortToggle is not null)
        {
            _srvSortToggle.Content = _srvSortByLatency
                ? Localization.SrvSortByLatencyAsc
                : Localization.SrvSortByOriginal;
        }
        RebuildServerList();
    }

    // ── Localization refresh on language toggle ────────────────────────────

    private void RefreshServerListLocalizedStrings()
    {
        if (_srvCurrentSub is not null && _srvTitle is not null)
        {
            _srvTitle.Text = string.Format(Localization.ServerListTitleFmt,
                string.IsNullOrWhiteSpace(_srvCurrentSub.Name) ? "(no name)" : _srvCurrentSub.Name);
        }
        if (_srvTestAllBtn is not null && _srvTestAllCts is null)
            _srvTestAllBtn.Content = Localization.AdvServersTestAll;
        if (_srvSortToggle is not null)
        {
            _srvSortToggle.Content = _srvSortByLatency
                ? Localization.SrvSortByLatencyAsc
                : Localization.SrvSortByOriginal;
        }
        if (_srvEmptyHint is not null) _srvEmptyHint.Text = Localization.SrvEmptyHint;
        if (_srvColServer is not null) _srvColServer.Text = Localization.ColServer;
        if (_srvColIp is not null) _srvColIp.Text = Localization.ColIp;
        if (_srvColPing is not null)
        {
            _srvColPing.Text = Localization.ColPing;
            ToolTip.SetTip(_srvColPing, Localization.ColPingTooltip);
        }
        if (_srvColPort is not null) _srvColPort.Text = Localization.ColPort;

        // Phase B (AND-ADV-SERVERS-SUBSCRIBE) — sub-tab segments + footer
        // action row + Custom JSON sub-panel labels.
        if (_srvSubTabServersBtn is not null)
            _srvSubTabServersBtn.Content = Localization.AdvServersSubTabServers;
        if (_srvSubTabCustomJsonBtn is not null)
            _srvSubTabCustomJsonBtn.Content = Localization.AdvServersSubTabCustomJson;
        if (_srvDeepVerifyBtn is not null)
        {
            _srvDeepVerifyBtn.Content = Localization.AdvServersDeepVerify;
            ToolTip.SetTip(_srvDeepVerifyBtn, Localization.AdvServersDeepVerifyAndroidNote);
        }
        if (_srvVlessUriInput is not null)
            _srvVlessUriInput.Watermark = Localization.WmVlessUri;
        if (_srvRemoveBtn is not null)
        {
            _srvRemoveBtn.Content = Localization.AdvServersRemove;
            ToolTip.SetTip(_srvRemoveBtn, Localization.TipDeleteServer);
        }
        if (_srvAddBtn is not null) _srvAddBtn.Content = Localization.AdvServersAddServers;
        if (_srvCustomJsonExplainer is not null)
            _srvCustomJsonExplainer.Text = Localization.AdvServersCustomJsonExplainer;
        if (_srvCustomJsonInput is not null)
            _srvCustomJsonInput.Watermark = Localization.CcCustomWatermark;
        if (_srvCustomJsonValidateBtn is not null)
            _srvCustomJsonValidateBtn.Content = Localization.CcValidateButton;
        if (_srvCustomJsonSaveBtn is not null)
            _srvCustomJsonSaveBtn.Content = Localization.CcSaveButton;
        if (_srvCustomJsonClearBtn is not null)
            _srvCustomJsonClearBtn.Content = Localization.CcClearButton;

        // Servers tab still mounted in the Advanced shell? Rebuild rows so
        // localized status badges flip to the new locale.
        if (_srvListStack is not null) RebuildServerList();
    }
}
