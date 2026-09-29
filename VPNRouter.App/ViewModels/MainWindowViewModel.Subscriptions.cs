#nullable enable
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private void RebuildSubscriptionPool()
    {
        var prevLoading = _isLoadingUI;
        _isLoadingUI = true;
        try
        {
            var selectedUuid = SelectedSubscriptionServer?.Uuid;
            var selectedName = SelectedSubscriptionServer?.Name ?? _settings?.App?.ActiveSubscriptionServer;
            var selectedHost = SelectedSubscriptionServer?.Server;
            var selectedPort = SelectedSubscriptionServer?.Port ?? 0;
            SubscriptionServers.Clear();

            foreach (var sub in Subscriptions)
            {
                if (!sub.Enabled) continue;
                foreach (var serverEntry in sub.UnderlyingEntry.Servers)
                    SubscriptionServers.Add(new ServerViewModel(serverEntry));
            }
            ServerViewModel.RefreshUdpSiblingFlags(SubscriptionServers);
            ServerViewModel.RefreshProviderRiskFlags(SubscriptionServers);

            SelectedSubscriptionServer = (!string.IsNullOrEmpty(selectedName)
                ? SubscriptionServers.FirstOrDefault(s =>
                    string.Equals(s.Name, selectedName, StringComparison.Ordinal) &&
                    ((!string.IsNullOrEmpty(selectedUuid) && string.Equals(s.Uuid, selectedUuid, StringComparison.Ordinal)) ||
                     (!string.IsNullOrEmpty(selectedHost) && string.Equals(s.Server, selectedHost, StringComparison.OrdinalIgnoreCase) && s.Port == selectedPort)))
                  ?? SubscriptionServers.FirstOrDefault(s => string.Equals(s.Name, selectedName, StringComparison.Ordinal))
                : null)
                ?? (!string.IsNullOrEmpty(selectedHost) && selectedPort > 0
                    ? SubscriptionServers.FirstOrDefault(s =>
                        string.Equals(s.Server, selectedHost, StringComparison.OrdinalIgnoreCase) && s.Port == selectedPort)
                    : null)
                ?? (!string.IsNullOrEmpty(selectedUuid) && SubscriptionServers.Count(s => string.Equals(s.Uuid, selectedUuid, StringComparison.Ordinal)) == 1
                    ? SubscriptionServers.FirstOrDefault(s => string.Equals(s.Uuid, selectedUuid, StringComparison.Ordinal))
                    : null)
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
        Subscriptions.Remove(sub);
        _settings.App.Subscriptions.RemoveAll(e => e.Id == sub.Id);
        RebuildSubscriptionPool();
        SaveSettings();
    }

    [RelayCommand]
    private async Task RefreshSubscriptionAsync(SubscriptionViewModel? sub)
    {
        if (sub == null || string.IsNullOrWhiteSpace(sub.Url)) return;
        if (sub.IsRefreshing) return;

        var activeName = SelectedSubscriptionServer?.Name ?? _settings?.App?.ActiveSubscriptionServer;
        var activeUuid = SelectedSubscriptionServer?.Uuid;
        var activeHost = SelectedSubscriptionServer?.Server;
        var activePort = SelectedSubscriptionServer?.Port ?? 0;
        var activeHpk  = SelectedSubscriptionServer?.ToEntry()?.Awg?.HeaderProtectionKey;
        var activeSigBefore = SelectedSubscriptionServer == null
            ? null
            : SubscriptionRefreshDiff.SignatureOf(SelectedSubscriptionServer.Server, SelectedSubscriptionServer.Port, SelectedSubscriptionServer.Uuid, activeHpk);

        sub.IsRefreshing = true;
        try
        {
            var count = await SubscriptionFetcher.RefreshEntryAsync(
                sub.UnderlyingEntry, _logger, CancellationToken.None);
            sub.LastRefreshFailed = count == 0;
            sub.LastServerCount = count > 0 ? count : (sub.UnderlyingEntry.Servers?.Count ?? 0);
            sub.LastRefreshedAt = sub.UnderlyingEntry.LastRefreshedAt;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] RefreshSubscription failed for {Url}", CanaryPolicy.RedactUrl(sub.Url));
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
            var enabled = Subscriptions.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
            var activeSigAfter = SubscriptionRefreshDiff.ActiveServerSignature(
                enabled.SelectMany(s => s.UnderlyingEntry.Servers
                    ?? Enumerable.Empty<VPNRouter.Core.Models.VlessServerEntry>()),
                activeName, activeUuid, activeHost, activePort, activeHpk);
            var activeChanged = !string.Equals(activeSigBefore, activeSigAfter, StringComparison.Ordinal);

            if (!activeChanged)
            {
                _logger.Information(
                    "[VM] RefreshSubscription: active server '{Active}' unchanged — tunnel preserved",
                    activeName ?? "(none)");
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

        var activeName = SelectedSubscriptionServer?.Name ?? _settings?.App?.ActiveSubscriptionServer;
        var activeUuid = SelectedSubscriptionServer?.Uuid;
        var activeHost = SelectedSubscriptionServer?.Server;
        var activePort = SelectedSubscriptionServer?.Port ?? 0;
        var activeHpk  = SelectedSubscriptionServer?.ToEntry()?.Awg?.HeaderProtectionKey;
        var activeSigBefore = SelectedSubscriptionServer == null
            ? null
            : SubscriptionRefreshDiff.SignatureOf(SelectedSubscriptionServer.Server, SelectedSubscriptionServer.Port, SelectedSubscriptionServer.Uuid, activeHpk);

        foreach (var s in enabled) s.IsRefreshing = true;
        try
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
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[VM] Refresh of {Url} failed", CanaryPolicy.RedactUrl(s.Url));
                    s.LastRefreshFailed = true;
                    s.LastServerCount = s.UnderlyingEntry.Servers?.Count ?? 0;
                }
            }));
        }
        finally
        {
            foreach (var s in enabled) s.IsRefreshing = false;
            RebuildSubscriptionPool();
            SaveSettings();
        }

        if (IsConnected && IsSubscribeMode && !IsConnecting && activeSigBefore != null)
        {
            var activeSigAfter = SubscriptionRefreshDiff.ActiveServerSignature(
                enabled.SelectMany(s => s.UnderlyingEntry.Servers
                    ?? Enumerable.Empty<VPNRouter.Core.Models.VlessServerEntry>()),
                activeName, activeUuid, activeHost, activePort, activeHpk);
            var activeChanged = !string.Equals(activeSigBefore, activeSigAfter, StringComparison.Ordinal);

            if (!activeChanged)
            {
                _logger.Information(
                    "[VM] RefreshAll: server pool refreshed, active server '{Active}' unchanged — tunnel preserved",
                    activeName ?? "(none)");
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
            _logger.Error(ex, "[VM] Subscription sync failed");
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

    private async Task RefreshSubscriptionSilentAsync()
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

            var beforeUuids = SubscriptionServers.Select(s => s.Uuid).OrderBy(u => u).ToList();

            var activeName = SelectedSubscriptionServer?.Name ?? _settings?.App?.ActiveSubscriptionServer;
            var activeUuid = SelectedSubscriptionServer?.Uuid;
            var activeHost = SelectedSubscriptionServer?.Server;
            var activePort = SelectedSubscriptionServer?.Port ?? 0;
            var activeHpk  = SelectedSubscriptionServer?.ToEntry()?.Awg?.HeaderProtectionKey;
            var activeSigBefore = SelectedSubscriptionServer == null
                ? null
                : SubscriptionRefreshDiff.SignatureOf(SelectedSubscriptionServer.Server, SelectedSubscriptionServer.Port, SelectedSubscriptionServer.Uuid, activeHpk);

            await Task.WhenAll(enabled.Select(async s =>
            {
                try
                {
                    var count = await SubscriptionFetcher.RefreshEntryAsync(s.UnderlyingEntry, _logger, ct);
                    s.LastRefreshFailed = count == 0;
                    s.LastServerCount = count > 0 ? count : (s.UnderlyingEntry.Servers?.Count ?? 0);
                    s.LastRefreshedAt = s.UnderlyingEntry.LastRefreshedAt;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[SubRefresh] Failed for {Url}", CanaryPolicy.RedactUrl(s.Url));
                    s.LastRefreshFailed = true;
                    s.LastServerCount = s.UnderlyingEntry.Servers?.Count ?? 0;
                }
            }));

            if (ct.IsCancellationRequested) return;

            var afterUuids = enabled
                .SelectMany(s => s.UnderlyingEntry.Servers
                    ?? Enumerable.Empty<VPNRouter.Core.Models.VlessServerEntry>())
                .Select(srv => srv.Uuid ?? string.Empty)
                .OrderBy(u => u, StringComparer.Ordinal)
                .ToList();
            var changed = !beforeUuids.SequenceEqual(afterUuids, StringComparer.Ordinal);

            SaveSettings();

            if (!changed)
            {
                _logger.Information("[SubRefresh] No UUID changes, skipping rebuild and reconnect");
                return;
            }

            var activeSigAfter = SubscriptionRefreshDiff.ActiveServerSignature(
                enabled.SelectMany(s => s.UnderlyingEntry.Servers
                    ?? Enumerable.Empty<VPNRouter.Core.Models.VlessServerEntry>()),
                activeName, activeUuid, activeHost, activePort, activeHpk);
            var activeChanged = !string.Equals(activeSigBefore, activeSigAfter, StringComparison.Ordinal);

            var prevLoadingUi = _isLoadingUI;
            _isLoadingUI = true;
            try { RebuildSubscriptionPool(); }
            finally { _isLoadingUI = prevLoadingUi; }

            if (!activeChanged)
            {
                _logger.Information(
                    "[SubRefresh] Server set changed but active '{Active}' unchanged — pool refreshed, no reconnect",
                    activeName ?? "(none)");
                RefreshActiveIndicator();
                OnPropertyChanged(nameof(SimpleStatusDescription));
                OnPropertyChanged(nameof(SimpleActiveOutboundLine));
                return;
            }

            _logger.Information("[SubRefresh] Active server changed, reconnecting...");
            var reconnectName = SelectedSubscriptionServer?.Name ?? "subscription";
            await ReconnectAsync(reconnectName);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[SubRefresh] Auto-refresh failed");
        }
    }
}
