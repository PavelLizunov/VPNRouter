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
    /// tab inside the Advanced shell. Layout (top→bottom): subscription
    /// label header → action row (Test all + sort toggle + status) →
    /// scrollable list. No title bar / close — the shell provides those.
    /// </summary>
    private Control BuildServersTabContent()
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
            Margin = new Thickness(12, 8, 12, 0),
        };

        // ── Action row: Test all + sort toggle + status text ──
        _srvTestAllBtn = new Avalonia.Controls.Button
        {
            Content = Localization.SrvTestAll,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(12, 6),
            Background = GetBrush("AccentSolidBrush"),
            Foreground = GetBrush("AccentOnSolidBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        };
        ToolTip.SetTip(_srvTestAllBtn, Localization.SrvTipTestAll);
        _srvTestAllBtn.Click += async (s, e) => await OnSrvTestAllClicked();

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

        var actionRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            ColumnSpacing = 8,
            Margin = new Thickness(12, 8, 12, 4),
        };
        Grid.SetColumn(_srvTestAllBtn, 0);
        Grid.SetColumn(_srvSortToggle, 1);
        Grid.SetColumn(_srvStatusText, 2);
        actionRow.Children.Add(_srvTestAllBtn);
        actionRow.Children.Add(_srvSortToggle);
        actionRow.Children.Add(_srvStatusText);

        // ── Column header strip (desktop ServersPage rows 140-154 parity) ──
        // Same column widths as the row template below — header labels
        // align to the cell content. Tiny SemiBold caps in muted text.
        _srvColServer = new TextBlock
        {
            Text = Localization.ColServer,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _srvColIp = new TextBlock
        {
            Text = Localization.ColIp,
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
        _srvColPort = new TextBlock
        {
            Text = Localization.ColPort,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("14,*,100,42,40,24"),
            ColumnSpacing = 8,
            Margin = new Thickness(8, 2, 8, 4),
        };
        Grid.SetColumn(_srvColServer, 1);
        Grid.SetColumn(_srvColIp, 2);
        Grid.SetColumn(_srvColPing, 3);
        Grid.SetColumn(_srvColPort, 4);
        headerGrid.Children.Add(_srvColServer);
        headerGrid.Children.Add(_srvColIp);
        headerGrid.Children.Add(_srvColPing);
        headerGrid.Children.Add(_srvColPort);

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
            Margin = new Thickness(12, 0, 12, 12),
            Child = listScroller,
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_srvTitle!, Dock.Top);
        DockPanel.SetDock(actionRow, Dock.Top);
        DockPanel.SetDock(headerHost, Dock.Top);
        dock.Children.Add(_srvTitle!);
        dock.Children.Add(actionRow);
        dock.Children.Add(headerHost);
        dock.Children.Add(listCard);

        return new Border
        {
            Background = GetBrush("SurfaceAppBrush"),
            Child = dock,
        };
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
        if (servers.Count == 0)
        {
            _srvEmptyHint.IsVisible = true;
            return;
        }
        _srvEmptyHint.IsVisible = false;

        var ordered = OrderServers(servers);
        var activeName = AndroidStorage.GetSelectedServerName();
        foreach (var srv in ordered)
        {
            _srvListStack.Children.Add(BuildServerRow(srv, activeName));
        }
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

        // ── IP cell — mono 9px muted, ellipsis trim ──
        var ipText = new TextBlock
        {
            Text = srv.Server ?? string.Empty,
            FontSize = 9,
            FontFamily = new FontFamily("monospace"),
            Foreground = GetBrush("TextMutedBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ── Ping cell — status-coloured mono text ("42 ms" / "—" / "×") ──
        var (pingDisplay, pingBrush) = ResolveLatencyDisplay(hasResult ? result : null, isTesting);
        var pingText = new TextBlock
        {
            Text = pingDisplay,
            FontSize = 9,
            FontFamily = new FontFamily("monospace"),
            Foreground = pingBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (hasResult && !string.IsNullOrEmpty(result?.Error))
            ToolTip.SetTip(pingText, result.Error);

        // ── Port cell — mono 9px muted, centered ──
        var portText = new TextBlock
        {
            Text = srv.Port.ToString(),
            FontSize = 9,
            FontFamily = new FontFamily("monospace"),
            Foreground = GetBrush("TextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

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

        // 6-column row matching desktop ServersPage.axaml line 188:
        // ColumnDefinitions="14,*,100,42,40,24" with ColumnSpacing="8".
        // Desktop has a 7th delete column for VLESS-direct entries; on
        // Android the servers come from a subscription, so deletion lives
        // in the subscription card's ✕ button — we drop the per-row delete.
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("14,*,100,42,40,24"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(radioOuter, 0);
        Grid.SetColumn(nameStack, 1);
        Grid.SetColumn(ipText, 2);
        Grid.SetColumn(pingText, 3);
        Grid.SetColumn(portText, 4);
        Grid.SetColumn(testBtn, 5);
        grid.Children.Add(radioOuter);
        grid.Children.Add(nameStack);
        grid.Children.Add(ipText);
        grid.Children.Add(pingText);
        grid.Children.Add(portText);
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
                _srvTestAllBtn.Content = Localization.SrvTestAll;
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
            _srvTestAllBtn.Content = Localization.SrvTestAll;
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
        // Servers tab still mounted in the Advanced shell? Rebuild rows so
        // localized status badges flip to the new locale.
        if (_srvListStack is not null) RebuildServerList();
    }
}
