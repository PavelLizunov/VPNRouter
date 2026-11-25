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

    // The editor owns the draft. The view supplies its lifetime so a cancelled or edited submission
    // cannot publish a late network result to the settings or to a different view.
    internal async Task<string?> SaveSimpleLinkAsync(string input, Func<bool> isCurrent)
    {
        if (SafeMode.Enabled) return Strings.ApplyFailed;
        if (_savingSimpleLink || !CanSaveSimpleLink()) return Strings.SmpLinkSaveBusy;
        input = input.Trim();
        var kind = SimpleInputDetector.Classify(input);
        if (kind == SmpInputKind.Invalid || input.Any(char.IsControl)) return Strings.SmpLinkInputInvalid;

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
            SubscriptionEntry? feed = null;
            var refreshFailed = false;
            SubscriptionInputAction action = default;
            if (kind == SmpInputKind.ServerUri)
            {
                server = ParseSimpleServerLink(input, out var error);
                if (server == null) return error;
            }
            else
            {
                if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
                    return Strings.SmpLinkInputInvalid;

                var registered = Subscriptions.Select(row => row.ToEntry()).ToList();
                action = SimpleConnectPolicy.DecideSubscriptionInput(input, registered);
                if (action == SubscriptionInputAction.Disabled) return SubscriptionDisabledMessage;
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
                return Strings.SmpLinkSaveStale;
            if (refreshFailed) return Strings.SyncEmpty;
            if (server != null && input == SimpleSavedLink ||
                feed != null && action == SubscriptionInputAction.KeepCached && SimpleConnectPolicy.SameUrl(input, SimpleSavedLink))
                return null;

            var candidate = SnapshotDesktopSettings();
            var rememberConsumedLink = false;
            if (server != null)
            {
                var dnsttError = CheckSimpleDnsttLink(server, out var existingRow, out rememberConsumedLink);
                if (dnsttError != null) return dnsttError;

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
                    candidate.Vless.Servers = [server];
                candidate.Vless.ActiveServer = server.Name ?? string.Empty;
                SetLegacyManualServer(candidate.Vless, server);
                RememberSimpleDirectLink(candidate.App, server, input);
            }
            else
            {
                SelectSavedSimpleFeed(candidate.App, feed!, action);
            }

            BackupConfigFile();
            _settingsStore.Save(candidate, AppPaths.ConfigYamlPath);
            PublishSavedSimpleLink(candidate, input, server != null);
            if (rememberConsumedLink)
            {
                _consumedServerInput = input;
                _consumedServerEntry = SelectedServer?.SourceEntry;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warning("[Simple] Link save failed ({ErrorType})", ex.GetType().Name);
            return Strings.ApplyFailed;
        }
        finally { _savingSimpleLink = false; }
    }

    private bool CanSaveSimpleLink() => !_disposed && (IsSimpleMode || IsSubscribeMode) && SmpHasConfig &&
        !SimpleCtaIsConnecting && !_isReconnecting && !SmpSubscriptionsRefreshing;

    private static void SelectSavedSimpleFeed(AppConfig app, SubscriptionEntry feed, SubscriptionInputAction action)
    {
        var keepSelection = string.Equals(app.ConfigMode, "subscribe", StringComparison.OrdinalIgnoreCase) &&
            app.ActiveSubscriptionId == feed.Id;
        var selected = keepSelection ? feed.FindExactRow(app.ActiveSubscriptionServer, app.ActiveSubscriptionRow) : null;
        selected ??= feed.Servers[0];

        if (action == SubscriptionInputAction.Replace) app.Subscriptions.Add(feed);
        else if (action == SubscriptionInputAction.KeepAndRefresh)
            app.Subscriptions[app.Subscriptions.FindIndex(entry => entry.Id == feed.Id)] = feed;
        if (!keepSelection) app.SubscriptionPreference = null;

        app.ConfigMode = "subscribe";
        app.ActiveSubscriptionId = feed.Id;
        app.ActiveSubscriptionServer = selected.Name;
        app.ActiveSubscriptionRow = SubscriptionEntry.RowKey(selected);
    }

    private static void RememberSimpleDirectLink(AppConfig app, VlessServerEntry server, string input)
    {
        // A legacy/manual edit of the row invalidates its original link without changing the usable server.
        app.SimpleServerLink = input.Any(char.IsControl) ? null : input;
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
