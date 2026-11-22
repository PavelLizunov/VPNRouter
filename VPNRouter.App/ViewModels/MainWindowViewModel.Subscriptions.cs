#nullable enable
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private string SubscriptionDisabledMessage => IsRussian
        ? "Подписка выключена. Включите её в разделе «Подписки»."
        : "This subscription is disabled. Enable it in Subscriptions.";

    private SubscriptionViewModel CreateSubscriptionView(SubscriptionEntry entry) => new(entry, sub =>
    {
        if (_isLoadingUI || !Subscriptions.Contains(sub)) return;
        OnPropertyChanged(nameof(SmpHasRefreshableSubscriptions));
        NotifySubscriptionRefreshState();
        var replaceInput = !sub.Enabled && SimpleConnectPolicy.SameUrl(SmpInput, sub.Url);
        RebuildSubscriptionPool();
        if (replaceInput)
            SmpInput = Subscriptions.FirstOrDefault(s => s.Id == SelectedSubscriptionServer?.SubscriptionId)?.Url
                ?? (SimpleServerPreference == null ? Subscriptions.FirstOrDefault(s => s.Enabled)?.Url : null)
                ?? string.Empty;
        SaveSettings();
        if (IsConnected) HasPendingAppChanges = true;
    });

    private (string? Feed, string Route)? SelectedSubscriptionRoute() => SelectedSubscriptionServer is { } row
        ? (row.SubscriptionId, SubscriptionEntry.RouteKey(row.SourceEntry,
            SubscriptionServers.Where(s => s.SubscriptionId == row.SubscriptionId).Select(s => s.SourceEntry))) : null;

    private void RebuildSubscriptionPool()
    {
        var prevLoading = _isLoadingUI;
        _isLoadingUI = true;
        try
        {
            var previous = SelectedSubscriptionServer;
            var id = previous?.SubscriptionId ?? _settings.App.ActiveSubscriptionId;
            var name = previous?.Name ?? _settings.App.ActiveSubscriptionServer;
            var key = previous == null ? _settings.App.ActiveSubscriptionRow : SubscriptionEntry.RowKey(previous.SourceEntry);
            var enabled = Subscriptions.Where(s => s.Enabled).ToList();
            var source = enabled.FirstOrDefault(s => s.Id == id);
            var entries = enabled.SelectMany(s => s.UnderlyingEntry.Servers);
            var selected = SubscriptionRefreshDiff.FindRegisteredServer(source?.UnderlyingEntry, entries, name, key,
                previous?.SourceEntry, SelectedSubscriptionRoute()?.Route);
            if (SimpleServerPreference == null && selected == null && source != null &&
                (previous?.SourceEntry.Dnstt != null || !string.IsNullOrEmpty(previous?.SourceEntry.DetourVia)))
            {
                foreach (var row in SubscriptionServers.Where(row => enabled.All(feed => feed.Id != row.SubscriptionId)).ToArray())
                    SubscriptionServers.Remove(row);
                RefreshActiveIndicator();
                return;
            }

            SubscriptionServers.Clear();
            foreach (var feed in enabled)
                foreach (var entry in feed.UnderlyingEntry.Servers)
                    SubscriptionServers.Add(new ServerViewModel(entry) { SubscriptionId = feed.Id });
            ServerViewModel.RefreshUdpSiblingFlags(SubscriptionServers);
            ServerViewModel.RefreshProviderRiskFlags(SubscriptionServers);

            if (SimpleServerPreference is { } preference)
            {
                var chosen = preference.Preferred(_settings.App, enabled.Select(feed => feed.UnderlyingEntry));
                SelectedSubscriptionServer = chosen is { } registered
                    ? SubscriptionServers.FirstOrDefault(row => row.SubscriptionId == registered.Feed.Id &&
                        ReferenceEquals(row.SourceEntry, registered.Server))
                    : null;
            }
            else
            {
                SelectedSubscriptionServer = SubscriptionServers.FirstOrDefault(row =>
                    (source == null || row.SubscriptionId == source.Id) && ReferenceEquals(row.SourceEntry, selected))
                    ?? SubscriptionServers.FirstOrDefault();
            }

            RefreshActiveIndicator();
        }
        finally
        {
            _isLoadingUI = prevLoading;
        }
    }

    [RelayCommand]
    private async Task AddSubscriptionAsync()
    {
        var url = (NewSubUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url)) return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            StatusText = Strings.SubscriptionEnterUrl;
            return;
        }

        var name = (NewSubName ?? "").Trim();
        if (string.IsNullOrEmpty(name)) name = $"Sub {Subscriptions.Count + 1}";

        if (Subscriptions.Any(s => !s.Enabled && SimpleConnectPolicy.SameUrl(s.Url, url))
            && !Subscriptions.Any(s => s.Enabled && SimpleConnectPolicy.SameUrl(s.Url, url)))
        {
            StatusText = SubscriptionDisabledMessage;
            return;
        }
        var entry = new SubscriptionEntry { Name = name, Url = url, Enabled = true };
        _settings.App.Subscriptions.Add(entry);
        var svm = CreateSubscriptionView(entry);
        Subscriptions.Add(svm);

        NewSubName = string.Empty;
        NewSubUrl = string.Empty;

        if (!IsSubscribeMode)
        {
            IsSubscribeMode = true;
            IsVlessMode = false;
            SelectedTabIndex = 1;
        }

        await RefreshSubscriptionAsync(svm);

        RebuildSubscriptionPool();
        SaveSettings();
    }

    [RelayCommand]
    private void RemoveSubscription(SubscriptionViewModel? sub)
    {
        if (sub == null) return;
        bool removedInput = IsSubscribeMode && SimpleConnectPolicy.SameUrl(SmpInput, sub.Url);
        Subscriptions.Remove(sub);
        _settings.App.Subscriptions.RemoveAll(e => e.Id == sub.Id);
        RebuildSubscriptionPool();
        if (removedInput)
        {
            var selected = Subscriptions.FirstOrDefault(s => s.Id == SelectedSubscriptionServer?.SubscriptionId);
            SmpInput = selected?.Url ??
                (SimpleServerPreference == null ? Subscriptions.FirstOrDefault(s => s.Enabled)?.Url : null) ?? string.Empty;
        }
        SaveSettings();
    }

    [RelayCommand]
    private async Task RefreshSubscriptionAsync(SubscriptionViewModel? sub)
    {
        if (sub == null || !sub.Enabled || string.IsNullOrWhiteSpace(sub.Url)) return;
        if (sub.IsRefreshing) return;

        await RefreshRegisteredSubscriptionsAsync([sub], CancellationToken.None);
    }

    [RelayCommand]
    private async Task RefreshAllSubscriptionsAsync()
    {
        var enabled = Subscriptions.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (enabled.Count > 0) await RefreshRegisteredSubscriptionsAsync(enabled, CancellationToken.None);
    }

    internal Func<string, Task>? SubscriptionRefreshReconnect { get; set; }

    private int _subscriptionRefreshApplyGeneration;

    partial void OnIsApplyingChanged(bool value)
    {
        if (value) _subscriptionRefreshApplyGeneration++;
    }

    private void NotifySubscriptionRefreshState()
    {
        OnPropertyChanged(nameof(SmpSubscriptionsRefreshing));
        OnPropertyChanged(nameof(SmpCanConnect));
    }

    private async Task RefreshRegisteredSubscriptionsAsync(List<SubscriptionViewModel> targets, CancellationToken ct)
    {
        if (_disposed || ct.IsCancellationRequested || !SmpCanRefreshSubscriptions || _isReconnecting) return;
        targets.RemoveAll(row => row.IsRefreshing);
        if (targets.Count == 0) return;

        var previous = SelectedSubscriptionServer;
        var previousRoute = SelectedSubscriptionRoute();
        var generation = _connectionGeneration;
        var applyGeneration = _subscriptionRefreshApplyGeneration;
        foreach (var row in targets) row.IsRefreshing = true;
        NotifySubscriptionRefreshState();
        try
        {
            var updates = await FetchSubscriptionRowsAsync(targets, ct);
            if (_disposed || ct.IsCancellationRequested || !SmpCanRefreshSubscriptions || _isReconnecting ||
                generation != _connectionGeneration || applyGeneration != _subscriptionRefreshApplyGeneration) return;
            if (!PublishSubscriptionRows(targets, updates)) return;
        }
        finally
        {
            foreach (var row in targets) row.IsRefreshing = false;
            NotifySubscriptionRefreshState();
        }

        var sameSelection = ReferenceEquals(previous, SelectedSubscriptionServer);
        var shown = SubscriptionServers.Select(row => (row.SubscriptionId, SubscriptionEntry.RowKey(row.SourceEntry)));
        var refreshed = Subscriptions.Where(s => s.Enabled).SelectMany(s =>
            s.UnderlyingEntry.Servers.Select(row => ((string?)s.Id, SubscriptionEntry.RowKey(row))));
        if (!shown.SequenceEqual(refreshed)) RebuildSubscriptionPool();
        SaveSettings();

        if (!sameSelection || previousRoute == null || previousRoute == SelectedSubscriptionRoute())
        {
            RefreshActiveIndicator();
            OnPropertyChanged(nameof(SimpleStatusDescription));
            OnPropertyChanged(nameof(SimpleActiveOutboundLine));
            return;
        }
        if (generation != _connectionGeneration || !IsConnected || !IsSubscribeMode || IsConnecting || IsApplying || _isReconnecting)
            return;

        var name = SelectedSubscriptionServer?.Name ?? "subscription";
        if (SubscriptionRefreshReconnect is { } reconnect) await reconnect(name);
        else await ReconnectAsync(name, ReconnectIntent.Subscription);
    }

    private Task<SubscriptionCacheUpdate[]> FetchSubscriptionRowsAsync(List<SubscriptionViewModel> rows, CancellationToken ct) =>
        Task.WhenAll(rows.Select(s => SubscriptionCacheUpdate.FetchAsync(s.ToEntry(), _logger, ct)));

    private bool PublishSubscriptionRows(List<SubscriptionViewModel> rows, SubscriptionCacheUpdate[] updates,
        AppSettings? submission = null)
    {
        var feeds = submission?.App.Subscriptions ?? Subscriptions.Select(s => s.ToEntry()).ToList();
        var app = submission?.App ?? _settings.App;
        var preference = SubscriptionServerPreference.For(app);
        var chain = submission != null || preference != null ? SubscriptionChainSelection.Capture(app, feeds) : null;
        if (submission == null && preference == null && SelectedSubscriptionServer is { } row &&
            (row.SourceEntry.Dnstt != null || !string.IsNullOrEmpty(row.SourceEntry.DetourVia)) &&
            feeds.FirstOrDefault(s => s.Id == row.SubscriptionId) is { } source)
            chain = new(source.Id, source.Url, row.SourceEntry, SelectedSubscriptionRoute()?.Route);

        var accepted = SubscriptionCacheUpdate.TryPublish(updates, feeds, chain, out var successor);
        if (accepted && successor != null) chain?.RebindPin(app, feeds, successor);
        foreach (var (view, update) in rows.Zip(updates))
        {
            view.LastRefreshFailed = !accepted || update.Failed;
            if (!accepted) continue;
            view.LastRefreshedAt = update.Origin.LastRefreshedAt;
            view.LastServerCount = update.Origin.Servers.Count;
        }
        return accepted;
    }

    [RelayCommand]
    private async Task SyncSubscriptionAsync()
    {
        if (string.IsNullOrWhiteSpace(SubscriptionUrl))
        {
            StatusText = Strings.SubscriptionEnterUrl;
            return;
        }

        StatusText = Strings.Syncing;
        try
        {
            var url = SubscriptionUrl;
            var registered = Subscriptions.FirstOrDefault(s => s.Enabled && SimpleConnectPolicy.SameUrl(s.Url, url));
            if (registered != null)
            {
                await RefreshSubscriptionAsync(registered);
                StatusText = registered.LastRefreshFailed ? Strings.SyncEmpty : Strings.SyncComplete(registered.CachedServerCount);
                return;
            }
            if (Subscriptions.Any(s => !s.Enabled && SimpleConnectPolicy.SameUrl(s.Url, url)))
            {
                StatusText = SubscriptionDisabledMessage;
                return;
            }
            var selectionAtStart = SelectedSubscriptionServer;
            var entries = await SubscriptionFetcher.FetchAsync(url, _logger);
            if (_disposed || !SimpleConnectPolicy.SameUrl(url, SubscriptionUrl) ||
                !ReferenceEquals(selectionAtStart, SelectedSubscriptionServer)) return;

            if (entries.Count == 0)
            {
                StatusText = Strings.SyncEmpty;
                return;
            }

            if (SelectedSubscriptionServer is { } selected && !string.IsNullOrEmpty(selected.SourceEntry.DetourVia) &&
                Subscriptions.FirstOrDefault(s => s.Id == selected.SubscriptionId) is { } owner)
            {
                var chain = new SubscriptionChainSelection(owner.Id, owner.Url, selected.SourceEntry, SelectedSubscriptionRoute()?.Route);
                var pool = Subscriptions.Where(s => s.Enabled).SelectMany(s => s.UnderlyingEntry.Servers).Concat(entries);
                if (chain.Match(owner.UnderlyingEntry, pool) == null)
                {
                    StatusText = Strings.SyncEmpty;
                    return;
                }
                Subscriptions.Add(CreateSubscriptionView(new SubscriptionEntry { Url = url, Name = "Default", Servers = entries }));
                RebuildSubscriptionPool();
                SaveSettings();
                StatusText = Strings.SyncComplete(entries.Count);
                return;
            }
            SubscriptionServers.Clear();
            foreach (var entry in entries)
                SubscriptionServers.Add(new ServerViewModel(entry));
            ServerViewModel.RefreshUdpSiblingFlags(SubscriptionServers);
            ServerViewModel.RefreshProviderRiskFlags(SubscriptionServers);

            SelectedSubscriptionServer = SubscriptionServers.FirstOrDefault();
            SaveSettings();
            StatusText = Strings.SyncComplete(entries.Count);
        }
        catch (Exception ex)
        {
            _logger.Error("[VM] Subscription sync failed ({ExceptionType})", ex.GetType().Name);
            StatusText = Strings.SyncFailed(CrashReporter.ScrubSecrets(ex.Message));
        }
    }

    [RelayCommand]
    private void ClearSubscription()
    {
        SubscriptionServers.Clear();
        SubscriptionUrl = string.Empty;
        SelectedSubscriptionServer = null;
        SaveSettings();
        StatusText = Strings.SubscriptionCleared;
    }

    private void StartSubRefreshTimer()
    {
        StopSubRefreshTimer();
        if (!(_settings.App.ConfigMode ?? "generated")
            .Equals("subscribe", StringComparison.OrdinalIgnoreCase)) return;
        var hasLegacyUrl = !string.IsNullOrWhiteSpace(SubscriptionUrl);
        var hasEnabledMultiSub = Subscriptions.Any(s =>
            s.Enabled && !string.IsNullOrWhiteSpace(s.Url));
        if (!hasLegacyUrl && !hasEnabledMultiSub) return;

        _logger.Information("[SubRefresh] Starting timer (interval: {Sec}s)", SubRefreshIntervalMs / 1000);
        _subRefreshTimer = new System.Threading.Timer(
            _ => Dispatcher.UIThread.Post(async () => await RefreshSubscriptionSilentAsync()),
            null,
            SubRefreshIntervalMs,
            SubRefreshIntervalMs);
    }

    private void StopSubRefreshTimer()
    {
        _subRefreshTimer?.Dispose();
        _subRefreshTimer = null;
    }

    internal async Task RefreshSubscriptionSilentAsync()
    {
        if (!IsConnected || _subRefreshCts != null || !SmpCanRefreshSubscriptions || !(_settings.App.ConfigMode ?? "generated")
            .Equals("subscribe", StringComparison.OrdinalIgnoreCase)) return;

        var enabled = Subscriptions.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (enabled.Count == 0) return;

        using var refresh = new CancellationTokenSource();
        _subRefreshCts = refresh;
        var ct = refresh.Token;

        try
        {
            _logger.Information("[SubRefresh] Checking {Count} subscription(s)...", enabled.Count);

            await RefreshRegisteredSubscriptionsAsync(enabled, ct);
        }
        catch (Exception)
        {
            _logger.Error("[SubRefresh] Auto-refresh failed");
        }
        finally
        {
            if (ReferenceEquals(_subRefreshCts, refresh)) _subRefreshCts = null;
        }
    }
}
