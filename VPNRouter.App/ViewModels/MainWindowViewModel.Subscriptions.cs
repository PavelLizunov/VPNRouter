#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// Phase 2B (Wave 8, 2026-05-18) — Subscription tab commands + auto-refresh
/// timer split out of the <c>MainWindowViewModel</c> god-class. Hosts the
/// user-facing Subscribe-tab surface:
///
/// <list type="bullet">
///   <item><see cref="RebuildSubscriptionPool"/> — aggregator that
///   flattens enabled <c>Subscriptions[]</c> into the
///   <see cref="MainWindowViewModel.SubscriptionServers"/>
///   <c>ObservableCollection</c> the Subscribe tab binds to.</item>
///   <item><see cref="AddSubscriptionAsync"/> / <see cref="RemoveSubscription"/>
///   — explicit user actions on individual subscription cards.</item>
///   <item><see cref="RefreshSubscriptionAsync"/> /
///   <see cref="RefreshAllSubscriptionsAsync"/> — single + bulk manual
///   refresh of subscription contents.</item>
///   <item><see cref="SyncSubscriptionAsync"/> — legacy single-URL sync
///   path (kept for users who haven't migrated to the multi-sub UI).</item>
///   <item><see cref="ClearSubscription"/> — wipe the subscription pool +
///   legacy URL field in one click.</item>
///   <item><see cref="StartSubRefreshTimer"/> /
///   <see cref="StopSubRefreshTimer"/> — periodic (1-hour) refresh while
///   VPN is connected in subscribe mode.</item>
///   <item><see cref="RefreshSubscriptionSilentAsync"/> — the silent
///   refresh body the periodic timer fires; v2.31.8-r3 added UUID
///   comparison so unchanged server sets don't trigger reconnects.</item>
/// </list>
///
/// <para>Server-model wiring (<see cref="MainWindowViewModel.Servers"/>,
/// active-indicator refresh, orphan tracking) stays in the main file
/// because it overlaps with manual-VLESS-mode and connect/reconnect
/// orchestration. This partial only owns the Subscribe surface.</para>
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Rebuild aggregated server pool from all enabled subscriptions.</summary>
    private void RebuildSubscriptionPool()
    {
        var selectedName = SelectedSubscriptionServer?.Name;
        SubscriptionServers.Clear();

        foreach (var sub in Subscriptions)
        {
            if (!sub.Enabled) continue;
            foreach (var serverEntry in sub.UnderlyingEntry.Servers)
                SubscriptionServers.Add(new ServerViewModel(serverEntry));
        }
        ServerViewModel.RefreshUdpSiblingFlags(SubscriptionServers); // r8 #6

        // Restore selection if possible
        SelectedSubscriptionServer = SubscriptionServers
            .FirstOrDefault(s => s.Name == selectedName)
            ?? SubscriptionServers.FirstOrDefault();
    }

    [RelayCommand]
    private async Task AddSubscriptionAsync()
    {
        var url = (NewSubUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url)) return;

        var name = (NewSubName ?? "").Trim();
        if (string.IsNullOrEmpty(name)) name = $"Sub {Subscriptions.Count + 1}";

        var entry = new SubscriptionEntry { Name = name, Url = url, Enabled = true };
        _settings.App.Subscriptions.Add(entry);
        var svm = new SubscriptionViewModel(entry);
        Subscriptions.Add(svm);

        NewSubName = string.Empty;
        NewSubUrl = string.Empty;

        // Auto-switch to Subscribe tab so user sees the newly added subscription
        // and its fetched servers. Without this, if user adds subscription while
        // on Manual (VLESS) tab, the result happens "behind the scenes" and user
        // has no visual confirmation, leading to the "invisible add" perception.
        if (!IsSubscribeMode)
        {
            IsSubscribeMode = true;
            IsVlessMode = false;
            SelectedTabIndex = 1; // Subscribe tab
        }

        // Immediately refresh this new subscription.
        // RefreshSubscriptionAsync now has fail-safe RebuildSubscriptionPool + SaveSettings
        // in its finally block, so even if fetch fails (bad URL, network), the UI
        // shows the subscription entry (just without servers) instead of appearing
        // to have done nothing.
        await RefreshSubscriptionAsync(svm);

        // Belt-and-suspenders: rebuild one more time after Refresh in case
        // Refresh's finally was short-circuited by some future exception path.
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

        sub.IsRefreshing = true;
        try
        {
            var count = await SubscriptionFetcher.RefreshEntryAsync(
                sub.UnderlyingEntry, _logger, CancellationToken.None);
            // r7: count==0 = fetch failed/empty. RefreshEntryAsync KEEPS the
            // cached servers, so show the real cache count + flag the failure
            // (honest "couldn't refresh — showing cached" badge) instead of
            // dropping the card to "0s" (which read as "configs lost / banned").
            sub.LastRefreshFailed = count == 0;
            sub.LastServerCount = count > 0 ? count : (sub.UnderlyingEntry.Servers?.Count ?? 0);
            sub.LastRefreshedAt = sub.UnderlyingEntry.LastRefreshedAt;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] RefreshSubscription failed for {Url}", sub.Url);
            sub.LastRefreshFailed = true;
            sub.LastServerCount = sub.UnderlyingEntry.Servers?.Count ?? 0;
        }
        finally
        {
            sub.IsRefreshing = false;
            // Always rebuild + save, even on exception. Previously these only ran
            // on the happy path, so a fetch failure left the UI with zero servers
            // visible for the new subscription — user thought nothing happened.
            // Now: failure means "sub entry exists, no servers" rather than
            // "sub entry exists but UI still shows old state".
            RebuildSubscriptionPool();
            SaveSettings();
        }
    }

    [RelayCommand]
    private async Task RefreshAllSubscriptionsAsync()
    {
        var enabled = Subscriptions.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (enabled.Count == 0) return;

        foreach (var s in enabled) s.IsRefreshing = true;
        try
        {
            await Task.WhenAll(enabled.Select(async s =>
            {
                try
                {
                    var count = await SubscriptionFetcher.RefreshEntryAsync(
                        s.UnderlyingEntry, _logger, CancellationToken.None);
                    s.LastRefreshFailed = count == 0;       // r7: cache kept on empty
                    s.LastServerCount = count > 0 ? count : (s.UnderlyingEntry.Servers?.Count ?? 0);
                    s.LastRefreshedAt = s.UnderlyingEntry.LastRefreshedAt;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[VM] Refresh of {Url} failed", s.Url);
                    s.LastRefreshFailed = true;
                    s.LastServerCount = s.UnderlyingEntry.Servers?.Count ?? 0;
                }
            }));
        }
        finally
        {
            foreach (var s in enabled) s.IsRefreshing = false;
            // Rebuild + save in finally so even if Task.WhenAll itself throws
            // (shouldn't normally — inner try/catch per entry — but defensive),
            // the UI still reflects any entries that did complete successfully.
            RebuildSubscriptionPool();
            SaveSettings();
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

            // Replace subscription servers list
            SubscriptionServers.Clear();
            foreach (var entry in entries)
                SubscriptionServers.Add(new ServerViewModel(entry));
            ServerViewModel.RefreshUdpSiblingFlags(SubscriptionServers); // r8 #6

            // Select first server as active
            SelectedSubscriptionServer = SubscriptionServers.FirstOrDefault();
            SaveSettings();
            StatusText = Strings.SyncComplete(entries.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] Subscription sync failed");
            StatusText = Strings.SyncFailed(ex.Message);
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

    // ── Subscription auto-refresh ──

    /// <summary>Start periodic subscription refresh (when VPN connected in subscribe mode).</summary>
    private void StartSubRefreshTimer()
    {
        StopSubRefreshTimer();
        if (!IsSubscribeMode) return;
        // v2.31.0-r3 (VM-1): multi-sub model uses Subscriptions[] — pre-fix
        // condition only checked the legacy single SubscriptionUrl field, so
        // users who had migrated to the multi-sub UI never got auto-refresh
        // even when they had multiple working subs. Accept either source.
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

    /// <summary>Stop the subscription refresh timer.</summary>
    private void StopSubRefreshTimer()
    {
        _subRefreshTimer?.Dispose();
        _subRefreshTimer = null;
    }

    /// <summary>
    /// Silent subscription refresh — fetches new servers, compares UUIDs,
    /// and reconnects if they changed (e.g. server rotated UUID).
    /// </summary>
    private async Task RefreshSubscriptionSilentAsync()
    {
        if (!IsConnected || !IsSubscribeMode) return;

        var enabled = Subscriptions.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
        if (enabled.Count == 0) return;

        // Cancel previous refresh if still running (prevents concurrent fetches on slow network)
        _subRefreshCts?.Cancel();
        _subRefreshCts = new CancellationTokenSource();
        var ct = _subRefreshCts.Token;

        try
        {
            _logger.Information("[SubRefresh] Checking {Count} subscription(s)...", enabled.Count);

            // Snapshot current aggregated UUIDs
            var beforeUuids = SubscriptionServers.Select(s => s.Uuid).OrderBy(u => u).ToList();

            // Parallel refresh, ignore per-entry failures
            await Task.WhenAll(enabled.Select(async s =>
            {
                try
                {
                    var count = await SubscriptionFetcher.RefreshEntryAsync(s.UnderlyingEntry, _logger, ct);
                    s.LastRefreshFailed = count == 0;       // r7: cache kept on empty
                    s.LastServerCount = count > 0 ? count : (s.UnderlyingEntry.Servers?.Count ?? 0);
                    s.LastRefreshedAt = s.UnderlyingEntry.LastRefreshedAt;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[SubRefresh] Failed for {Url}", s.Url);
                    s.LastRefreshFailed = true;
                    s.LastServerCount = s.UnderlyingEntry.Servers?.Count ?? 0;
                }
            }));

            if (ct.IsCancellationRequested) return;

            // v2.31.8-r3: compare against UnderlyingEntry.Servers BEFORE
            // RebuildSubscriptionPool — the previous code rebuilt the
            // SubscriptionServers ObservableCollection unconditionally on
            // every hourly refresh. Clear() drops the
            // SelectedSubscriptionServer reference, then the
            // "restore selection" line at the end of
            // RebuildSubscriptionPool re-assigns SelectedSubscriptionServer
            // to a NEW ServerViewModel instance. The setter fires
            // OnSelectedSubscriptionServerChanged, which (when IsConnected
            // && IsSubscribeMode) triggers ReconnectAsync — full sing-box
            // stop+start cycle, ~3-4 s VPN downtime EVERY HOUR even when
            // the server set didn't change.
            //
            // Caught in brat-2026-05-05 logs: every hourly SubRefresh tick
            // disconnected/reconnected the tunnel even though the
            // "No UUID changes, no reconnect needed" log line was emitted
            // right after. Symptom user-reported: long-running TCP
            // connections (e.g. claude.exe → Anthropic API) failed during
            // each window, sometimes returning 403 because traffic
            // briefly fell back to direct route during the gap.
            //
            // Fix: compute afterUuids from UnderlyingEntry.Servers (the
            // model layer that the fetch actually populated), make the
            // change decision FIRST, and only call RebuildSubscriptionPool
            // when something actually changed. SaveSettings still runs in
            // both branches so the refreshed LastRefreshedAt timestamp
            // lands in YAML.
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

            _logger.Information("[SubRefresh] Servers changed, rebuilding pool and reconnecting...");
            RebuildSubscriptionPool();
            var reconnectName = SelectedSubscriptionServer?.Name ?? "subscription";
            await ReconnectAsync(reconnectName);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[SubRefresh] Auto-refresh failed");
        }
    }
}
