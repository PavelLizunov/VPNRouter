using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

internal static class AndroidSubscriptionRefresh
{
    internal const string Subscriptions = "subscriptions_json";
    internal const string StandaloneServers = "servers_json";
    internal const string SelectedName = "selected_server_name";
    internal const string SubscriptionUrl = "subscription_url";
    internal const string SelectedFeedId = "selected_subscription_id";
    internal const string SelectedFeedRow = "selected_subscription_row";
    internal const string ManualUri = "vless_uri";
    internal const string ConfigMode = "config_mode";
    private const string ResolvedCommand = "vpn_resolved_server_command";
    private const string ResolvedSource = "vpn_resolved_feed_source";
    private const string DnsttRowPrefix = "dnstt:";
    private const string ResolvedRoute = "vpn_resolved_feed_route";
    internal const string UiCommand = "vpn_ui_command";
    internal const string CustomJson = "custom_config_json";
    internal const string CustomName = "custom_config_name";
    internal const string SupersededSelection = "vpn_superseded_selection";
    internal const string LegacyManualOwner = "subscription_manual_owner_hash";
    internal const string ImportRevision = "subscription_import_revision";

    private static readonly Dictionary<string, Task> PendingRefreshes = new(StringComparer.Ordinal);

    internal static async Task<int> RefreshAsync(SubscriptionEntry entry, string? importRevision,
        Action<SubscriptionEntry, bool, string?> persist)
    {
        // Keep the request tied to the URL it actually fetched if the user edits the feed while waiting.
        var candidate = new SubscriptionEntry { Id = entry.Id, Url = entry.Url };
        // QR and page refresh can overlap. Commit each response before fetching the same source again.
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (PendingRefreshes)
        {
            previous = PendingRefreshes.GetValueOrDefault(candidate.Url, Task.CompletedTask);
            PendingRefreshes[candidate.Url] = finished.Task;
        }
        try
        {
            await previous;
            return await FetchAndSaveAsync(candidate, (result, rejected) => persist(result, rejected, importRevision));
        }
        finally
        {
            lock (PendingRefreshes)
            {
                if (PendingRefreshes[candidate.Url] == finished.Task) PendingRefreshes.Remove(candidate.Url);
                finished.SetResult();
            }
        }
    }

    private static async Task<int> FetchAndSaveAsync(SubscriptionEntry candidate, Action<SubscriptionEntry, bool> persist)
    {
        int count;
        try
        {
            count = await Task.Run(() => SubscriptionFetcher.RefreshEntryAsync(candidate));
        }
        catch (UnsupportedSubscriptionChainException)
        {
            persist(candidate, true);
            throw;
        }
        if (count > 0) persist(candidate, false);
        return count;
    }

    // The caller holds the preference transaction through read, selection check and commit.
    internal static bool Save(SubscriptionEntry refreshed, bool rejected, string? selectingCommand,
        Func<string, string?> read, Action<IReadOnlyDictionary<string, string?>> commit, JsonSerializerOptions options,
        string? importRevision)
    {
        if (read(ImportRevision) != importRevision)
            throw new InvalidOperationException("Subscription changed during refresh.");
        var json = read(Subscriptions);
        List<SubscriptionEntry> subscriptions;
        if (string.IsNullOrWhiteSpace(json) && read(SubscriptionUrl) == refreshed.Url)
        {
            // The old single-feed format kept its cache in servers_json and had no persistent ID.
            subscriptions = [new SubscriptionEntry
            {
                Id = refreshed.Id, Name = "Default", Url = refreshed.Url,
                Servers = ReadStandalone(read, options)
            }];
        }
        else
        {
            subscriptions = JsonSerializer.Deserialize<List<SubscriptionEntry>>(json ?? "[]", options) ?? [];
        }

        var affected = subscriptions.Where(s => AppliesTo(s, refreshed, rejected)).ToList();
        if (affected.Count == 0) throw new InvalidOperationException("Subscription changed during refresh.");
        // Only a command which already resolved this selection can have the old row in flight.
        // An unrelated refresh must leave a newer command still awaiting its own feed untouched.
        var resolved = read(ResolvedCommand);
        var prepared = IsCurrentSelection(resolved, read) &&
            (read("vpn_state_command") != resolved || read("vpn_state") == "connecting");
        var resolvedFeed = prepared ? read(ResolvedSource) : null;
        var retained = prepared && resolvedFeed == null ? ReadRetainedManual(read) : null;
        var oldRows = affected.SelectMany(s => s.Servers ?? []).Where(row => row != null).ToList();
        var previousRows = affected[0].Servers ?? [];
        foreach (var current in affected) CopyCache(refreshed, current);

        var updates = new Dictionary<string, string?>();
        // Once a registration is replaced, a later rejection cannot identify its former standalone copies.
        bool superseded = selectingCommand != null && !IsCurrentSelection(selectingCommand, read);
        RemoveSupersededCopies(refreshed.Url, oldRows, subscriptions, read, options, updates,
            preserveSelection: superseded && !rejected, rejected: rejected);
        bool selected = !rejected && selectingCommand != null && !superseded;
        if (selected)
        {
            affected[0].Enabled = true;
            SelectFeedRow(updates, affected[0], refreshed.Servers[0]);
        }
        else if (!rejected && !superseded && ReadMode(read) == "subscribe" && read(SelectedFeedId) == refreshed.Id &&
            read(SubscriptionUrl) == refreshed.Url && ReadRetainedManual(read) == null)
        {
            var identity = read(SelectedFeedRow);
            if (!refreshed.Servers.Any(row => RowIdentity(row) == identity))
            {
                var previous = identity == null ? null : previousRows.Find(row => row != null && RowIdentity(row) == identity);
                var route = previous == null ? null : SubscriptionEntry.RouteKey(previous, previousRows);
                var equivalent = previous?.Dnstt != null
                    ? new SubscriptionChainSelection(affected[0].Id, affected[0].Url, previous, route)
                        .Match(affected[0], refreshed.Servers)
                    : route == null ? null : refreshed.Servers.FirstOrDefault(row => SubscriptionEntry.RouteKey(row, refreshed.Servers) == route);
                if (equivalent != null)
                    BindFeed(updates, affected[0], equivalent);
                else if (identity?.StartsWith(DnsttRowPrefix, StringComparison.Ordinal) != true &&
                    string.IsNullOrEmpty(previous?.DetourVia))
                {
                    var name = read(SelectedName);
                    var matches = refreshed.Servers.Where(row =>
                        string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                    if (matches.Count <= 1)
                        BindFeed(updates, affected[0], matches.FirstOrDefault() ?? refreshed.Servers[0]);
                }
            }
        }

        var resolvedRoute = resolvedFeed != null ? read(ResolvedRoute) : null;
        if ((resolvedRoute != null && affected.Any(feed => SourceIdentity(feed.Id, feed.Url) == resolvedFeed) &&
             !refreshed.Servers.Any(row => SubscriptionEntry.RouteKey(row, refreshed.Servers) == resolvedRoute)) ||
            (retained != null && updates.ContainsKey(ManualUri) && read(SubscriptionUrl) == refreshed.Url))
            updates[SupersededSelection] = resolved;
        updates[Subscriptions] = JsonSerializer.Serialize(subscriptions, options);
        commit(updates);
        return selected;
    }

    internal static bool SaveSingleServer(VlessServerEntry server, string uri, string command,
        Func<string, string?> read, Action<IReadOnlyDictionary<string, string?>> commit, JsonSerializerOptions options)
    {
        if (!IsCurrentSelection(command, read)) return false;
        var servers = ReadStandalone(read, options);
        var updates = new Dictionary<string, string?>();
        // Removing the old scalar URL must not sever its ownership of legacy cached rows.
        PreserveLegacyFeed(updates, read, servers, options);
        // Earlier clients kept a duplicate of the superseded scalar in the standalone list.
        // Its digest still identifies it until this transaction changes the active selection.
        RemoveRetainedManualCopy(servers, ReadRetainedManual(read), options);
        SelectManualRow(updates, servers, server, uri, options);
        commit(updates);
        return true;
    }

    // Pasted hop + target pairs arrive as separate share links. Keep both rows so
    // the target's opaque detour id resolves, and select the target (never the hop).
    internal static bool SaveServerLinks(IReadOnlyList<VlessServerEntry> entries, string targetUri, string command,
        Func<string, string?> read, Action<IReadOnlyDictionary<string, string?>> commit, JsonSerializerOptions options)
    {
        if (!IsCurrentSelection(command, read)) return false;
        if (entries.Count == 0) return false;

        var servers = ReadStandalone(read, options);
        var updates = new Dictionary<string, string?>();
        PreserveLegacyFeed(updates, read, servers, options);
        RemoveRetainedManualCopy(servers, ReadRetainedManual(read), options);

        var indexes = new List<int>();
        foreach (var entry in entries)
            indexes.Add(AddStandaloneRow(servers, entry, options));

        var targetIndex = indexes[^1];
        var target = servers[targetIndex];
        updates[SelectedFeedId] = null;
        updates[SelectedFeedRow] = null;
        updates[StandaloneServers] = JsonSerializer.Serialize(servers, options);
        updates[SelectedName] = target.Name;
        updates[ManualUri] = targetUri;
        updates[LegacyManualOwner] = null;
        updates[SubscriptionUrl] = null;
        updates[ConfigMode] = "manual";
        commit(updates);
        return true;
    }

    private static int AddStandaloneRow(List<VlessServerEntry> servers, VlessServerEntry entry,
        JsonSerializerOptions options)
    {
        var identity = JsonSerializer.Serialize(entry, options);
        var existing = servers.FindIndex(row => JsonSerializer.Serialize(row, options) == identity);
        if (existing >= 0) return existing;

        var names = servers.Select(row => row.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = string.IsNullOrWhiteSpace(entry.Name) ? entry.Server : entry.Name;
        var name = baseName;
        for (var suffix = 2; names.Contains(name); suffix++)
            name = $"{baseName} #{suffix}";
        entry.Name = name;
        servers.Add(entry);
        return servers.Count - 1;
    }

    private static void PreserveLegacyFeed(Dictionary<string, string?> changes, Func<string, string?> read,
        List<VlessServerEntry> servers, JsonSerializerOptions options)
    {
        if (!string.IsNullOrWhiteSpace(read(Subscriptions)) || read(SubscriptionUrl) is not { Length: > 0 } url) return;
        var feed = LegacyFeed(url, servers);
        changes[Subscriptions] = JsonSerializer.Serialize(new List<SubscriptionEntry> { feed }, options);
    }

    internal static SubscriptionEntry LegacyFeed(string url, List<VlessServerEntry> rows) =>
        new() { Id = "legacy-default", Name = "Default", Enabled = true, Url = url, Servers = rows };

    private static int RemoveRetainedManualCopy(List<VlessServerEntry> servers, VlessServerEntry? retired, JsonSerializerOptions options)
    {
        if (retired == null) return 0;
        var identity = JsonSerializer.Serialize(retired, options);
        return servers.RemoveAll(row => JsonSerializer.Serialize(row, options) == identity);
    }

    private static void SelectManualRow(Dictionary<string, string?> updates, List<VlessServerEntry> servers,
        VlessServerEntry server, string uri, JsonSerializerOptions options)
    {
        var index = VPNRouter.Core.Services.FreeConfigs.FreeConfigSelection.SelectOrAdd(servers, server);
        updates[SelectedFeedId] = null;
        updates[SelectedFeedRow] = null;
        updates[StandaloneServers] = JsonSerializer.Serialize(servers, options);
        updates[SelectedName] = servers[index].Name;
        updates[ManualUri] = uri;
        updates[LegacyManualOwner] = null;
        updates[SubscriptionUrl] = null;
        updates[ConfigMode] = "manual";
    }

    internal static bool IsCurrentSelection(string? command, Func<string, string?> read) =>
        command != null && command == read(UiCommand) && command != read(SupersededSelection);

    internal static string RowIdentity(VlessServerEntry row) =>
        (row.Dnstt == null ? string.Empty : DnsttRowPrefix) + SubscriptionEntry.RowKey(row);

    private static void BindFeed(Dictionary<string, string?> updates, SubscriptionEntry? feed, VlessServerEntry? row)
    {
        updates[SelectedName] = row?.Name;
        updates[SelectedFeedId] = feed?.Id;
        updates[SelectedFeedRow] = row == null ? null : RowIdentity(row);
        updates[SubscriptionUrl] = feed?.Url;
    }

    private static void SelectFeedRow(Dictionary<string, string?> updates, SubscriptionEntry feed, VlessServerEntry row)
    {
        BindFeed(updates, feed, row);
        updates[ManualUri] = null;
        updates[LegacyManualOwner] = null;
        updates[ConfigMode] = "subscribe";
    }

    internal static bool SaveFeedSelection(SubscriptionEntry source, VlessServerEntry row, string? command,
        List<SubscriptionEntry> subscriptions, Func<string, string?> read,
        Action<IReadOnlyDictionary<string, string?>> commit, JsonSerializerOptions options)
    {
        if (command != null && !IsCurrentSelection(command, read)) return false;
        var feed = subscriptions.FirstOrDefault(s => s != null && s.Id == source.Id && s.Url == source.Url);
        var identity = RowIdentity(row);
        if (feed?.Servers == null || !feed.Servers.Any(s => s != null && RowIdentity(s) == identity))
            return false;
        feed.Enabled = true;
        var changes = new Dictionary<string, string?> { [Subscriptions] = JsonSerializer.Serialize(subscriptions, options) };
        if (command == null) changes[SupersededSelection] = read(UiCommand);
        SelectFeedRow(changes, feed, row);
        commit(changes);
        return true;
    }

    internal static void SaveRegistrations(List<SubscriptionEntry> previous, List<SubscriptionEntry> next, string mode,
        Func<string, string?> read, Action<IReadOnlyDictionary<string, string?>> commit, JsonSerializerOptions options)
    {
        var changes = new Dictionary<string, string?>();
        foreach (var feed in next)
        {
            if (feed == null || previous.Any(f => f != null && f.Id == feed.Id && f.Url == feed.Url) ||
                !previous.Any(f => f != null && f.Id == feed.Id)) continue;
            // A different URL has not supplied this registration's old cache or quota.
            feed.Servers = [];
            feed.LastServerCount = 0;
            feed.LastRefreshedAt = null;
            feed.UserInfo = null;
        }
        // The legacy single-feed layout stored its rows in the standalone collection.
        // Move those copies out when the old source changes. Other registrations retain their own caches.
        var legacy = previous.Find(f => f?.Id == "legacy-default" && !string.IsNullOrEmpty(f.Url));
        if (legacy?.Servers is { Count: > 0 } legacyRows && !next.Any(f => f is { Enabled: true } && f.Id == legacy.Id && f.Url == legacy.Url))
        {
            var owned = legacyRows.Where(row => row != null).Select(RowIdentity).ToHashSet(StringComparer.Ordinal);
            if (mode == "manual" && ServerUriParser.TryParse(read(ManualUri) ?? string.Empty) is { } manual)
                owned.Remove(RowIdentity(manual));
            var standalone = ReadStandalone(read, options);
            if (standalone.RemoveAll(row => row != null && owned.Contains(RowIdentity(row))) > 0)
                changes[StandaloneServers] = JsonSerializer.Serialize(standalone, options);
        }
        if (mode == "subscribe")
        {
            var retainedManual = ReadRetainedManual(read);
            var previousRow = retainedManual == null ? FindFeedRow(previous, read) : null;
            var url = read(SubscriptionUrl);
            var id = read(SelectedFeedId);
            var old = previousRow?.Feed ?? previous.Find(f => f != null && f.Url == url && (id == null || f.Id == id));
            if (old != null)
            {
                var same = next.Find(f => f is { Enabled: true } && f.Id == old.Id && f.Url == old.Url)
                    ?? next.Find(f => f is { Enabled: true } && f.Id == old.Id);
                if (same == null || same.Url != old.Url || previousRow != null && previousRow.In(same) == null)
                {
                    var target = same ?? next.Find(f => f is { Enabled: true, Servers.Count: > 0 })
                        ?? next.Find(f => f is { Enabled: true });
                    BindFeed(changes, target, target?.Servers?.FirstOrDefault());
                    changes[SupersededSelection] = read(UiCommand);
                    if (retainedManual != null)
                    {
                        changes[ManualUri] = null;
                        changes[LegacyManualOwner] = null;
                    }
                }
            }
        }
        changes[Subscriptions] = JsonSerializer.Serialize(next, options);
        commit(changes);
    }

    internal static void ImportProfile(ConfigShareDocument doc, Func<string, string?> read,
        Action<IReadOnlyDictionary<string, string?>> commit, JsonSerializerOptions options)
    {
        var mode = (doc.ConfigMode ?? "subscribe").ToLowerInvariant();
        var feeds = doc.Subscriptions ?? [];
        var changes = new Dictionary<string, string?>
        {
            [ImportRevision] = Guid.NewGuid().ToString("N"),
            [SupersededSelection] = read(UiCommand),
            [ConfigMode] = mode,
            [Subscriptions] = JsonSerializer.Serialize(feeds, options),
            [SelectedName] = null, [SelectedFeedId] = null, [SelectedFeedRow] = null,
            [SubscriptionUrl] = null, [LegacyManualOwner] = null, [ManualUri] = null
        };
        if (mode == "manual")
        {
            if (string.IsNullOrWhiteSpace(doc.ManualVlessUri))
                changes[ManualUri] = string.Empty; // An exported empty profile has no active server.
            else
                SelectManualRow(changes, ReadStandalone(read, options), ServerUriParser.Parse(doc.ManualVlessUri),
                    doc.ManualVlessUri, options);
        }
        else if (mode == "subscribe")
        {
            var enabled = feeds.Where(f => f is { Enabled: true, Servers.Count: > 0 }).ToList();
            var choice = read(ConfigMode) == "subscribe" ? FindFeedRow(feeds, read) : null;
            var source = choice?.Feed ?? enabled.Find(f => read(SelectedFeedId) == f.Id && read(SubscriptionUrl) == f.Url)
                ?? enabled.Find(f => read(SubscriptionUrl) == f.Url) ?? enabled.FirstOrDefault();
            if (source != null)
            {
                SelectFeedRow(changes, source, choice?.Row ?? source.Servers[0]);
            }
            else changes[SubscriptionUrl] = feeds.FirstOrDefault(f => f?.Enabled == true)?.Url;
        }
        else if (mode == "custom" && doc.CustomConfig != null)
        {
            changes[CustomJson] = doc.CustomConfig.SingBoxJson;
            changes[CustomName] = doc.CustomConfig.Name;
        }
        else throw new FormatException("Invalid connection profile mode or payload.");
        commit(changes);
    }

    internal static void SaveCustomProfile(string raw, Func<string, string?> read,
        Action<IReadOnlyDictionary<string, string?>> commit, JsonSerializerOptions options)
    {
        var changes = new Dictionary<string, string?>
        {
            [CustomJson] = raw, [ConfigMode] = "custom", [SupersededSelection] = read(UiCommand),
            [LegacyManualOwner] = null
        };
        var retired = ReadRetainedManual(read);
        if (retired != null || string.IsNullOrWhiteSpace(read(Subscriptions)) && !string.IsNullOrEmpty(read(SubscriptionUrl)))
        {
            var standalone = ReadStandalone(read, options);
            PreserveLegacyFeed(changes, read, standalone, options);
            if (RemoveRetainedManualCopy(standalone, retired, options) > 0)
                changes[StandaloneServers] = JsonSerializer.Serialize(standalone, options);
            if (retired != null) changes[ManualUri] = null;
        }
        BindFeed(changes, null, null);
        commit(changes);
    }

    internal static VlessServerEntry? ResolveForConnect(string command, List<VlessServerEntry> standalone,
        List<SubscriptionEntry> subscriptions, string mode, Func<string, string?> read,
        Action<IReadOnlyDictionary<string, string?>> commit, JsonSerializerOptions options)
    {
        if (!IsCurrentSelection(command, read)) return null;
        var row = ResolveActiveServer(standalone, subscriptions, mode, read, commit, options);
        if (row == null) return null;
        SelectedSources.TryGetValue(row, out var source);
        if (source?.Url != null || ReadRetainedManual(read) != null)
            commit(new Dictionary<string, string?>
            {
                [ResolvedCommand] = command,
                [ResolvedSource] = source?.Url == null ? null : SourceIdentity(source.Id, source.Url),
                [ResolvedRoute] = source?.Route
            });
        return row;
    }

    internal static VlessServerEntry? ResolveActiveServer(List<VlessServerEntry> standalone,
        List<SubscriptionEntry> subscriptions, string mode, Func<string, string?> read,
        Action<IReadOnlyDictionary<string, string?>>? commit, JsonSerializerOptions options)
    {
        var name = read(SelectedName);
        if (mode == "custom") return null;
        if (ReadRetainedManual(read) is { } retained &&
            (string.IsNullOrEmpty(name) || string.Equals(name, retained.Name, StringComparison.OrdinalIgnoreCase)))
        {
            retained.UseAsSingleOutbound = true;
            return retained;
        }

        if (mode == "manual" && read(ManualUri) is { } uri)
        {
            var manual = ServerUriParser.TryParse(uri);
            if (manual == null) return null;
            // Old Simple saves could persist the fallback feed name when drawing the summary.
            var previous = standalone.Find(s => s != null && s.Name == name && s.UseAsSingleOutbound);
            var index = VPNRouter.Core.Services.FreeConfigs.FreeConfigSelection.SelectOrAdd(standalone, manual);
            var selected = standalone[index];
            if (!ReferenceEquals(previous, selected))
                commit?.Invoke(new Dictionary<string, string?>
                {
                    [StandaloneServers] = JsonSerializer.Serialize(standalone, options), [SelectedName] = selected.Name
                });
            return selected;
        }

        if (mode == "subscribe")
        {
            var choice = FindFeedRow(subscriptions, read);
            if (choice == null) return null;
            if (read(SelectedFeedRow) == null)
            {
                var changes = new Dictionary<string, string?>();
                BindFeed(changes, choice.Feed, choice.Row);
                commit?.Invoke(changes);
            }
            SelectedSources.AddOrUpdate(choice.Row, FeedSource.Capture(choice.Feed, choice.Row));
            return choice.Row;
        }
        var first = FindLegacyRow(standalone, subscriptions, name);
        if (first == null) return null;
        var source = subscriptions.Find(f => f is { Enabled: true } && f.Servers?.Contains(first) == true);
        SelectedSources.AddOrUpdate(first, FeedSource.Capture(source, first));
        if (!string.IsNullOrEmpty(first.Name) && first.Name != name)
            commit?.Invoke(new Dictionary<string, string?> { [SelectedName] = first.Name, [SelectedFeedRow] = null });
        return first;
    }

    internal sealed record FeedRow(SubscriptionEntry Feed, VlessServerEntry Row)
    {
        internal VlessServerEntry? In(SubscriptionEntry? source)
        {
            if (source == null || source.Id != Feed.Id || source.Url != Feed.Url || !source.Enabled) return null;
            var identity = RowIdentity(Row);
            return source.Servers?.Find(row => row != null && RowIdentity(row) == identity);
        }
    }

    private static VlessServerEntry? FindLegacyRow(List<VlessServerEntry> standalone,
        List<SubscriptionEntry> subscriptions, string? name)
    {
        var feedRows = subscriptions.Where(f => f is { Enabled: true, Servers: not null }).SelectMany(f => f.Servers);
        var available = standalone.Concat(feedRows).Where(row => row != null);
        return available.FirstOrDefault(row => !string.IsNullOrEmpty(name) &&
            string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)) ?? available.FirstOrDefault();
    }

    internal static FeedRow? FindFeedRow(List<SubscriptionEntry> subscriptions, Func<string, string?> read)
    {
        var id = read(SelectedFeedId);
        var url = read(SubscriptionUrl);
        var name = read(SelectedName);
        var rows = subscriptions.Where(f => f is { Enabled: true, Servers: not null })
            .SelectMany(f => f.Servers.Where(s => s != null).Select(s => new FeedRow(f, s)));
        bool Named(FeedRow choice) => !string.IsNullOrEmpty(name) &&
            string.Equals(choice.Row.Name, name, StringComparison.OrdinalIgnoreCase);
        var selectedIdentity = read(SelectedFeedRow);
        if (id != null || selectedIdentity != null)
        {
            rows = rows.Where(choice => choice.Feed.Id == id && choice.Feed.Url == url);
            if (selectedIdentity is { } identity)
                return rows.FirstOrDefault(choice => RowIdentity(choice.Row) == identity);
        }
        else
        {
            // The old picker wrote only a name and could leave the URL of an earlier import.
            var named = rows.Where(Named).Take(2).ToList();
            if (named.Count == 1) return named[0];
            if (!string.IsNullOrEmpty(url)) rows = rows.Where(choice => choice.Feed.Url == url);
        }
        return rows.FirstOrDefault(Named) ?? rows.FirstOrDefault();
    }

    internal static string SavedServerInput(string mode, Func<string, string?> read, Func<List<SubscriptionEntry>> getFeeds)
    {
        if (mode == "manual") return read(ManualUri) ?? string.Empty;
        if (mode != "subscribe") return string.Empty;
        if (read(SelectedFeedId) == null && !string.IsNullOrEmpty(read(SelectedName)) && ReadRetainedManual(read) == null)
            return FindFeedRow(getFeeds(), read)?.Feed.Url ?? read(SubscriptionUrl) ?? string.Empty;
        return read(SubscriptionUrl) ?? string.Empty;
    }

    internal static string ReadMode(Func<string, string?> read) => read(ConfigMode) switch
    {
        "custom" => "custom",
        "subscribe" => "subscribe",
        "manual" => "manual",
        _ => string.IsNullOrEmpty(read(SubscriptionUrl)) ? "manual" : "subscribe"
    };

    internal static string? SubscriptionToRefresh(string input, string mode, Func<string, string?> read,
        Func<List<SubscriptionEntry>> getFeeds)
    {
        var typedUrl = input.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                       input.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        if (!typedUrl && (input.Length != 0 || mode != "subscribe")) return null;
        var saved = mode == "subscribe" ? SavedServerInput(mode, read, getFeeds) : null;
        var url = typedUrl ? input : saved;
        if (string.IsNullOrEmpty(url)) return null;
        var id = read(SelectedFeedId);
        if (url != saved) return url;
        var registered = getFeeds().Where(f => f is { Enabled: true } && f.Url == url && (id == null || f.Id == id)).ToList();
        if (id != null && registered.Count == 0) return null;
        return registered.Any(f => f.Servers is { Count: > 0 }) ? null : url;
    }

    // The Android service passes the resolved entry to the builder on a worker thread.
    // Keep its registration with that instance until the builder checks the current preferences.
    private sealed record FeedSource(string? Id, string? Url, string Route)
    {
        internal static FeedSource Capture(SubscriptionEntry? feed, VlessServerEntry row) =>
            new(feed?.Id, feed?.Url, feed == null ? RowIdentity(row) : SubscriptionEntry.RouteKey(row, feed.Servers));
    }

    private static string SourceIdentity(string? id, string url)
    {
        // A length prefix distinguishes missing and empty IDs without splitting imported text.
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{id?.Length ?? -1}:{id}{url}");
        try { return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    private static readonly ConditionalWeakTable<VlessServerEntry, FeedSource> SelectedSources = new();

    internal static void PreparePool(VlessConfig config, VlessServerEntry selected, List<VlessServerEntry> standalone,
        List<SubscriptionEntry> subscriptions, string mode, Func<string, string?> read, JsonSerializerOptions options)
    {
        // An explicit URI passed directly to the builder has no saved pool to join.
        if (!SelectedSources.TryGetValue(selected, out var origin))
        {
            config.Servers = [selected];
            return;
        }
        var current = ResolveActiveServer(standalone, subscriptions, mode, read, null, options);
        if (current == null || !SelectedSources.TryGetValue(current, out var currentSource) ||
            currentSource != origin)
            throw new InvalidOperationException("Selected server is no longer available.");

        if (!string.IsNullOrEmpty(current.DetourVia))
        {
            var feed = subscriptions.FirstOrDefault(f => f is { Enabled: true, Servers: not null } &&
                f.Id == origin.Id && f.Url == origin.Url && f.Servers.Contains(current));
            config.Servers = [current];
            if (feed != null)
                config.Servers.AddRange(feed.Servers.Where(row => string.IsNullOrEmpty(row.DetourVia) &&
                    string.Equals(row.OutboundId, current.DetourVia, StringComparison.OrdinalIgnoreCase)));
            config.ActiveServer = current.Name ?? string.Empty;
            return;
        }

        var identity = RowIdentity(current);
        var pool = standalone.Concat(subscriptions.Where(f => f is { Enabled: true, Servers: not null })
            .SelectMany(f => f.Servers)).Where(row => row != null).ToList();
        pool.RemoveAll(row => RowIdentity(row) == identity);
        pool.Insert(0, current);
        config.Servers = pool;
        config.ActiveServer = current.Name ?? string.Empty;
        config.AutoSelectBestServer = pool.Count > 1;
    }

    internal static bool HasServerInputEdit(string? displayed, string? saved)
    {
        var input = displayed?.Trim();
        return !string.IsNullOrEmpty(input) && !string.Equals(input, saved?.Trim(), StringComparison.Ordinal);
    }

    internal static string RebindSimpleInput(string? displayed, string? previous, string saved) =>
        previous != null && HasServerInputEdit(displayed, previous) ? displayed! : saved;

    private static string ManualOwner(string url, string uri) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(url + "\0" + uri)));

    internal static VlessServerEntry? ReadRetainedManual(Func<string, string?> read)
    {
        var owner = read(LegacyManualOwner);
        if (owner == null || read(ConfigMode) is not (null or "subscribe") ||
            read(SubscriptionUrl) is not { Length: > 0 } url || read(ManualUri) is not { Length: > 0 } uri ||
            owner != ManualOwner(url, uri)) return null;
        return ServerUriParser.TryParse(uri);
    }

    internal static bool AppliesTo(SubscriptionEntry? registration, SubscriptionEntry refreshed, bool rejected) =>
        registration != null && registration.Url == refreshed.Url && (rejected || registration.Id == refreshed.Id);

    internal static void CopyCache(SubscriptionEntry refreshed, SubscriptionEntry current)
    {
        current.Servers = [.. refreshed.Servers];
        current.LastServerCount = refreshed.LastServerCount;
        current.LastRefreshedAt = refreshed.LastRefreshedAt;
        if (refreshed.UserInfo != null) current.UserInfo = refreshed.UserInfo;
    }

    private static List<VlessServerEntry> ReadStandalone(Func<string, string?> read, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<List<VlessServerEntry>>(read(StandaloneServers) ?? "[]", options) ?? [];

    private static void RemoveSupersededCopies(string url, List<VlessServerEntry> oldRows,
        List<SubscriptionEntry> subscriptions, Func<string, string?> read, JsonSerializerOptions options,
        Dictionary<string, string?> updates, bool preserveSelection, bool rejected)
    {
        var recordedOwner = read(LegacyManualOwner);
        if (oldRows.Count == 0 && !(rejected && recordedOwner != null)) return;

        // A name or endpoint alone can also identify an independently saved manual server.
        string Identity(VlessServerEntry row) => JsonSerializer.Serialize(row, options);
        var oldCopies = oldRows.Select(Identity).ToHashSet(StringComparer.Ordinal);
        var retained = subscriptions.OfType<SubscriptionEntry>().SelectMany(s => s.Servers ?? [])
            .Where(row => row != null).Select(Identity).ToHashSet(StringComparer.Ordinal);
        oldCopies.ExceptWith(retained);

        var uri = read(ManualUri);
        var manual = ServerUriParser.TryParse(uri ?? string.Empty);
        string? retiredManualName = null;
        bool keepScalarOnly = false;
        if (manual != null)
        {
            var manualKey = Identity(manual);
            if (read(SubscriptionUrl) == url && read(ConfigMode) is null or "subscribe")
            {
                // A superseded success keeps the selected legacy URI but replaces the cache that
                // identified its owner. Retain only a digest so a later rejection can still clear it.
                var owner = ManualOwner(url, uri!);
                if (rejected && recordedOwner == owner && !retained.Contains(manualKey)) oldCopies.Add(manualKey);
                if (oldCopies.Contains(manualKey))
                {
                    if (preserveSelection)
                    {
                        updates[LegacyManualOwner] = owner;
                        keepScalarOnly = true;
                    }
                    else
                    {
                        updates[ManualUri] = null;
                        updates[LegacyManualOwner] = null;
                        retiredManualName = manual.Name;
                        manual = null;
                    }
                }
            }
            if (manual != null && !keepScalarOnly) oldCopies.Remove(manualKey);
        }
        var standalone = ReadStandalone(read, options);
        if (standalone.RemoveAll(row => oldCopies.Contains(Identity(row))) > 0)
            updates[StandaloneServers] = JsonSerializer.Serialize(standalone, options);

        if (preserveSelection) return;
        var selected = read(SelectedName);
        bool IsSelected(VlessServerEntry row) => string.Equals(row?.Name, selected, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(selected) && (oldRows.Any(IsSelected) ||
                string.Equals(selected, retiredManualName, StringComparison.OrdinalIgnoreCase))
            && !standalone.Any(IsSelected) && !subscriptions.Any(s => s is { Enabled: true } && s.Servers?.Any(IsSelected) == true)
            && (manual == null || !IsSelected(manual)))
            updates[SelectedName] = null;
    }
}
