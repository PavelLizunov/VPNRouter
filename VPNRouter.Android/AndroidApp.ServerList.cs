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
using IconView = VPNRouter.UI.Controls.IconView;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    private TextBlock? _srvTitle;
    private Avalonia.Controls.Button? _srvTestAllBtn;
    private Avalonia.Controls.Button? _srvSortToggle;
    private TextBlock? _srvStatusText;
    private StackPanel? _srvListStack;
    private TextBlock? _srvEmptyHint;
    private TextBlock? _srvColServer;
    private TextBlock? _srvColPing;

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

    private TextBox? _srvCustomJsonInput;
    private TextBlock? _srvCustomJsonStatus;
    private IconView? _srvCustomJsonStatusIcon;
    private Avalonia.Controls.Button? _srvCustomJsonSaveBtn;
    private Avalonia.Controls.Button? _srvCustomJsonClearBtn;
    private Avalonia.Controls.Button? _srvCustomJsonValidateBtn;
    private TextBlock? _srvCustomJsonExplainer;

    private SubscriptionEntry? _srvCurrentSub;

    private Dictionary<string, AndroidStorage.ServerTestResultDto> _srvResults = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _srvTestingKeys = new(StringComparer.OrdinalIgnoreCase);

    private bool _srvSortByLatency = false;

    private CancellationTokenSource? _srvTestAllCts;

    // Coalesces a burst of probe results into one list rebuild (avoids O(N^2) rebuilds); Interlocked because up to 4 probes complete concurrently.
    private int _srvRebuildScheduled;

    private Control BuildServersTabContent()
    {
        _srvSubTabRow = BuildServersSubTabBar();

        _srvServersSubPanel = BuildServersListSubPanel();

        _srvCustomJsonSubPanel = BuildCustomJsonSubPanel();

        _srvFooterActionsRow = BuildServersFooterActions();

        var contentHost = new Grid();
        contentHost.Children.Add(_srvServersSubPanel);
        contentHost.Children.Add(_srvCustomJsonSubPanel);

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_srvSubTabRow, Dock.Top);
        DockPanel.SetDock(_srvFooterActionsRow, Dock.Bottom);
        dock.Children.Add(_srvSubTabRow);
        dock.Children.Add(_srvFooterActionsRow);
        dock.Children.Add(contentHost);

        ApplyServersSubTabVisuals();

        return new Border
        {
            Background = GetBrush("SurfaceAppBrush"),
            Child = dock,
        };
    }

    private StackPanel BuildServersSubTabBar()
    {
        _srvSubTabServersBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvServersSubTabServers,
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 4),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            BorderThickness = new Thickness(1),
        };
        _srvSubTabServersBtn.Click += (_, _) => SetServersSubTab("servers");

        _srvSubTabCustomJsonBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvServersSubTabCustomJson,
            FontSize = UiScale.Fs(11),
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

    private DockPanel BuildServersListSubPanel()
    {
        _srvTitle = new TextBlock
        {
            Text = string.Empty,
            FontSize = UiScale.Fs(12),
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 4, 12, 0),
        };

        _srvSortToggle = new Avalonia.Controls.Button
        {
            Content = Localization.SrvSortByOriginal,
            FontSize = UiScale.Fs(10),
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
            FontSize = UiScale.Fs(10),
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

        _srvColServer = new TextBlock
        {
            Text = Localization.ColServer,
            FontSize = UiScale.Fs(9),
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _srvColPing = new TextBlock
        {
            Text = Localization.ColPing,
            FontSize = UiScale.Fs(9),
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(_srvColPing, Localization.ColPingTooltip);
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("14,*,Auto,40"),
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

        _srvListStack = new StackPanel
        {
            Spacing = 0,
        };
        _srvEmptyHint = new TextBlock
        {
            Text = Localization.SrvEmptyHint,
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
            Children = { _srvListStack, _srvEmptyHint },
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
        DockPanel.SetDock(_srvTitle, Dock.Top);
        DockPanel.SetDock(sortRow, Dock.Top);
        DockPanel.SetDock(headerHost, Dock.Top);
        dock.Children.Add(_srvTitle);
        dock.Children.Add(sortRow);
        dock.Children.Add(headerHost);
        dock.Children.Add(listCard);
        return dock;
    }

    private StackPanel BuildCustomJsonSubPanel()
    {
        _srvCustomJsonExplainer = new TextBlock
        {
            Text = Localization.AdvServersCustomJsonExplainer,
            FontSize = UiScale.Fs(11),
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = UiScale.Lh(16),
            Margin = new Thickness(0, 0, 0, 8),
        };

        _srvCustomJsonInput = new TextBox
        {
            Watermark = Localization.CcCustomWatermark,
            FontFamily = new FontFamily("monospace"),
            FontSize = UiScale.Fs(10),
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
            FontSize = UiScale.Fs(11),
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
            FontSize = UiScale.Fs(11),
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
            FontSize = UiScale.Fs(11),
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
            FontSize = UiScale.Fs(10),
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            IsVisible = false,
        };
        var customJsonStatusRow = WithStatusIcon(_srvCustomJsonStatus, out var customJsonStatusIcon);
        _srvCustomJsonStatusIcon = customJsonStatusIcon;

        var stack = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(12, 4, 12, 12),
            Children = { _srvCustomJsonExplainer, _srvCustomJsonInput, btnRow, customJsonStatusRow },
        };
        return stack;
    }

    private Border BuildServersFooterActions()
    {
        _srvTestAllBtn = new Avalonia.Controls.Button
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
        ToolTip.SetTip(_srvTestAllBtn, Localization.SrvTipTestAll);
        _srvTestAllBtn.Click += async (_, _) => await OnSrvTestAllClicked();

        _srvDeepVerifyBtn = new Avalonia.Controls.Button
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
        ToolTip.SetTip(_srvDeepVerifyBtn, Localization.AdvServersDeepVerifyAndroidNote);
        _srvDeepVerifyBtn.Click += async (_, _) => await OnSrvDeepVerifyClicked();

        _srvVlessUriInput = new TextBox
        {
            Watermark = Localization.WmVlessUri,
            FontFamily = new FontFamily("monospace"),
            FontSize = UiScale.Fs(10),
            Padding = new Thickness(6, 4),
            AcceptsReturn = false,
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
        };

        _srvRemoveBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvServersRemove,
            FontSize = UiScale.Fs(11),
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
            Content = IconText(UiIcons.Plus, Localization.AdvServersAddServers, 14),
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(14, 5),
            Background = GetBrush("AccentSolidBrush"),
            Foreground = GetBrush("AccentOnSolidBrush"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        };
        _srvAddBtn.Click += (_, _) => OnSrvAddClicked();

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

    private void SetServersSubTab(string sub)
    {
        if (sub != "servers" && sub != "custom") return;
        if (_srvSubTab == sub) return;
        _srvSubTab = sub;
        ApplyServersSubTabVisuals();
        if (sub == "custom") ReseedCustomJsonSubPanel();
    }

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

    private void OnSrvCustomJsonValidateClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_srvCustomJsonInput is null || _srvCustomJsonStatus is null) return;
        var raw = (_srvCustomJsonInput.Text ?? string.Empty).Trim();
        _srvCustomJsonStatus.IsVisible = true;

        if (string.IsNullOrEmpty(raw))
        {
            _srvCustomJsonStatus.Text = Localization.CcSaveStatusEmpty;
            _srvCustomJsonStatus.Foreground = GetBrush("DangerFgBrush");
            SetStatusIcon(_srvCustomJsonStatusIcon, UiIcons.CircleX);
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
                SetStatusIcon(_srvCustomJsonStatusIcon, UiIcons.CircleX);
                return;
            }
            var (protocols, server) = VPNRouter.Core.Services.CustomConfigInjector.ParseConfigInfo(raw);
            _srvCustomJsonStatus.Text = string.Format(Localization.CcValidationOk, protocols, server);
            _srvCustomJsonStatus.Foreground = GetBrush("SuccessFgBrush");
            SetStatusIcon(_srvCustomJsonStatusIcon, UiIcons.CircleCheck);
        }
        catch (Exception ex)
        {
            _srvCustomJsonStatus.Text = string.Format(Localization.CcValidationParseError, ex.Message);
            _srvCustomJsonStatus.Foreground = GetBrush("DangerFgBrush");
            SetStatusIcon(_srvCustomJsonStatusIcon, UiIcons.CircleX);
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
            SetStatusIcon(_srvCustomJsonStatusIcon, UiIcons.CircleX);
            return;
        }

        var (isValid, errors) = VPNRouter.Core.Services.CustomConfigInjector.Validate(raw);
        AndroidStorage.SetCustomConfigJson(raw);
        AndroidStorage.SetConfigMode("custom");
        _ccMode = "custom";
        UpdateConfigSummary();

        if (!isValid)
        {
            _srvCustomJsonStatus.Text = string.Format(
                Localization.CcSaveStatusInvalid + " ({0})",
                string.Join("; ", errors));
            _srvCustomJsonStatus.Foreground = GetBrush("WarningFgBrush");
            SetStatusIcon(_srvCustomJsonStatusIcon, UiIcons.CircleAlert);
            return;
        }
        _srvCustomJsonStatus.Text = Localization.CcSaveStatusOk;
        _srvCustomJsonStatus.Foreground = GetBrush("SuccessFgBrush");
        SetStatusIcon(_srvCustomJsonStatusIcon, UiIcons.CircleCheck);
    }

    private void OnSrvCustomJsonClearClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_srvCustomJsonInput is not null) _srvCustomJsonInput.Text = string.Empty;
        if (_srvCustomJsonStatus is not null) _srvCustomJsonStatus.IsVisible = false;
        AndroidStorage.SetCustomConfigJson(null);
        UpdateConfigSummary();
    }

    private async Task OnSrvDeepVerifyClicked() => await OnSrvTestAllClicked();

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

        AndroidStorage.SetSelectedServerName(null);
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

    public void OpenServerListOverlay(SubscriptionEntry sub)
    {
        _srvCurrentSub = sub;
        _srvResults = AndroidStorage.GetServerTestResults();
        _srvTestingKeys.Clear();
        _srvSortByLatency = false;
        OpenAdvancedShell(AdvancedTab.Servers);
    }

    private void ReseedServersTabState()
    {
        if (_srvCurrentSub is null)
        {
            var subs = AndroidStorage.GetSubscriptions();
            _srvCurrentSub = subs.FirstOrDefault(s =>
                string.Equals(s.Name, "Manual", StringComparison.OrdinalIgnoreCase))
                ?? new SubscriptionEntry
                {
                    Name = "Manual",
                    Url = string.Empty,
                    Enabled = true,
                    Servers = new List<VlessServerEntry>(),
                };
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
        if (_srvVlessUriInput is not null) _srvVlessUriInput.Text = string.Empty;
        ReseedCustomJsonSubPanel();
        RebuildServerList();
    }

    private void StopServersTabBackgroundWork()
    {
        try { _srvTestAllCts?.Cancel(); } catch { }
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
        global::Android.Util.Log.Info("VpnRouter.Perf", "server list rebuilt rows=" + ordered.Count);
        foreach (var srv in ordered)
        {
            _srvListStack.Children.Add(BuildServerRow(srv, activeName));
        }
        UpdateRemoveButtonEnabled(servers, activeName);
    }

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

    private Control BuildServerRow(VlessServerEntry srv, string? activeServerName)
    {
        var key = AndroidStorage.BuildServerKey(srv);
        var hasResult = _srvResults.TryGetValue(key, out var result);
        return BuildServerRowCore(srv, activeServerName, hasResult ? result : null,
            _srvTestingKeys.Contains(key), TestSingleServerAsync);
    }

    private Control BuildServerRowCore(
        VlessServerEntry srv,
        string? activeServerName,
        AndroidStorage.ServerTestResultDto? result,
        bool isTesting,
        Func<VlessServerEntry, Task> onTest)
    {
        var isActive = !string.IsNullOrEmpty(activeServerName)
                       && string.Equals(srv.Name, activeServerName, StringComparison.OrdinalIgnoreCase);

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
        radioOuter.PointerReleased += (s, e) => ApplyServerSelection(srv);
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

        var displayName = string.IsNullOrWhiteSpace(srv.Name) ? srv.Server : srv.Name;

        var nameText = new TextBlock
        {
            Text = displayName,
            FontSize = UiScale.Fs(11),
            FontWeight = isActive ? FontWeight.Bold : FontWeight.SemiBold,
            Foreground = isActive ? GetBrush("AccentFgBrush") : GetBrush("TextPrimaryBrush"),
            FontFamily = new FontFamily("monospace"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var metaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(srv.Server)) metaParts.Add(srv.Server!);
        if (srv.Port > 0) metaParts.Add(":" + srv.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var hostSubtitle = BuildHostSubtitle(srv);
        if (!string.IsNullOrEmpty(hostSubtitle)) metaParts.Add(hostSubtitle);
        var hostText = new TextBlock
        {
            Text = string.Join(" · ", metaParts),
            FontSize = UiScale.Fs(9),
            Foreground = GetBrush("TextMutedBrush"),
            FontFamily = new FontFamily("monospace"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        hostText.IsVisible = !string.IsNullOrEmpty(hostText.Text);
        var nameStack = new Border
        {
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { nameText, hostText },
            },
        };
        ToolTip.SetTip(nameStack, Localization.SrvTipSelectServer);
        nameStack.PointerReleased += (s, e) => ApplyServerSelection(srv);

        var (pingDisplay, _) = ResolveLatencyDisplay(result, isTesting);
        var pingBgBrush = ResolveLatencyBadgeBackground(result, isTesting);
        var pingHasData = !isTesting && result is not null;
        var pingTextInside = new TextBlock
        {
            Text = pingDisplay,
            FontSize = UiScale.Fs(9),
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
        pingBadge.PointerReleased += (s, e) => ApplyServerSelection(srv);
        if (!string.IsNullOrEmpty(result?.Error))
            ToolTip.SetTip(pingBadge, result.Error);

        var testBtn = new Avalonia.Controls.Button
        {
            Content = IconContent(UiIcons.RefreshCw, 18),
            Width = 40,
            Height = 40,
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
        testBtn.Click += async (s, e) => await onTest(srv);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("14,*,Auto,40"),
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

    private void ApplyServerSelection(VlessServerEntry srv)
    {
        AndroidStorage.SetSelectedServerName(srv.Name);

        var activity = MainActivity.Instance;
        if (activity is not null && MainActivity.IntendedConnected)
        {
            ShowMenuFeedback(string.Format(Localization.SrvSwitchedReconnect, srv.Name));
            activity.RequestConnect();
        }
        else
        {
            ShowMenuFeedback(string.Format(Localization.SrvSelectedActive, srv.Name));
        }

        try { RebuildServerList(); } catch { }
        try { ScheduleAggregatedServerListRebuild(); } catch { }
        UpdateConfigSummary();
    }

    private async Task OnSrvTestAllClicked()
    {
        var sub = _srvCurrentSub;
        if (sub is null || sub.Servers.Count == 0) return;
        if (_srvTestAllCts is not null)
        {
            try { _srvTestAllCts.Cancel(); } catch { }
            return;
        }

        _srvTestAllCts = new CancellationTokenSource();
        var ct = _srvTestAllCts.Token;

        try
        {
            var servers = sub.Servers.ToList();
            var total = servers.Count;
            var done = 0;

            foreach (var srv in servers)
            {
                _srvTestingKeys.Add(AndroidStorage.BuildServerKey(srv));
            }
            RebuildServerList();
            UpdateProgressText(0, total);
            if (_srvTestAllBtn is not null)
                _srvTestAllBtn.Content = Localization.SrvTesting;

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
                }
                catch
                {
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
            _srvTestingKeys.Clear();
            AndroidStorage.SetServerTestResults(_srvResults);
            try { _srvTestAllCts?.Dispose(); } catch { }
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
        ScheduleServerListRebuild();
    }

    private void ScheduleServerListRebuild()
    {
        if (Interlocked.CompareExchange(ref _srvRebuildScheduled, 1, 0) != 0)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _srvRebuildScheduled, 0);
            RebuildServerList();
        }, DispatcherPriority.Background);
    }

    private void UpdateProgressText(int done, int total)
    {
        if (_srvStatusText is null) return;
        _srvStatusText.Text = string.Format(Localization.SrvProgressFmt, done, total);
    }

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
        if (_srvColPing is not null)
        {
            _srvColPing.Text = Localization.ColPing;
            ToolTip.SetTip(_srvColPing, Localization.ColPingTooltip);
        }

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
        if (_srvAddBtn is not null) _srvAddBtn.Content = IconText(UiIcons.Plus, Localization.AdvServersAddServers, 14);
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

        if (_srvListStack is not null) RebuildServerList();
    }
}
