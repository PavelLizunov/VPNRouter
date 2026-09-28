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
    private async Task ApplyScannedTextAsync(string? scanned)
    {
        if (string.IsNullOrWhiteSpace(scanned)) return;
        var trimmed = scanned.Trim();
        var lowered = trimmed.ToLowerInvariant();

        if (lowered.StartsWith("naive://") ||
            lowered.StartsWith("naive+https://") ||
            lowered.StartsWith("naive+quic://"))
        {
            ShowMenuFeedback(Localization.SmpQrNaiveUnsupportedAndroid);
            return;
        }

        if (lowered.StartsWith("vless://") ||
            lowered.StartsWith("hy2://") ||
            lowered.StartsWith("hysteria2://") ||
            lowered.StartsWith("tuic://") ||
            lowered.StartsWith("ss://"))
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

    private void ApplyScannedServerUri(string uri)
    {
        VlessServerEntry parsed;
        try
        {
            parsed = ServerUriParser.Parse(uri);
        }
        catch (PlaceholderConfigException ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.QrScan",
                $"ApplyScannedServerUri: placeholder rejected — field={ex.OffendingField} value={ex.OffendingValue}");
            ShowMenuFeedback(Localization.PlaceholderCredentialRejected);
            return;
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
            AndroidStorage.SetVlessUri(uri);
            AndroidStorage.SetSubscriptionUrl(null);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.QrScan",
                $"ApplyScannedServerUri: persist failed — {ex.GetType().Name}: {ex.Message}");
            try
            {
                AndroidStorage.SetVlessUri(uri);
                AndroidStorage.SetSubscriptionUrl(null);
            }
            catch { }
        }

        ShowMenuFeedback(Localization.SmpQrConnecting);
        CloseAdvancedShell();
        UpdateConfigSummary();

        Dispatcher.UIThread.Post(() =>
        {
            MainActivity.Instance?.RequestConnect();
        }, DispatcherPriority.Background);
    }

    private async Task ApplyScannedSubscriptionUrlAsync(string url)
    {
        var subs = AndroidStorage.GetSubscriptions();
        _subs = subs;

        var existing = subs.FirstOrDefault(s =>
            string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase));

        SubscriptionEntry entry;
        bool isNew;
        if (existing is not null)
        {
            entry = existing;
            entry.Enabled = true;
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
            AndroidStorage.SetSubscriptions(subs);
            isNew = true;
        }

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
        RebuildSubsList();

        if (fetchedCount == 0 || entry.Servers == null || entry.Servers.Count == 0)
        {
            ShowMenuFeedback(Localization.SmpQrSubscriptionEmpty);
            return;
        }

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
        catch { }
        return fallback;
    }
}
