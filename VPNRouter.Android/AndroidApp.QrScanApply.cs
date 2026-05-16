// AndroidApp.QrScanApply.cs — Bug-AND-023 v3 magic 1-step apply.
//
// User feedback after v2 ship (commit 594051c): "Нам нужна магия 1-действия —
// условно я сканю и сразу всё добавляется и соединяется". Pre-v3 the user
// had to scan → type a name → tap Add → tap Connect. Four taps for what
// should be zero. v3 routes by URI scheme:
//
//   vless:// / hy2:// / hysteria2:// / tuic:// / ss:// →
//        parse, dedupe by host:port:uuid against the saved Servers list,
//        add if new with a collision-safe display name, set as the
//        active server, close the Advanced shell if open, fire Connect.
//
//   http:// / https:// →
//        treat as a subscription URL. Auto-name from the URL host (so
//        the user never has to type one), dedupe against existing
//        subscriptions, refresh-fetch the server list, dedupe each
//        fetched server against the saved Servers list, set the FIRST
//        fetched server as active, close the Advanced shell if open,
//        fire Connect.
//
//   anything else →
//        toast "QR doesn't contain vless:// or a subscription URL"; do
//        nothing else. The user can still tap the camera again or paste
//        a URL by hand.
//
// Pattern crib: mirrors VPNRouter.App ViewModels' "scan, dedupe, persist,
// connect" flow + the existing OnFreeConfigsUseClicked (Bug-AND-021)
// dedupe-into-Servers logic. The subscription branch is new on Android —
// desktop's equivalent is wired through SubscribeViewModel.AddAsync but
// without auto-Connect (desktop expects the user to pick a server).

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    /// <summary>
    /// Tap-and-go flow invoked from both <c>OnSimpleQrScanClicked</c>
    /// (AndroidApp.axaml.cs) and <c>OnSubscribeQrScanClicked</c>
    /// (AndroidApp.SubscribePage.cs) after a successful decode. Already on
    /// the UI thread (the QR result callback marshals via
    /// Dispatcher.UIThread.Post).
    /// </summary>
    private async Task ApplyScannedTextAsync(string? scanned)
    {
        if (string.IsNullOrWhiteSpace(scanned)) return;
        var trimmed = scanned.Trim();
        var lowered = trimmed.ToLowerInvariant();

        if (lowered.StartsWith("vless://") ||
            lowered.StartsWith("hy2://") ||
            lowered.StartsWith("hysteria2://") ||
            lowered.StartsWith("tuic://") ||
            lowered.StartsWith("ss://") ||
            lowered.StartsWith("ssr://") ||
            lowered.StartsWith("vmess://"))
        {
            ApplyScannedServerUri(trimmed);
            return;
        }

        if (lowered.StartsWith("http://") || lowered.StartsWith("https://"))
        {
            await ApplyScannedSubscriptionUrlAsync(trimmed);
            return;
        }

        ShowMenuFeedback(Localization.SmpQrUnsupportedScheme);
    }

    /// <summary>
    /// vless:// (and friends) branch. Mirrors OnFreeConfigsUseClicked
    /// (Bug-AND-021) so the QR-scanned server lands in the same place as a
    /// Free-Configs "Use" tap — visible in Advanced → Servers, selected as
    /// the active server, and persisted via SetServers so the next launch
    /// still has it.
    /// </summary>
    private void ApplyScannedServerUri(string uri)
    {
        VlessServerEntry parsed;
        try
        {
            parsed = ServerUriParser.Parse(uri);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.QrScan",
                $"ApplyScannedServerUri: parse failed — {ex.GetType().Name}: {ex.Message}");
            ShowMenuFeedback(Localization.SmpQrUnsupportedScheme);
            return;
        }

        try
        {
            var servers = AndroidStorage.GetServers() ?? new List<VlessServerEntry>();
            // Match by host:port:uuid; reuse the existing user-curated
            // label if a row already exists (don't surprise the user with
            // a renamed row just because they re-scanned the same QR).
            var existing = servers.FirstOrDefault(s =>
                string.Equals(s.Server, parsed.Server, StringComparison.OrdinalIgnoreCase) &&
                s.Port == parsed.Port &&
                string.Equals(s.Uuid, parsed.Uuid, StringComparison.OrdinalIgnoreCase));

            string activeName;
            if (existing is not null)
            {
                activeName = existing.Name ?? parsed.Server ?? string.Empty;
            }
            else
            {
                var baseName = string.IsNullOrWhiteSpace(parsed.Name) ? "QR" : parsed.Name!;
                var displayName = baseName;
                int suffix = 2;
                while (servers.Any(s => string.Equals(s.Name, displayName, StringComparison.OrdinalIgnoreCase)))
                    displayName = $"{baseName} #{suffix++}";
                parsed.Name = displayName;
                servers.Add(parsed);
                AndroidStorage.SetServers(servers);
                activeName = displayName;
            }
            AndroidStorage.SetSelectedServerName(activeName);
            // Manual-config legacy fallback + clear any subscription URL so
            // GetActiveServer takes the Servers-list path (Bug-AND-021
            // commentary explains the priority order).
            AndroidStorage.SetVlessUri(uri);
            AndroidStorage.SetSubscriptionUrl(null);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.QrScan",
                $"ApplyScannedServerUri: persist failed — {ex.GetType().Name}: {ex.Message}");
            // Fall through — still try to Connect. The legacy SetVlessUri
            // path inside MainActivity.StartTunnelService can still resolve
            // the URI as a manual server.
            try
            {
                AndroidStorage.SetVlessUri(uri);
                AndroidStorage.SetSubscriptionUrl(null);
            }
            catch { /* secondary best effort */ }
        }

        ShowMenuFeedback(Localization.SmpQrConnecting);
        CloseAdvancedShell();
        UpdateConfigSummary();

        // One render frame between shell-close animation and the system
        // VPN consent dialog (first launch only) — same UX trick as
        // OnFreeConfigsUseClicked.
        Dispatcher.UIThread.Post(() =>
        {
            MainActivity.Instance?.RequestConnect();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// http(s):// branch. Auto-name the subscription from the URL host
    /// (e.g. "ninitux.com"), refresh in background, then dedupe each
    /// fetched server into the saved Servers list and Connect via the
    /// first fetched server. If the refresh returns 0 servers we still
    /// keep the subscription so a later manual refresh can recover, but
    /// we surface "subscription empty" toast and do NOT call Connect.
    /// </summary>
    private async Task ApplyScannedSubscriptionUrlAsync(string url)
    {
        var subs = _subs ?? new List<SubscriptionEntry>();

        // Dedupe by URL — if the user re-scans the same subscription QR,
        // we don't want a "Sub #2" duplicate row.
        var existing = subs.FirstOrDefault(s =>
            string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase));

        SubscriptionEntry entry;
        bool isNew;
        if (existing is not null)
        {
            entry = existing;
            isNew = false;
        }
        else
        {
            var name = ExtractDisplayNameFromUrl(url, fallback: $"Sub {subs.Count + 1}");
            entry = new SubscriptionEntry
            {
                Name = name,
                Url = url,
                Enabled = true,
            };
            subs.Add(entry);
            _subs = subs;
            AndroidStorage.SetSubscriptions(subs);
            isNew = true;
        }

        // Live refresh; mirror RefreshOneAsync but inline so we can read
        // entry.Servers out the moment the fetch completes.
        ShowMenuFeedback(Localization.SmpQrSubscriptionFetching);
        int fetchedCount;
        try
        {
            fetchedCount = await Task.Run(() =>
                SubscriptionFetcher.RefreshEntryAsync(entry, logger: null, ct: CancellationToken.None));
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.QrScan",
                $"ApplyScannedSubscriptionUrlAsync: refresh failed — {ex.GetType().Name}: {ex.Message}");
            ShowMenuFeedback(Localization.SmpQrSubscriptionFailed);
            return;
        }

        AndroidStorage.SetSubscriptions(subs);
        // Keep the Subscribe-page list in sync if it's mounted (RebuildSubsList
        // is in AndroidApp.SubscribePage.cs and no-ops if UI isn't built yet).
        RebuildSubsList();

        if (fetchedCount == 0 || entry.Servers == null || entry.Servers.Count == 0)
        {
            ShowMenuFeedback(Localization.SmpQrSubscriptionEmpty);
            return;
        }

        // Bug-AND-023 v4 (2026-05-17, user-reported "сервера подписки также
        // продублировались из страницы подписки на страницу сервер"): pre-v4
        // we merged entry.Servers into AndroidStorage.GetServers() so the
        // connect resolver could find them. That made every subscription
        // server appear on both Subscribe and Servers tabs.
        //
        // v4 leaves the subscription's Servers in-place (inside the
        // SubscriptionEntry only) and relies on the GetActiveServer
        // walking-subscriptions tier added in the same fix. SetSelectedServerName
        // is enough — GetActiveServer resolves it against the in-memory
        // sub.Servers list when the connect path runs.
        VlessServerEntry firstServer;
        try
        {
            firstServer = entry.Servers[0];
            AndroidStorage.SetSelectedServerName(firstServer.Name);
            AndroidStorage.SetSubscriptionUrl(url);
            AndroidStorage.SetVlessUri(null);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.QrScan",
                $"ApplyScannedSubscriptionUrlAsync: persist failed — {ex.GetType().Name}: {ex.Message}");
            ShowMenuFeedback(Localization.SmpQrSubscriptionFailed);
            return;
        }

        ShowMenuFeedback(Localization.SmpQrConnecting);
        CloseAdvancedShell();
        UpdateConfigSummary();

        Dispatcher.UIThread.Post(() =>
        {
            MainActivity.Instance?.RequestConnect();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// "https://provider.example/path/abc" → "provider.example".
    /// Returns <paramref name="fallback"/> when the URL is malformed or
    /// the host is empty (e.g. relative path masquerading as a URL).
    /// </summary>
    private static string ExtractDisplayNameFromUrl(string url, string fallback)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            {
                var host = u.Host;
                if (!string.IsNullOrWhiteSpace(host)) return host;
            }
        }
        catch { /* fall through */ }
        return fallback;
    }
}
