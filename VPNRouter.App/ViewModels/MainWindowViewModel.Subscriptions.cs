#nullable enable
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
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

            SubscriptionServers.Clear();
            foreach (var feed in enabled)
                foreach (var entry in feed.UnderlyingEntry.Servers)
                    SubscriptionServers.Add(new ServerViewModel(entry) { SubscriptionId = feed.Id });
            ServerViewModel.RefreshUdpSiblingFlags(SubscriptionServers);
            ServerViewModel.RefreshProviderRiskFlags(SubscriptionServers);

            SelectedSubscriptionServer = SubscriptionServers.FirstOrDefault(row =>
                (source == null || row.SubscriptionId == source.Id) && ReferenceEquals(row.SourceEntry, selected))
                ?? SubscriptionServers.FirstOrDefault();

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

        var entry = new SubscriptionEntry { Name = name, Url = url, Enabled = true };
        _settings.App.Subscriptions.Add(entry);
        var svm = new SubscriptionViewModel(entry);
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
            SmpInput = selected?.Url ?? Subscriptions.FirstOrDefault(s => s.Enabled)?.Url ?? string.Empty;
        }
        SaveSettings();
    }

    [RelayCommand]
    private async Task RefreshSubscriptionAsync(SubscriptionViewModel? sub)
    {
        if (sub == null || string.IsNullOrWhiteSpace(sub.Url)) return;
        if (sub.IsRefreshing) return;

        var activeSigBefore = SelectedSubscriptionRoute();

        sub.IsRefreshing = true;
        try
        {
            var count = await SubscriptionFetcher.RefreshEntryAsync(
                sub.UnderlyingEntry, _logger, CancellationToken.None);
            sub.LastRefreshFailed = count == 0;
            sub.LastServerCount = count > 0 ? count : (sub.UnderlyingEntry.Servers?.Count ?? 0);
            sub.LastRefreshedAt = sub.UnderlyingEntry.LastRefreshedAt;
        }
        catch (Exception)
        {
            _logger.Error("[VM] Subscription refresh failed");
            sub.LastRefreshFailed = true;
            sub.LastServerCount = sub.UnderlyingEntry.Servers?.Count ?? 0;
        }
        finally
        {
            sub.IsRefreshing = false;
            RebuildSubscriptionPool();
            SaveSettings();
        }

        if (IsConnected && IsSubscribeMode && !IsConnecting && activeSigBefore != null)
        {
            var activeChanged = activeSigBefore != SelectedSubscriptionRoute();

            if (!activeChanged)
            {
                _logger.Information("[VM] Subscription refreshed, keeping current connection");
                RefreshActiveIndicator();
                OnPropertyChanged(nameof(SimpleStatusDescription));
                OnPropertyChanged(nameof(SimpleActiveOutboundLine));
            }
            else
            {
                _logger.Information("[VM] RefreshSubscription: active server changed or removed, reconnecting...");
                var reconnectName = SelectedSubscriptionServer?.Name ?? "subscription";
                await ReconnectAsync(reconnectName, ReconnectIntent.Subscription);
            }
        }
    }

    [RelayCommand]
    private async Task RefreshAllSubscriptionsAsync()
    {
        var enabled = Subscriptions.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (enabled.Count == 0) return;

        var activeSigBefore = SelectedSubscriptionRoute();

        foreach (var s in enabled) s.IsRefreshing = true;
        try
        {
            await RefreshSubscriptionRowsAsync(enabled);
        }
        finally
        {
            foreach (var s in enabled) s.IsRefreshing = false;
            RebuildSubscriptionPool();
            SaveSettings();
        }

        if (IsConnected && IsSubscribeMode && !IsConnecting && activeSigBefore != null)
        {
            var activeChanged = activeSigBefore != SelectedSubscriptionRoute();

            if (!activeChanged)
            {
                _logger.Information("[VM] All subscriptions refreshed, keeping current connection");
                RefreshActiveIndicator();
                OnPropertyChanged(nameof(SimpleStatusDescription));
                OnPropertyChanged(nameof(SimpleActiveOutboundLine));
            }
            else
            {
                _logger.Information("[VM] RefreshAll: active server changed or removed, reconnecting...");
                var reconnectName = SelectedSubscriptionServer?.Name ?? "subscription";
                await ReconnectAsync(reconnectName, ReconnectIntent.Subscription);
            }
        }
    }

    private async Task RefreshSubscriptionRowsAsync(IEnumerable<SubscriptionViewModel> enabled)
    {
        await Task.WhenAll(enabled.Select(async s =>
        {
            try
            {
                var count = await SubscriptionFetcher.RefreshEntryAsync(
                    s.UnderlyingEntry, _logger, CancellationToken.None);
                s.LastRefreshFailed = count == 0;
                s.LastServerCount = count > 0 ? count : (s.UnderlyingEntry.Servers?.Count ?? 0);
                s.LastRefreshedAt = s.UnderlyingEntry.LastRefreshedAt;
            }
            catch (Exception)
            {
                _logger.Warning("[VM] Subscription refresh failed during refresh all");
                s.LastRefreshFailed = true;
                s.LastServerCount = s.UnderlyingEntry.Servers?.Count ?? 0;
            }
        }));
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
            var entries = await SubscriptionFetcher.FetchAsync(SubscriptionUrl, _logger);

            if (entries.Count == 0)
            {
                StatusText = Strings.SyncEmpty;
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
        if (!IsConnected || !(_settings.App.ConfigMode ?? "generated")
            .Equals("subscribe", StringComparison.OrdinalIgnoreCase)) return;

        var enabled = Subscriptions.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (enabled.Count == 0) return;

        _subRefreshCts?.Cancel();
        _subRefreshCts = new CancellationTokenSource();
        var ct = _subRefreshCts.Token;

        try
        {
            _logger.Information("[SubRefresh] Checking {Count} subscription(s)...", enabled.Count);

            var before = SubscriptionServers.Select(row => (row.SubscriptionId, SubscriptionEntry.RowKey(row.SourceEntry))).ToList();

            var activeSigBefore = SelectedSubscriptionRoute();

            await Task.WhenAll(enabled.Select(async s =>
            {
                try
                {
                    var count = await SubscriptionFetcher.RefreshEntryAsync(s.UnderlyingEntry, _logger, ct);
                    s.LastRefreshFailed = count == 0;
                    s.LastServerCount = count > 0 ? count : (s.UnderlyingEntry.Servers?.Count ?? 0);
                    s.LastRefreshedAt = s.UnderlyingEntry.LastRefreshedAt;
                }
                catch (Exception)
                {
                    _logger.Warning("[SubRefresh] Subscription refresh failed");
                    s.LastRefreshFailed = true;
                    s.LastServerCount = s.UnderlyingEntry.Servers?.Count ?? 0;
                }
            }));

            if (ct.IsCancellationRequested) return;

            var after = Subscriptions.Where(s => s.Enabled).SelectMany(s =>
                s.UnderlyingEntry.Servers.Select(row => ((string?)s.Id, SubscriptionEntry.RowKey(row))));
            if (before.SequenceEqual(after))
            {
                SaveSettings();
                return;
            }

            var selectionUnchanged = activeSigBefore == SelectedSubscriptionRoute();
            RebuildSubscriptionPool();
            SaveSettings();
            var activeChanged = selectionUnchanged && activeSigBefore != SelectedSubscriptionRoute();

            if (!activeChanged)
            {
                _logger.Information("[SubRefresh] Server pool changed, keeping current connection");
                RefreshActiveIndicator();
                OnPropertyChanged(nameof(SimpleStatusDescription));
                OnPropertyChanged(nameof(SimpleActiveOutboundLine));
                return;
            }

            if (!IsConnected || IsConnecting || IsApplying || _isReconnecting) return;
            _logger.Information("[SubRefresh] Active server changed, reconnecting...");
            var reconnectName = SelectedSubscriptionServer?.Name ?? "subscription";
            await ReconnectAsync(reconnectName);
        }
        catch (Exception)
        {
            _logger.Error("[SubRefresh] Auto-refresh failed");
        }
    }
}
