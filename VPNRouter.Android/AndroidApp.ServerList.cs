using Avalonia;
using Avalonia.Controls;
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
    private Border? _srvOverlay;
    private TextBlock? _srvTitle;
    private Avalonia.Controls.Button? _srvCloseBtn;
    private Avalonia.Controls.Button? _srvTestAllBtn;
    private Avalonia.Controls.Button? _srvSortToggle;
    private TextBlock? _srvStatusText;
    private StackPanel? _srvListStack;
    private TextBlock? _srvEmptyHint;

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
    /// Build the fullscreen Server-list overlay. Layout (top→bottom):
    /// title bar (× close + "Серверы · &lt;sub name&gt;") → action row
    /// (Test all + sort toggle + status) → scrollable list.
    /// </summary>
    private Border BuildServerListOverlay()
    {
        _srvTitle = new TextBlock
        {
            Text = string.Empty,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _srvCloseBtn = new Avalonia.Controls.Button
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
            Foreground = GetBrush("TextSecondaryBrush"),
        };
        _srvCloseBtn.Click += (s, e) => CloseServerListOverlay();

        var titleBarGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(8, 4, 4, 4),
        };
        Grid.SetColumn(_srvTitle, 0);
        Grid.SetColumn(_srvCloseBtn, 1);
        titleBarGrid.Children.Add(_srvTitle);
        titleBarGrid.Children.Add(_srvCloseBtn);

        var titleBarBorder = new Border
        {
            Background = GetBrush("SurfaceRaisedBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4),
            Child = titleBarGrid,
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

        // ── Body: scrollable per-server card list ──
        _srvListStack = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(12, 4, 12, 12),
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
            Background = GetBrush("SurfaceAppBrush"),
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleBarBorder, Dock.Top);
        DockPanel.SetDock(actionRow, Dock.Top);
        dock.Children.Add(titleBarBorder);
        dock.Children.Add(actionRow);
        dock.Children.Add(listScroller);

        return new Border
        {
            Background = GetBrush("SurfaceAppBrush"),
            IsVisible = false,
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
        if (_srvOverlay is null) return;
        _srvCurrentSub = sub;
        _srvResults = AndroidStorage.GetServerTestResults();
        _srvTestingKeys.Clear();
        _srvSortByLatency = false;
        if (_srvSortToggle is not null)
            _srvSortToggle.Content = Localization.SrvSortByOriginal;
        if (_srvTitle is not null)
            _srvTitle.Text = string.Format(Localization.ServerListTitleFmt,
                string.IsNullOrWhiteSpace(sub.Name) ? "(no name)" : sub.Name);
        if (_srvStatusText is not null)
            _srvStatusText.Text = string.Empty;
        RebuildServerList();
        _srvOverlay.IsVisible = true;
    }

    private void CloseServerListOverlay()
    {
        // Cancel any in-flight Test all batch so we don't keep firing
        // probes against a phone the user has navigated away from.
        try { _srvTestAllCts?.Cancel(); } catch { /* swallow */ }
        if (_srvOverlay is not null) _srvOverlay.IsVisible = false;
        // Refresh SimplePage's inline server-list reflection — selecting
        // a server inside the overlay flips KeySelectedServerName but the
        // SimplePage form needs a re-read.
        ReloadServerList();
        UpdateConfigSummary();
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
    /// from <c>ServersPage.axaml</c> lines 178-238 (radio dot + name +
    /// host:port + colored latency badge + Test button). Tap on the row
    /// body (NOT badges/buttons) selects this server as the active one.
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

        // ── Name + host:port stack ──
        var nameText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(srv.Name)
                ? $"{srv.Server}:{srv.Port}"
                : srv.Name,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = isActive ? GetBrush("AccentFgBrush") : GetBrush("TextPrimaryBrush"),
            FontFamily = new FontFamily("monospace"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var hostText = new TextBlock
        {
            Text = $"{srv.Server}:{srv.Port}",
            FontSize = 9,
            Foreground = GetBrush("TextMutedBrush"),
            FontFamily = new FontFamily("monospace"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Wrap the name stack in a Border so we can put the tap-to-select
        // handler on a region that explicitly excludes the Test button +
        // latency badge. Pre-rev1 we attached PointerReleased to the whole
        // card and walked the visual tree to filter out button presses —
        // simpler to bound the hit area instead.
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

        // ── Latency badge ──
        var (badgeText, badgeBg) = isTesting
            ? (Localization.SrvTesting, "#9CA3AF")
            : ResolveBadgeText(hasResult ? result : null);
        var badge = new Border
        {
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            Padding = new Thickness(6, 2),
            Background = TryParseBrush(badgeBg) ?? new SolidColorBrush(Color.Parse("#9CA3AF")),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = badgeText,
                Foreground = Brushes.White,
                FontSize = 9,
                FontWeight = FontWeight.SemiBold,
            },
        };
        if (hasResult && !string.IsNullOrEmpty(result?.Error))
            ToolTip.SetTip(badge, result.Error);

        // ── Per-row Test button (⟳) ──
        var testBtn = new Avalonia.Controls.Button
        {
            Content = "⟳",
            FontSize = 13,
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = GetBrush("TextSecondaryBrush"),
            IsEnabled = !isTesting,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(testBtn, Localization.SrvTipTestRow);
        testBtn.Click += async (s, e) => await TestSingleServerAsync(srv);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(radioOuter, 0);
        Grid.SetColumn(nameStack, 1);
        Grid.SetColumn(badge, 2);
        Grid.SetColumn(testBtn, 3);
        grid.Children.Add(radioOuter);
        grid.Children.Add(nameStack);
        grid.Children.Add(badge);
        grid.Children.Add(testBtn);

        return new Border
        {
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Background = isActive
                ? GetBrush("AccentBgSubtleBrush")
                : GetBrush("SurfaceBaseBrush"),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Padding = new Thickness(10, 8),
            Child = grid,
        };
    }

    /// <summary>
    /// Map a stored test result to a (badge text, hex background) pair.
    /// Hex codes mirror <c>AndroidApp.FreeConfigs.cs:716-727</c> for
    /// visual consistency with the Free Configs tab — same probe means
    /// same color.
    /// </summary>
    private (string text, string hex) ResolveBadgeText(AndroidStorage.ServerTestResultDto? result)
    {
        if (result is null) return (Localization.SrvNeverTested, "#9CA3AF");
        var status = (ServerProbeStatus)result.Status;
        var ms = result.LatencyMs;
        return status switch
        {
            ServerProbeStatus.Ok when ms < 100 => ($"{ms} ms", "#22C55E"),
            ServerProbeStatus.Ok when ms < 300 => ($"{ms} ms", "#65A30D"),
            ServerProbeStatus.Ok when ms < 800 => ($"{ms} ms", "#F59E0B"),
            ServerProbeStatus.Ok                => ($"{ms} ms", "#EF4444"),
            ServerProbeStatus.Slow              => ($"{ms} ms", "#EF4444"),
            ServerProbeStatus.TlsFailed         => (Localization.SrvTlsFailed, "#F97316"),
            ServerProbeStatus.Timeout           => (Localization.SrvUnreachable, "#DC2626"),
            ServerProbeStatus.Unreachable       => (Localization.SrvUnreachable, "#DC2626"),
            ServerProbeStatus.Implausible       => (Localization.SrvImplausible, "#DC2626"),
            ServerProbeStatus.SkippedNotApplicable => (Localization.SrvNeverTested, "#9CA3AF"),
            _ => (Localization.SrvNeverTested, "#9CA3AF"),
        };
    }

    private void SelectServerAndClose(VlessServerEntry srv)
    {
        AndroidStorage.SetSelectedServerName(srv.Name);
        CloseServerListOverlay();
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
        if (_srvOverlay?.IsVisible == true) RebuildServerList();
    }
}
