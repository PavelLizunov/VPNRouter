using VPNRouter.Core;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private bool _savingSimpleLink;

    internal bool SimpleUsesDirectLink =>
        string.Equals(_settings.App.ConfigMode, "generated", StringComparison.OrdinalIgnoreCase) && Servers.Count > 0;

    internal string SimpleSavedLink
    {
        get
        {
            var app = _settings.App;
            if (SimpleUsesDirectLink)
                return app.SimpleServerLink is { Length: > 0 } link && SelectedServer is { } server &&
                    app.SimpleServerLinkRow == SubscriptionEntry.RowKey(server.SourceEntry) ? link : string.Empty;
            if (!string.Equals(app.ConfigMode, "subscribe", StringComparison.OrdinalIgnoreCase)) return string.Empty;

            var id = SelectedSubscriptionServer?.SubscriptionId ?? _settings.App.ActiveSubscriptionId;
            return Subscriptions.FirstOrDefault(feed => feed.Enabled && feed.Id == id)?.Url
                ?? Subscriptions.FirstOrDefault(feed => feed.Enabled && SimpleConnectPolicy.SameUrl(feed.Url, SmpInput))?.Url
                ?? string.Empty;
        }
    }

    internal readonly record struct SimpleLinkSaveResult(string? Error, string? Notice);

    // The editor owns the draft. The view supplies its lifetime so a cancelled or edited submission
    // cannot publish a late network result to the settings or to a different view.
    internal async Task<string?> SaveSimpleLinkAsync(string input, Func<bool> isCurrent) =>
        (await SaveSimpleLinkDetailedAsync(input, isCurrent)).Error;

    internal async Task<SimpleLinkSaveResult> SaveSimpleLinkDetailedAsync(string input, Func<bool> isCurrent)
    {
        if (SafeMode.Enabled) return new(Strings.ApplyFailed, null);
        if (_savingSimpleLink || !CanSaveSimpleLink()) return new(Strings.SmpLinkSaveBusy, null);
        input = input.Trim();
        var kind = SimpleInputDetector.Classify(input);
        if (kind == SmpInputKind.Invalid || (kind == SmpInputKind.SubscriptionUrl && input.Any(char.IsControl)))
            return new(Strings.SmpLinkInputInvalid, null);

        var choiceUnchanged = CaptureSimpleChoice(out _);
        var generation = _connectionGeneration;
        var applyGeneration = _subscriptionRefreshApplyGeneration;
        var revision = _routingSettingsRevision;
        var wasConnected = IsConnected;
        var settings = _settings;
        _savingSimpleLink = true;
        try
        {
            VlessServerEntry? server = null;
            VlessServerEntry[]? pair = null;
            SubscriptionEntry? feed = null;
            var refreshFailed = false;
            SubscriptionInputAction action = default;
            if (kind == SmpInputKind.ServerUri)
            {
                if (input.IndexOfAny(['\r', '\n']) >= 0)
                {
                    pair = ServerUriParser.ParseTurnRelayPair(input, out _);
                    if (pair == null) return new(Strings.SmpTurnRelayChainRequired, null);
                    server = pair[^1];
                }
                else
                {
                    if (input.Any(char.IsControl)) return new(Strings.SmpLinkInputInvalid, null);
                    server = ParseSimpleServerLink(input, out var error);
                    if (server == null) return new(error, null);
                }
            }
            else
            {
                if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
                    return new(Strings.SmpLinkInputInvalid, null);

                var registered = Subscriptions.Select(row => row.ToEntry()).ToList();
                action = SimpleConnectPolicy.DecideSubscriptionInput(input, registered);
                if (action == SubscriptionInputAction.Disabled) return new(SubscriptionDisabledMessage, null);
                feed = registered.FirstOrDefault(entry => entry.Enabled && SimpleConnectPolicy.SameUrl(entry.Url, input))
                    ?? new SubscriptionEntry { Name = "simple", Url = input, Enabled = true };
                if (action != SubscriptionInputAction.KeepCached)
                {
                    var update = await SubscriptionCacheUpdate.FetchAsync(feed, _logger, CancellationToken.None);
                    refreshFailed = update.Failed || update.Candidate.Servers.Count == 0;
                    update.Candidate.Name = feed.Name;
                    feed = update.Candidate;
                }
            }

            if (!isCurrent() || !CanSaveSimpleLink() || generation != _connectionGeneration || IsConnected != wasConnected ||
                applyGeneration != _subscriptionRefreshApplyGeneration ||
                revision != _routingSettingsRevision || !ReferenceEquals(settings, _settings) || !choiceUnchanged())
                return new(Strings.SmpLinkSaveStale, null);
            if (refreshFailed) return new(Strings.SyncEmpty, null);
            if (server != null && input == SimpleSavedLink ||
                feed != null && action == SubscriptionInputAction.KeepCached && SimpleConnectPolicy.SameUrl(input, SimpleSavedLink))
                return new(null, Strings.SmpLinkAlreadySaved);

            var candidate = SnapshotDesktopSettings();
            var rememberConsumedLink = false;
            if (server != null)
            {
                var dnsttError = CheckSimpleDnsttLink(server, out var existingRow, out rememberConsumedLink);
                if (dnsttError != null) return new(dnsttError, null);

                candidate.App.ConfigMode = "generated";
                candidate.App.SubscriptionPreference = null;
                if (existingRow != null)
                {
                    var pastedName = server.Name;
                    // The detached snapshot preserves the order of the editable Servers list.
                    server = candidate.Vless.Servers[Servers.IndexOf(existingRow)];
                    if (string.IsNullOrWhiteSpace(server.Name)) server.Name = pastedName;
                }
                else if (rememberConsumedLink)
                    candidate.Vless.Servers.Add(server);
                else
                    candidate.Vless.Servers = pair?.ToList() ?? [server];
                candidate.Vless.ActiveServer = server.Name ?? string.Empty;
                SetLegacyManualServer(candidate.Vless, server);
                RememberSimpleDirectLink(candidate.App, server, input, pair != null);
            }
            else
            {
                SelectSavedSimpleFeed(candidate.App, feed!, action);
            }

            BackupConfigFile();
            _settingsStore.Save(candidate, AppPaths.ConfigYamlPath);
            PublishSavedSimpleLink(candidate, input, server != null);
            if (rememberConsumedLink || pair != null)
            {
                _consumedServerInput = input;
                _consumedServerEntry = SelectedServer?.SourceEntry;
            }
            var loadedCount = server != null ? 1 : (feed?.Servers.Count(s => !s.IsTurnRelay) ?? SmpSelectableServerCount);
            var countLabel = Strings.SmpKindServers(loadedCount);
            var notice = wasConnected
                ? Strings.SmpLinkSavedConnected(countLabel)
                : Strings.SmpLinkSavedLoaded(countLabel);
            return new(null, notice);
        }
        catch (Exception ex)
        {
            _logger.Warning("[Simple] Link save failed ({ErrorType})", ex.GetType().Name);
            return new(Strings.ApplyFailed, null);
        }
        finally { _savingSimpleLink = false; }
    }

    private bool CanSaveSimpleLink() => !_disposed && (IsSimpleMode || IsSubscribeMode) &&
        !SimpleCtaIsConnecting && !_isReconnecting && !SmpSubscriptionsRefreshing;

    private static void SelectSavedSimpleFeed(AppConfig app, SubscriptionEntry feed, SubscriptionInputAction action)
    {
        var keepSelection = string.Equals(app.ConfigMode, "subscribe", StringComparison.OrdinalIgnoreCase) &&
            app.ActiveSubscriptionId == feed.Id;
        var selected = keepSelection ? feed.FindExactRow(app.ActiveSubscriptionServer, app.ActiveSubscriptionRow) : null;
        if (selected?.IsTurnRelay != false)
            selected = feed.Servers.FirstOrDefault(row => !row.IsTurnRelay);

        if (action == SubscriptionInputAction.Replace) app.Subscriptions.Add(feed);
        else if (action == SubscriptionInputAction.KeepAndRefresh)
            app.Subscriptions[app.Subscriptions.FindIndex(entry => entry.Id == feed.Id)] = feed;
        if (!keepSelection) app.SubscriptionPreference = null;

        app.ConfigMode = "subscribe";
        app.ActiveSubscriptionId = feed.Id;
        app.ActiveSubscriptionServer = selected?.Name ?? string.Empty;
        app.ActiveSubscriptionRow = selected == null ? null : SubscriptionEntry.RowKey(selected);
    }

    private static void RememberSimpleDirectLink(AppConfig app, VlessServerEntry server, string input, bool isPair = false)
    {
        // A legacy/manual edit of the row invalidates its original link without changing the usable server.
        app.SimpleServerLink = !isPair && input.Any(char.IsControl) ? null : input;
        app.SimpleServerLinkRow = app.SimpleServerLink == null ? null : SubscriptionEntry.RowKey(server);
    }

    private void PublishSavedSimpleLink(AppSettings candidate, string input, bool direct)
    {
        _settings = candidate;
        var loading = _isLoadingUI;
        _isLoadingUI = true;
        try
        {
            IsSubscribeMode = !direct;
            IsVlessMode = direct;
            if (direct)
            {
                SmpFormExpanded = false;
                LoadServersIntoUI();
            }
            else
            {
                SelectedSubscriptionServer = null;
                LoadSubscriptionsIntoUI();
            }
            SmpInput = input;
        }
        finally { _isLoadingUI = loading; }

        MarkRoutingSettingsChanged();
        RefreshActiveIndicator();
        RaiseSimpleHomeProps();
    }
}
