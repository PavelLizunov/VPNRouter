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
    private Border? _subsOverlay;
    private StackPanel? _subsListStack;
    private TextBlock? _subsEmptyHint;
    private TextBox? _subsNewName;
    private TextBox? _subsNewUrl;
    private Avalonia.Controls.Button? _subsAddBtn;
    private Avalonia.Controls.Button? _subsRefreshAllBtn;
    private TextBlock? _subsTitle;
    private TextBlock? _subsSectionLabel;
    private TextBlock? _subsRefreshAllStatus;
    private Avalonia.Controls.Button? _subsCloseBtn;

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
    /// Build the fullscreen Subscribe overlay (mirrors
    /// <see cref="BuildAppPickerOverlay"/> structure: title bar with × +
    /// scrollable content + bottom bar).
    /// </summary>
    private Border BuildSubsOverlay()
    {
        _subsTitle = new TextBlock
        {
            Text = Localization.SubscriptionsSection,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _subsCloseBtn = new Avalonia.Controls.Button
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
        _subsCloseBtn.Click += OnSubsCloseClicked;

        var titleBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(8, 4, 4, 4),
        };
        Grid.SetColumn(_subsTitle, 0);
        Grid.SetColumn(_subsCloseBtn, 1);
        titleBar.Children.Add(_subsTitle);
        titleBar.Children.Add(_subsCloseBtn);

        var titleBarBorder = new Border
        {
            Background = GetBrush("SurfaceRaisedBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4),
            Child = titleBar,
        };

        // Card list (one Border per SubscriptionEntry, or empty hint).
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
            Margin = new Thickness(0, 24, 0, 0),
            IsVisible = false,
        };

        var listRoot = new StackPanel
        {
            Spacing = 0,
            Children = { _subsListStack, _subsEmptyHint },
        };

        var listScroller = new ScrollViewer
        {
            Content = listRoot,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = GetBrush("SurfaceAppBrush"),
        };

        // ── Section header bar — "Subscriptions" + "Refresh all" on right.
        //     Mirrors desktop SubscribePage.axaml lines 251-260 (Row 2).
        //     Sits ABOVE the card list with a top divider so the heading
        //     reads as the cards' label, not the form's.
        _subsSectionLabel = new TextBlock
        {
            Text = Localization.SubscriptionsSection,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = GetBrush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _subsRefreshAllBtn = new Avalonia.Controls.Button
        {
            Content = Localization.RefreshAll,
            FontSize = 10,
            Padding = new Thickness(8, 3),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceRaisedBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        _subsRefreshAllBtn.Click += OnSubsRefreshAllClicked;
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
        var sectionHeaderGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        Grid.SetColumn(_subsSectionLabel, 0);
        Grid.SetColumn(_subsRefreshAllBtn, 1);
        sectionHeaderGrid.Children.Add(_subsSectionLabel);
        sectionHeaderGrid.Children.Add(_subsRefreshAllBtn);

        var sectionHeaderBorder = new Border
        {
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = GetBrush("SurfaceBaseBrush"),
            Padding = new Thickness(10, 6, 10, 6),
            Child = new StackPanel
            {
                Spacing = 0,
                Children = { sectionHeaderGrid, _subsRefreshAllStatus },
            },
        };

        // ── Add-subscription form (bottom) ─────────────────────────────
        //     Mirrors desktop SubscribePage.axaml lines 338-361 (Row 4):
        //     "100,*,Auto" name + URL + Add button with top divider.
        _subsNewName = new TextBox
        {
            Watermark = Localization.SubscriptionNameHint,
            FontSize = 10,
            Padding = new Thickness(6, 4),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
        };
        _subsNewUrl = new TextBox
        {
            Watermark = Localization.SubscriptionUrlHint,
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

        // Body = section-header (top) + scrolling card list (fill).
        // DockPanel needs the fill child to be added LAST.
        var bodyDock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(sectionHeaderBorder, Dock.Top);
        bodyDock.Children.Add(sectionHeaderBorder);
        bodyDock.Children.Add(listScroller);

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleBarBorder, Dock.Top);
        DockPanel.SetDock(addFormBorder, Dock.Bottom);
        dock.Children.Add(titleBarBorder);
        dock.Children.Add(addFormBorder);
        dock.Children.Add(bodyDock);

        return new Border
        {
            Background = GetBrush("SurfaceAppBrush"),
            IsVisible = false,
            Child = dock,
        };
    }

    /// <summary>
    /// Public-ish entry point: open the overlay and rebuild the card
    /// list from the latest persisted subscriptions. Triggered by the
    /// SimplePage advanced-card tap.
    /// </summary>
    private void OpenSubsOverlay()
    {
        if (_subsOverlay is null) return;
        _subs = AndroidStorage.GetSubscriptions();
        _refreshingIds.Clear();
        _pendingDeleteId = null;
        _editingId = null;
        RebuildSubsList();
        _subsOverlay.IsVisible = true;
    }

    private void OnSubsCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_subsOverlay is null) return;
        _subsOverlay.IsVisible = false;
        // After closing, refresh the SimplePage server-list reflection
        // so any newly fetched servers appear in the inline ListBox + the
        // CTA button summary stays accurate.
        ReloadServerList();
        UpdateConfigSummary();
    }

    private void RebuildSubsList()
    {
        if (_subsListStack is null || _subsEmptyHint is null) return;
        _subsListStack.Children.Clear();

        if (_subs.Count == 0)
        {
            _subsEmptyHint.IsVisible = true;
            return;
        }
        _subsEmptyHint.IsVisible = false;

        foreach (var sub in _subs)
        {
            _subsListStack.Children.Add(BuildSubCard(sub));
        }
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
    /// </summary>
    private Avalonia.Controls.Button StyledRowActionButton(string glyph, string? tooltip)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = glyph,
            FontSize = 14,
            Padding = new Thickness(6, 2),
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
        if (_subsTitle is not null) _subsTitle.Text = Localization.SubscriptionsSection;
        if (_subsSectionLabel is not null) _subsSectionLabel.Text = Localization.SubscriptionsSection;
        if (_subsRefreshAllBtn is not null) _subsRefreshAllBtn.Content = Localization.RefreshAll;
        if (_subsAddBtn is not null) _subsAddBtn.Content = Localization.AddSubscription;
        if (_subsNewName is not null) _subsNewName.Watermark = Localization.SubscriptionNameHint;
        if (_subsNewUrl is not null) _subsNewUrl.Watermark = Localization.SubscriptionUrlHint;
        if (_subsEmptyHint is not null) _subsEmptyHint.Text = Localization.LblAddSubscriptionHint;
        // Card list: cheapest path is full rebuild — strings are per-card
        // (Refreshing… spinner, refresh/delete tooltips, formatted
        // timestamp uses "никогда"/"never"). Skip if overlay is hidden.
        if (_subsOverlay?.IsVisible == true) RebuildSubsList();
    }
}
