#nullable enable
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;
using VPNRouter.Core;
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
            if (SimpleServerPreference == null && selected == null && IsSubscribeConfig(_settings.App.ConfigMode) && source != null &&
                SubscriptionChainSelection.RequiresMatchingSuccessor(previous?.SourceEntry))
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
            ServerViewModel.RefreshChainFlags(SubscriptionServers);

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
                    ?? SubscriptionServers.FirstOrDefault(row => !row.SourceEntry.IsTurnRelay);
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
    internal event Action? SubscriptionRefreshFeedbackChanged;

    private enum SubscriptionRefreshFeedbackKind
    {
        None,
        UpdatedOffline,
        UpdatedConnectedUnchanged,
        UpdatedConnectedReconnecting,
        UpdatedConnectedService,
        PartialFailure,
        FailedCached,
        FailedEmpty,
        ChainPreserved,
    }

    private sealed record SubscriptionRefreshFeedback(
        SubscriptionRefreshFeedbackKind Kind,
        int SelectableCount,
        int SucceededFeeds,
        int TotalFeeds,
        string FailedFeedNames,
        DateTimeOffset At);

    private SubscriptionRefreshFeedback? _lastSubscriptionRefreshFeedback;

    internal bool SmpRefreshResultIsError => _lastSubscriptionRefreshFeedback?.Kind is
        SubscriptionRefreshFeedbackKind.PartialFailure
        or SubscriptionRefreshFeedbackKind.FailedCached
        or SubscriptionRefreshFeedbackKind.FailedEmpty
        or SubscriptionRefreshFeedbackKind.ChainPreserved;

    internal string SmpRefreshResultText
    {
        get
        {
            if (_lastSubscriptionRefreshFeedback is not { } fb) return string.Empty;
            var countText = Core.Localization.Strings.SmpKindServers(fb.SelectableCount);
            return fb.Kind switch
            {
                SubscriptionRefreshFeedbackKind.UpdatedOffline =>
                    Core.Localization.Strings.SmpRefreshUpdated(countText, fb.At.ToLocalTime().ToString("HH:mm")),
                SubscriptionRefreshFeedbackKind.UpdatedConnectedUnchanged =>
                    Core.Localization.Strings.SmpRefreshUpdatedConnectedUnchanged(countText),
                SubscriptionRefreshFeedbackKind.UpdatedConnectedReconnecting =>
                    Core.Localization.Strings.SmpRefreshUpdatedConnectedReconnecting,
                SubscriptionRefreshFeedbackKind.UpdatedConnectedService =>
                    Core.Localization.Strings.SmpRefreshUpdatedConnectedService,
                SubscriptionRefreshFeedbackKind.PartialFailure =>
                    Core.Localization.Strings.SmpRefreshPartialFailure(fb.SucceededFeeds, fb.TotalFeeds, fb.FailedFeedNames),
                SubscriptionRefreshFeedbackKind.FailedCached =>
                    Core.Localization.Strings.SmpRefreshFailedCached(countText),
                SubscriptionRefreshFeedbackKind.FailedEmpty =>
                    Core.Localization.Strings.SmpRefreshFailedEmpty,
                SubscriptionRefreshFeedbackKind.ChainPreserved =>
                    Core.Localization.Strings.SmpRefreshChainPreserved,
                _ => string.Empty,
            };
        }
    }

    private void SetSubscriptionRefreshFeedback(SubscriptionRefreshFeedback? feedback)
    {
        _lastSubscriptionRefreshFeedback = feedback;
        SubscriptionRefreshFeedbackChanged?.Invoke();
    }

    private int _subscriptionRefreshApplyGeneration;

    partial void OnIsApplyingChanged(bool value)
    {
        if (value) _subscriptionRefreshApplyGeneration++;
    }

    private void NotifySubscriptionRefreshState()
    {
        OnPropertyChanged(nameof(SmpSubscriptionsRefreshing));
        OnPropertyChanged(nameof(SmpCanConnect));
        OnPropertyChanged(nameof(SmpHomeSubline));
        SubscriptionRefreshFeedbackChanged?.Invoke();
    }

    private async Task RefreshRegisteredSubscriptionsAsync(List<SubscriptionViewModel> targets, CancellationToken ct)
    {
        if (_disposed || ct.IsCancellationRequested || !SmpCanRefreshSubscriptions || _isReconnecting) return;
        targets.RemoveAll(row => row.IsRefreshing);
        if (targets.Count == 0) return;

        var previous = SelectedSubscriptionServer;
        var previousRoute = SelectedSubscriptionRoute();
        SyncSelectedServerForGeneratedRefresh();
        var generatedGrant = DnsttSelectionRefresh.Capture(_settings);
        var generation = _connectionGeneration;
        var applyGeneration = _subscriptionRefreshApplyGeneration;
        SubscriptionCacheUpdate[]? updates = null;
        var succeededCount = 0;
        var failedNames = string.Empty;
        foreach (var row in targets) row.IsRefreshing = true;
        NotifySubscriptionRefreshState();
        try
        {
            updates = await FetchSubscriptionRowsAsync(targets, ct);
            if (_disposed || ct.IsCancellationRequested || !SmpCanRefreshSubscriptions || _isReconnecting ||
                generation != _connectionGeneration || applyGeneration != _subscriptionRefreshApplyGeneration) return;
            succeededCount = updates.Count(u => !u.Failed);
            var failedFeeds = targets.Zip(updates)
                .Where(pair => pair.Second.Failed)
                .Select(pair => string.IsNullOrWhiteSpace(pair.First.Name) ? pair.First.Url : pair.First.Name)
                .ToList();
            failedNames = string.Join(", ", failedFeeds);

            var published = PublishSubscriptionRows(targets, updates, generatedGrant: generatedGrant);
            if (!published.Accepted)
            {
                if (published.GeneratedOutcome is DnsttSelectionRefresh.RefreshOutcome.Missing
                    or DnsttSelectionRefresh.RefreshOutcome.Ambiguous)
                {
                    StatusText = GeneratedDnsttBlockMessage(published.GeneratedOutcome);
                }
                var selectable = SmpSelectableServerCount;
                var kind = succeededCount > 0
                    ? SubscriptionRefreshFeedbackKind.ChainPreserved
                    : selectable > 0
                        ? SubscriptionRefreshFeedbackKind.FailedCached
                        : SubscriptionRefreshFeedbackKind.FailedEmpty;
                SetSubscriptionRefreshFeedback(new(kind, selectable, succeededCount, targets.Count, failedNames, DateTimeOffset.UtcNow));
                return;
            }
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
        if (generatedGrant != null)
            _generatedDnsttRefreshKey = DnsttSelectionRefresh.KeyOf(DnsttSelectionRefresh.Capture(_settings));

        var selectableAfter = SmpSelectableServerCount;
        var routeChanged = sameSelection && previousRoute != null && previousRoute != SelectedSubscriptionRoute();
        var willReconnect = routeChanged && generation == _connectionGeneration && IsConnected && IsSubscribeMode &&
            !IsConnecting && !IsApplying && !_isReconnecting;

        SubscriptionRefreshFeedbackKind resultKind;
        if (succeededCount == 0)
            resultKind = selectableAfter > 0 ? SubscriptionRefreshFeedbackKind.FailedCached : SubscriptionRefreshFeedbackKind.FailedEmpty;
        else if (succeededCount < targets.Count)
            resultKind = SubscriptionRefreshFeedbackKind.PartialFailure;
        else if (IsConnected && IsSubscribeMode)
            resultKind = !willReconnect
                ? SubscriptionRefreshFeedbackKind.UpdatedConnectedUnchanged
                : IsServiceManagedVpn
                    ? SubscriptionRefreshFeedbackKind.UpdatedConnectedService
                    : SubscriptionRefreshFeedbackKind.UpdatedConnectedReconnecting;
        else
            resultKind = SubscriptionRefreshFeedbackKind.UpdatedOffline;

        SetSubscriptionRefreshFeedback(new(resultKind, selectableAfter, succeededCount, targets.Count, failedNames, DateTimeOffset.UtcNow));

        if (!routeChanged)
        {
            RefreshActiveIndicator();
            OnPropertyChanged(nameof(SimpleStatusDescription));
            OnPropertyChanged(nameof(SimpleActiveOutboundLine));
            return;
        }
        if (!willReconnect)
            return;

        var name = SelectedSubscriptionServer?.Name ?? "subscription";
        if (SubscriptionRefreshReconnect is { } reconnect) await reconnect(name);
        else await ReconnectAsync(name, ReconnectIntent.Subscription);
    }

    private Task<SubscriptionCacheUpdate[]> FetchSubscriptionRowsAsync(List<SubscriptionViewModel> rows, CancellationToken ct) =>
        Task.WhenAll(rows.Select(s => SubscriptionCacheUpdate.FetchAsync(s.ToEntry(), _logger, ct)));

    private readonly record struct SubscriptionPublishResult(
        bool Accepted,
        DnsttSelectionRefresh.RefreshOutcome GeneratedOutcome);

    private SubscriptionPublishResult PublishSubscriptionRows(List<SubscriptionViewModel> rows, SubscriptionCacheUpdate[] updates,
        AppSettings? submission = null, DnsttSelectionRefresh.Binding? generatedGrant = null)
    {
        var feeds = submission?.App.Subscriptions ?? Subscriptions.Select(s => s.ToEntry()).ToList();
        var app = submission?.App ?? _settings.App;
        var preference = SubscriptionServerPreference.For(app);
        var chain = submission != null || preference != null ? SubscriptionChainSelection.Capture(app, feeds) : null;
        // A generated-mode row is not selected from the subscription pool; that pool selection must
        // not veto a feed replacement or a legacy DNSTT reconciliation.
        if (submission == null && preference == null && IsSubscribeConfig(app.ConfigMode) &&
            SelectedSubscriptionServer is { } selectedRow &&
            SubscriptionChainSelection.RequiresMatchingSuccessor(selectedRow.SourceEntry) &&
            feeds.FirstOrDefault(s => s.Id == selectedRow.SubscriptionId) is { } source)
            chain = new(source.Id, source.Url, selectedRow.SourceEntry, SelectedSubscriptionRoute()?.Route);

        // generatedGrant was captured before the await; re-capturing here could bind to a foreign
        // grant or lose the only link to the old cache after an in-flight selection/edit.
        var binding = submission == null ? generatedGrant : null;
        var bindingUpdate = binding == null
            ? null
            : updates.FirstOrDefault(candidate => candidate.Origin.Id == binding.FeedId);
        ServerViewModel? editorRow = null;
        var outcome = DnsttSelectionRefresh.RefreshOutcome.Invalid;
        bool PublishRows()
        {
            if (!SubscriptionCacheUpdate.TryPublish(updates, feeds, chain, out var successor))
                return false;
            if (successor != null) chain?.RebindPin(app, feeds, successor);
            return true;
        }
        bool accepted;
        if (binding == null)
        {
            accepted = PublishRows();
        }
        else
        {
            accepted = false;
            if (bindingUpdate != null && CanApplyGeneratedDnsttRefresh(binding, out editorRow))
            {
                outcome = DnsttSelectionRefresh.Evaluate(_settings, binding, bindingUpdate);
                // A failed/empty feed and a removed or ambiguous grant must not replace the cache:
                // the old list is the only remaining link to the selected row. Only an unchanged
                // or a safely rotated grant may be published.
                if (outcome is DnsttSelectionRefresh.RefreshOutcome.Unchanged
                    or DnsttSelectionRefresh.RefreshOutcome.Applied)
                    accepted = PublishRows();
            }
        }

        if (accepted && binding != null && outcome == DnsttSelectionRefresh.RefreshOutcome.Applied)
        {
            if (DnsttSelectionRefresh.TryApply(_settings, binding, bindingUpdate!, _logger))
            {
                editorRow!.Server = binding.Active.Server;
                editorRow.Port = binding.Active.Port;
                editorRow.Security = binding.Active.Security;
            }
            else
            {
                // The feed was already published, but the selected row was not rotated. Do not
                // report success or consume the first-connect skip for an inconsistent state.
                accepted = false;
                outcome = DnsttSelectionRefresh.RefreshOutcome.Invalid;
            }
        }

        if (accepted && submission == null)
        {
            foreach (var update in updates)
                DnsttSelectionRefresh.RememberDisplacedGrants(_settings, update);
            if (binding != null) binding.Active.DnsttRefreshBinding = null;
        }
        else if (binding != null && outcome is (DnsttSelectionRefresh.RefreshOutcome.Missing
            or DnsttSelectionRefresh.RefreshOutcome.Ambiguous))
        {
            _generatedDnsttRefreshKey = null;
            var key = DnsttSelectionRefresh.KeyOf(binding);
            if (binding.Active.DnsttRefreshBinding != key)
            {
                binding.Active.DnsttRefreshBinding = key;
                try { _settingsStore.Save(_settings, AppPaths.ConfigYamlPath); }
                catch (Exception ex)
                {
                    _logger.Warning("[DnsttSelectionRefresh] Could not save the required grant check ({ErrorType})", ex.GetType().Name);
                }
            }
        }

        foreach (var (view, update) in rows.Zip(updates))
        {
            view.LastRefreshFailed = !accepted || update.Failed;
            if (!accepted) continue;
            view.LastRefreshedAt = update.Origin.LastRefreshedAt;
            view.LastServerCount = update.Origin.Servers.Count;
        }
        if (!accepted && binding != null)
        {
            switch (outcome)
            {
                case DnsttSelectionRefresh.RefreshOutcome.Missing:
                    _logger.Warning(
                        "[DnsttSelectionRefresh] The feed no longer advertises the selected DNSTT grant; kept the previous cache");
                    break;
                case DnsttSelectionRefresh.RefreshOutcome.Ambiguous:
                    _logger.Warning(
                        "[DnsttSelectionRefresh] The feed advertised an ambiguous DNSTT grant; kept the previous cache");
                    break;
                case DnsttSelectionRefresh.RefreshOutcome.FetchFailed:
                    _logger.Information(
                        "[DnsttSelectionRefresh] Could not refresh the selected DNSTT grant; kept the previous cache for a retry");
                    break;
                default:
                    _logger.Information(
                        "[DnsttSelectionRefresh] Kept the previous subscription cache; the selected generated grant changed or is ambiguous");
                    break;
            }
        }
        return new(accepted, outcome);
    }

    private static bool IsSubscribeConfig(string? mode) =>
        string.Equals(mode, "subscribe", StringComparison.OrdinalIgnoreCase);

    private bool CanApplyGeneratedDnsttRefresh(DnsttSelectionRefresh.Binding binding, out ServerViewModel? row)
    {
        row = Servers.FirstOrDefault(view => ReferenceEquals(view.SourceEntry, binding.Active));
        // The editor must still show the selected row without an unsaved edit; otherwise the
        // next SaveSettings would write the user's value over the rotated grant.
        return row != null &&
            ReferenceEquals(SelectedServer ?? Servers.FirstOrDefault(item => !item.SourceEntry.IsTurnRelay), row) &&
            row.Name == binding.Active.Name &&
            row.Server == binding.Active.Server &&
            row.Port == binding.Active.Port &&
            row.Uuid == binding.Active.Uuid &&
            row.Flow == binding.Active.Flow &&
            row.Security == binding.Active.Security;
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
            ServerViewModel.RefreshChainFlags(SubscriptionServers);

            SelectedSubscriptionServer = SubscriptionServers.FirstOrDefault(row => !row.SourceEntry.IsTurnRelay);
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

    private string? _generatedDnsttRefreshKey;

    // The Advanced server list is bound to the view models, while Capture reads AppSettings.
    // Persisting the visible choice into the in-memory settings before capture makes an
    // immediate Connect use the row the user sees, without waiting for the later SaveSettings.
    private void SyncSelectedServerForGeneratedRefresh()
    {
        if (!DnsttSelectionRefresh.IsGenerated(_settings.App.ConfigMode)) return;
        // Settings are reloaded after a manual Advanced connect. Re-point the feed entry graph
        // to the live subscription views so capture, fetch and publish all reference the same
        // registration objects; otherwise a later generated refresh is rejected as stale.
        _settings.App.Subscriptions = Subscriptions.Select(s => s.ToEntry()).ToList();
        ApplyServersToSettings();
    }

    private string GeneratedDnsttRefreshBusyMessage => IsRussian
        ? "Подписка ещё обновляется. Дождитесь завершения и нажмите «Подключить» снова."
        : "The subscription is still refreshing. Wait for it to finish, then press Connect again.";

    private string GeneratedDnsttSelectionChanged => IsRussian
        ? "Выбранный сервер изменился. Нажмите «Подключить» для нового сервера."
        : "The selected server changed. Press Connect to check the new server.";

    private string GeneratedDnsttRowConflict => IsRussian
        ? "У сохранённых серверов совпадает имя или идентификатор DNSTT. Переименуйте сервер с таким же именем или удалите дубликат профиля."
        : "Saved servers share a name or DNSTT ID. Rename the server with the same name or remove the duplicate profile.";

    private bool HasConflictingDnsttRow(ServerViewModel? selected, VlessServerEntry entry)
    {
        var name = string.IsNullOrWhiteSpace(selected?.Name) ? entry.Name : selected.Name;
        return Servers.Any(other => !ReferenceEquals(other, selected) &&
            (string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(other.SourceEntry.OutboundId, entry.OutboundId, StringComparison.OrdinalIgnoreCase)));
    }

    private string GeneratedDnsttConfirmationRequired => IsRussian
        ? "Подписка изменила или убрала этот DNSTT-сервер. Для подключения обновите подписку при доступной сети."
        : "The subscription changed or removed this DNSTT server. Refresh the subscription online before connecting.";

    private string GeneratedDnsttBlockMessage(DnsttSelectionRefresh.RefreshOutcome outcome) => outcome switch
    {
        DnsttSelectionRefresh.RefreshOutcome.Missing => IsRussian
            ? "Подписка больше не выдаёт выбранный DNSTT-сервер. Обновите подписку или выберите другой сервер."
            : "The subscription no longer provides the selected DNSTT server. Refresh the subscription or choose another server.",
        DnsttSelectionRefresh.RefreshOutcome.Ambiguous => IsRussian
            ? "Не удалось определить действующие параметры этого DNSTT-сервера. Обновите подписку или выберите другой сервер."
            : "The subscription could not confirm a unique, usable DNSTT server. Refresh the subscription or choose another server.",
        _ => string.Empty,
    };

    // Generated mode has no background refresh timer. Do one user-initiated fetch on the first
    // manual connect so a stale imported DNSTT grant is replaced before the candidate starts.
    // Returns a user-visible message only when the candidate must not start at all.
    private async Task<string?> RefreshGeneratedDnsttSelectionAsync(int generation, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || !IsCurrentConnectionPreparation(generation)) return null;

        if (!DnsttSelectionRefresh.IsGenerated(_settings.App.ConfigMode) ||
            SelectedServer?.SourceEntry is not { Dnstt: not null } selected) return null;
        SyncSelectedServerForGeneratedRefresh();
        var binding = DnsttSelectionRefresh.Capture(_settings);
        if (binding == null)
            return HasConflictingDnsttRow(SelectedServer, selected) &&
                DnsttSelectionRefresh.MatchesRegisteredGrant(_settings, selected) ? GeneratedDnsttRowConflict : null;
        // A Connect during an ordinary refresh must not race the old cached credential into
        // the engine. Refuse until the refresh settles; the user can press Connect again.
        if (Subscriptions.Any(row => row.IsRefreshing))
            return GeneratedDnsttRefreshBusyMessage;
        var key = DnsttSelectionRefresh.KeyOf(binding);
        var needsConfirmation = key != null && binding.Active.DnsttRefreshBinding == key;
        if (!needsConfirmation && key != null && _generatedDnsttRefreshKey == key) return null;
        var unavailable = needsConfirmation ? GeneratedDnsttConfirmationRequired : null;
        var feed = Subscriptions.FirstOrDefault(row => row.Enabled && row.Id == binding.FeedId &&
            SimpleConnectPolicy.SameUrl(row.Url, binding.FeedUrl));
        if (feed == null) return unavailable;

        var applyGeneration = _subscriptionRefreshApplyGeneration;
        feed.IsRefreshing = true;
        NotifySubscriptionRefreshState();
        try
        {
            var update = await SubscriptionCacheUpdate.FetchAsync(feed.ToEntry(), _logger, ct);
            if (ct.IsCancellationRequested || !IsCurrentConnectionPreparation(generation) ||
                applyGeneration != _subscriptionRefreshApplyGeneration) return GeneratedDnsttSelectionChanged;
            if (!ReferenceEquals(SelectedServer?.SourceEntry, binding.Active))
            {
                feed.LastRefreshFailed = true;
                return GeneratedDnsttSelectionChanged;
            }
            if (update.Failed)
            {
                // A network failure preserves the last authoritative result for this grant.
                feed.LastRefreshFailed = true;
                _logger.Information("[DnsttSelectionRefresh] Could not refresh the selected DNSTT grant");
                return unavailable;
            }
            var published = PublishSubscriptionRows([feed], [update], generatedGrant: binding);
            if (!published.Accepted)
            {
                if (published.GeneratedOutcome is DnsttSelectionRefresh.RefreshOutcome.Missing
                    or DnsttSelectionRefresh.RefreshOutcome.Ambiguous)
                {
                    return GeneratedDnsttBlockMessage(published.GeneratedOutcome);
                }
                return unavailable;
            }
            RebuildSubscriptionPool();
            SaveSettings();
            // The one-shot skip is keyed by the actual saved binding. A failed save above
            // leaves it unset, so the next connect retries and persists again.
            _generatedDnsttRefreshKey = DnsttSelectionRefresh.KeyOf(DnsttSelectionRefresh.Capture(_settings));
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "[DnsttSelectionRefresh] Generated DNSTT refresh failed ({ExceptionType})",
                ex.GetType().Name);
            return unavailable;
        }
        finally
        {
            feed.IsRefreshing = false;
            NotifySubscriptionRefreshState();
        }
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
