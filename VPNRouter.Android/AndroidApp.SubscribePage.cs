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
using IconLabel = VPNRouter.UI.Controls.IconLabel;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    private StackPanel? _subsListStack;
    private TextBlock? _subsEmptyHint;
    private TextBox? _subsNewName;
    private TextBox? _subsNewUrl;
    private Avalonia.Controls.Button? _subsAddBtn;
    private Avalonia.Controls.Button? _subsRefreshAllBtn;
    private TextBlock? _subsSectionLabel;
    private TextBlock? _subsRefreshAllStatus;
    private Avalonia.Controls.CheckBox? _subsAutoSelectChk;

    private StackPanel? _subsAggListStack;
    private TextBlock? _subsAggEmptyHint;
    private TextBlock? _subsAggColServer;
    private TextBlock? _subsAggColPing;
    private Avalonia.Controls.Button? _subsAggTestAllBtn;
    private Avalonia.Controls.Button? _subsAggDeepVerifyBtn;
    private TextBlock? _subsAggStatusText;
    private TextBlock? _subsAggSectionHeaderLabel;

    private readonly HashSet<string> _subsAggTestingKeys = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, AndroidStorage.ServerTestResultDto> _subsAggResults =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _subsAggTestAllCts;

    // Coalesces a burst of probe results into one aggregated-list rebuild; Interlocked because up to 4 probes complete concurrently.
    private int _subsAggRebuildScheduled;

    private List<SubscriptionEntry> _subs = new();

    private readonly HashSet<string> _refreshingIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingDeleteId;
    private string? _editingId;
    private DateTime _lastDeleteTapAt = DateTime.MinValue;

    private Control BuildSubscribeTabContent()
    {
        var aggServerSection = BuildSubscribeAggregatedServerSection();

        var middleActionRow = BuildSubscribeFooterActions();

        _subsSectionLabel = new TextBlock
        {
            Text = Localization.SubscriptionsSection,
            FontSize = UiScale.Fs(12),
            FontWeight = FontWeight.Bold,
            Foreground = GetBrush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _subsRefreshAllStatus = new TextBlock
        {
            Text = string.Empty,
            FontSize = UiScale.Fs(10),
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

        _subsListStack = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(12, 8, 12, 8),
        };
        _subsEmptyHint = new TextBlock
        {
            Text = Localization.LblAddSubscriptionHint,
            FontSize = UiScale.Fs(11),
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
            MaxHeight = 180,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = GetBrush("SurfaceAppBrush"),
        };

        _subsNewName = new TextBox
        {
            Watermark = Localization.AdvSubscribeNameLabel,
            FontSize = UiScale.Fs(10),
            Padding = new Thickness(6, 4),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
        };
        _subsNewUrl = new TextBox
        {
            Watermark = Localization.AdvSubscribeUrlLabel,
            FontSize = UiScale.Fs(10),
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
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(12, 5),
            Background = GetBrush("AccentSolidBrush"),
            Foreground = GetBrush("AccentOnSolidBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        };
        _subsAddBtn.Click += OnSubsAddClicked;

        var subsQrBtn = new Avalonia.Controls.Button
        {
            Content = IconContent(UiIcons.ScanQrCode, 20),
            Padding = new Thickness(0),
            Width = 40,
            Background = GetBrush("AccentBgSubtleBrush"),
            Foreground = GetBrush("AccentFgBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(subsQrBtn, Localization.SmpScanQrButton);
        subsQrBtn.Click += OnSubscribeQrScanClicked;

        var addFormRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("100,*,Auto,Auto"),
            ColumnSpacing = 4,
        };
        Grid.SetColumn(_subsNewName, 0);
        Grid.SetColumn(_subsNewUrl, 1);
        Grid.SetColumn(subsQrBtn, 2);
        Grid.SetColumn(_subsAddBtn, 3);
        addFormRow.Children.Add(_subsNewName);
        addFormRow.Children.Add(_subsNewUrl);
        addFormRow.Children.Add(subsQrBtn);
        addFormRow.Children.Add(_subsAddBtn);

        var addFormBorder = new Border
        {
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = GetBrush("SurfaceBaseBrush"),
            Padding = new Thickness(10, 6, 10, 10),
            Child = addFormRow,
        };

        _subsAutoSelectChk = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetAutoSelectBestServer(),
            Content = Localization.AutoSelectBestServer,
            FontSize = UiScale.Fs(11),
            Foreground = GetBrush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_subsAutoSelectChk, Localization.AutoSelectBestServerTip);
        _subsAutoSelectChk.IsCheckedChanged += (_, _) =>
            AndroidStorage.SetAutoSelectBestServer(_subsAutoSelectChk.IsChecked == true);
        var autoSelectBorder = new Border
        {
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = GetBrush("SurfaceBaseBrush"),
            Padding = new Thickness(10, 6, 10, 6),
            Child = _subsAutoSelectChk,
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(addFormBorder, Dock.Bottom);
        DockPanel.SetDock(subsListScroller, Dock.Bottom);
        DockPanel.SetDock(sectionHeaderBorder, Dock.Bottom);
        DockPanel.SetDock(middleActionRow, Dock.Bottom);
        DockPanel.SetDock(autoSelectBorder, Dock.Bottom);
        dock.Children.Add(addFormBorder);
        dock.Children.Add(subsListScroller);
        dock.Children.Add(sectionHeaderBorder);
        dock.Children.Add(middleActionRow);
        dock.Children.Add(autoSelectBorder);
        dock.Children.Add(aggServerSection);

        return new Border
        {
            Background = GetBrush("SurfaceAppBrush"),
            Child = dock,
        };
    }

    private DockPanel BuildSubscribeAggregatedServerSection()
    {
        _subsAggColServer = new TextBlock
        {
            Text = Localization.ColServer,
            FontSize = UiScale.Fs(9),
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _subsAggColPing = new TextBlock
        {
            Text = Localization.ColPing,
            FontSize = UiScale.Fs(9),
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(_subsAggColPing, Localization.ColPingTooltip);
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

        _subsAggListStack = new StackPanel { Spacing = 0 };
        _subsAggEmptyHint = new TextBlock
        {
            Text = Localization.AdvSubscribeAggregatedEmpty,
            FontSize = UiScale.Fs(11),
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

    private Border BuildSubscribeFooterActions()
    {
        _subsAggTestAllBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvServersTestAll,
            FontSize = UiScale.Fs(10),
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(12, 6),
            MinHeight = 36,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = GetBrush("AccentBgMutedBrush"),
            Foreground = GetBrush("AccentFgBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        };
        ToolTip.SetTip(_subsAggTestAllBtn, Localization.SrvTipTestAll);
        _subsAggTestAllBtn.Click += async (_, _) => await OnSubsAggTestAllClicked();

        _subsAggDeepVerifyBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvServersDeepVerify,
            FontSize = UiScale.Fs(10),
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(12, 6),
            MinHeight = 36,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = GetBrush("AccentBgMutedBrush"),
            Foreground = GetBrush("AccentFgBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        };
        ToolTip.SetTip(_subsAggDeepVerifyBtn, Localization.AdvServersDeepVerifyAndroidNote);
        _subsAggDeepVerifyBtn.Click += async (_, _) => await OnSubsAggDeepVerifyClicked();

        _subsAggStatusText = new TextBlock
        {
            Text = string.Empty,
            FontSize = UiScale.Fs(10),
            Foreground = GetBrush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _subsRefreshAllBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvSubscribeRefreshAll,
            FontSize = UiScale.Fs(10),
            Padding = new Thickness(8, 3),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceRaisedBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        _subsRefreshAllBtn.Click += OnSubsRefreshAllClicked;

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

    private Control BuildAggregatedServerRow(VlessServerEntry srv, string? activeServerName)
    {
        var key = AndroidStorage.BuildServerKey(srv);
        var hasResult = _subsAggResults.TryGetValue(key, out var result);
        return BuildServerRowCore(srv, activeServerName, hasResult ? result : null,
            _subsAggTestingKeys.Contains(key), TestSingleAggregatedServerAsync);
    }

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
            try { _subsAggTestAllCts.Cancel(); } catch { }
            return;
        }

        _subsAggTestAllCts = new CancellationTokenSource();
        var ct = _subsAggTestAllCts.Token;
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
                catch (OperationCanceledException) { }
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
            try { _subsAggTestAllCts?.Dispose(); } catch { }
            _subsAggTestAllCts = null;
            if (_subsAggTestAllBtn is not null)
                _subsAggTestAllBtn.Content = Localization.AdvServersTestAll;
            RebuildAggregatedServerList();
        }
    }

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
        ScheduleAggregatedServerListRebuild();
    }

    private void ScheduleAggregatedServerListRebuild()
    {
        if (Interlocked.CompareExchange(ref _subsAggRebuildScheduled, 1, 0) != 0)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _subsAggRebuildScheduled, 0);
            RebuildAggregatedServerList();
        }, DispatcherPriority.Background);
    }

    private void UpdateAggregatedProgressText(int done, int total)
    {
        if (_subsAggStatusText is null) return;
        _subsAggStatusText.Text = string.Format(Localization.SrvProgressFmt, done, total);
    }

    private void ReseedSubscribeTabState()
    {
        _subs = AndroidStorage.GetSubscriptions();
        _refreshingIds.Clear();
        _pendingDeleteId = null;
        _editingId = null;
        _subsAggTestingKeys.Clear();
        _subsAggResults = AndroidStorage.GetServerTestResults();
        if (_subsAggStatusText is not null) _subsAggStatusText.Text = string.Empty;
        if (_subsAutoSelectChk is not null)
            _subsAutoSelectChk.IsChecked = AndroidStorage.GetAutoSelectBestServer();
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
            RebuildAggregatedServerList();
            return;
        }
        _subsEmptyHint.IsVisible = false;

        foreach (var sub in _subs)
        {
            _subsListStack.Children.Add(BuildSubCard(sub));
        }
        RebuildAggregatedServerList();
    }

    private Control BuildSubCard(SubscriptionEntry sub)
    {
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
            RebuildAggregatedServerList();
        };

        var nameText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(sub.Name) ? "(no name)" : sub.Name,
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
            FontFamily = new FontFamily("monospace"),
            Foreground = GetBrush("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var metadataText = new TextBlock
        {
            Text = FormatSubMetadata(sub),
            FontSize = UiScale.Fs(9),
            FontFamily = new FontFamily("monospace"),
            Foreground = GetBrush("TextMutedBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        ToolTip.SetTip(metadataText, Localization.TipSubscriptionMetadata);
        var infoPanel = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        infoPanel.Children.Add(nameText);
        infoPanel.Children.Add(metadataText);
        var userInfo = VPNRouter.Core.Services.SubscriptionUserInfo.Parse(sub.UserInfo);
        if (userInfo is not null)
        {
            var summary = userInfo.FormatSummary(DateTimeOffset.UtcNow);
            if (!string.IsNullOrEmpty(summary))
            {
                infoPanel.Children.Add(new TextBlock
                {
                    Text = summary,
                    FontSize = UiScale.Fs(9),
                    FontWeight = FontWeight.SemiBold,
                    FontFamily = new FontFamily("monospace"),
                    Foreground = GetBrush("AccentFgBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
        }
        var nameStack = new Border
        {
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Child = infoPanel,
        };
        nameStack.PointerReleased += (s, e) => OpenServerListOverlay(sub);

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

        var editBtn = StyledRowActionButton(UiIcons.Pencil, Localization.TipEditSubscription);
        editBtn.Click += (s, e) => StartEditUrl(sub);

        var refreshBtn = StyledRowActionButton(UiIcons.RefreshCw, Localization.TipRefreshSubscription);
        refreshBtn.IsEnabled = !_refreshingIds.Contains(sub.Id);
        refreshBtn.Click += async (s, e) => await RefreshOneAsync(sub);

        var deleteBtn = StyledRowActionButton(UiIcons.Trash, Localization.TipRemoveSubscription);
        if (_pendingDeleteId == sub.Id)
        {
            deleteBtn.Content = new IconLabel(UiIcons.Trash, "?", UiScale.Ic(18)) { Spacing = 2 };
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

        Control? editorRow = null;
        if (_editingId == sub.Id)
        {
            var nameBox = new TextBox
            {
                Text = sub.Name,
                FontSize = UiScale.Fs(11),
                Padding = new Thickness(8, 6),
                CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            };
            var urlBox = new TextBox
            {
                Text = sub.Url,
                FontSize = UiScale.Fs(11),
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

        return new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            Padding = new Thickness(8, 5),
            Child = content,
        };
    }

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

    private Avalonia.Controls.Button StyledRowActionButton(string icon, string? tooltip)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = IconContent(icon, UiScale.Ic(18)),
            FontSize = UiScale.Fs(11),
            Padding = new Thickness(2),
            MinWidth = 40,
            MinHeight = 40,
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
        var armedRecently = _pendingDeleteId == sub.Id
                            && (now - _lastDeleteTapAt).TotalSeconds < 4;
        if (!armedRecently)
        {
            _pendingDeleteId = sub.Id;
            _lastDeleteTapAt = now;
            RebuildSubsList();
            return;
        }

        _pendingDeleteId = null;
        _subs.RemoveAll(s => string.Equals(s.Id, sub.Id, StringComparison.Ordinal));
        AndroidStorage.SetSubscriptions(_subs);
        RebuildSubsList();
    }

    private void OnSubscribeQrScanClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var activity = MainActivity.Instance;
        if (activity is null) return;
        MainActivity.PendingQrScanCallback = (success, text) =>
        {
            Dispatcher.UIThread.Post(async () =>
            {
                if (!success)
                {
                    if (text == "cancelled") return;
                    var msg = text switch
                    {
                        "permission_denied" => Localization.SmpQrPermissionDenied,
                        _                    => Localization.SmpQrNotRecognized,
                    };
                    ShowMenuFeedback(msg);
                    return;
                }
                await ApplyScannedTextAsync(text);
            });
        };
        activity.RequestQrCodeScan();
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
        catch { }
    }

    private void RefreshSubsLocalizedStrings()
    {
        if (_subsSectionLabel is not null) _subsSectionLabel.Text = Localization.SubscriptionsSection;
        if (_subsRefreshAllBtn is not null) _subsRefreshAllBtn.Content = Localization.AdvSubscribeRefreshAll;
        if (_subsAddBtn is not null) _subsAddBtn.Content = Localization.AddSubscription;
        if (_subsNewName is not null) _subsNewName.Watermark = Localization.AdvSubscribeNameLabel;
        if (_subsNewUrl is not null) _subsNewUrl.Watermark = Localization.AdvSubscribeUrlLabel;
        if (_subsEmptyHint is not null) _subsEmptyHint.Text = Localization.LblAddSubscriptionHint;
        if (_subsAutoSelectChk is not null)
        {
            _subsAutoSelectChk.Content = Localization.AutoSelectBestServer;
            ToolTip.SetTip(_subsAutoSelectChk, Localization.AutoSelectBestServerTip);
        }

        if (_subsAggColServer is not null) _subsAggColServer.Text = Localization.ColServer;
        if (_subsAggColPing is not null)
        {
            _subsAggColPing.Text = Localization.ColPing;
            ToolTip.SetTip(_subsAggColPing, Localization.ColPingTooltip);
        }
        if (_subsAggEmptyHint is not null)
            _subsAggEmptyHint.Text = Localization.AdvSubscribeAggregatedEmpty;
        if (_subsAggTestAllBtn is not null && _subsAggTestAllCts is null)
            _subsAggTestAllBtn.Content = Localization.AdvServersTestAll;
        if (_subsAggDeepVerifyBtn is not null)
        {
            _subsAggDeepVerifyBtn.Content = Localization.AdvServersDeepVerify;
            ToolTip.SetTip(_subsAggDeepVerifyBtn, Localization.AdvServersDeepVerifyAndroidNote);
        }

        if (_subsListStack is not null) RebuildSubsList();
    }
}
