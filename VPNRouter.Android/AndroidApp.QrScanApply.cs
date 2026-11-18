#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    private async Task ApplyScannedTextAsync(string? scanned, string command)
    {
        if (!AndroidStorage.IsCurrentUiCommand(command) || string.IsNullOrWhiteSpace(scanned)) return;
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
            ApplyScannedServerUri(trimmed, command);
            return;
        }

        if (lowered.StartsWith("http://") || lowered.StartsWith("https://"))
        {
            await ApplyScannedSubscriptionUrlAsync(trimmed, command);
            return;
        }

        ShowMenuFeedback(Localization.SmpQrUnsupportedScheme);
    }

    private void ApplyScannedServerUri(string uri, string command)
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
            MainActivity.Instance?.RequestConnect(command);
        }, DispatcherPriority.Background);
    }

    private async Task ApplyScannedSubscriptionUrlAsync(string url, string? reserved = null)
    {
        var command = reserved ?? AndroidStorage.BeginUiCommand();
        if (command == null || !AndroidStorage.IsCurrentUiCommand(command)) return;
        var subs = AndroidStorage.GetSubscriptions();
        _subs = subs;

        var entry = subs.FirstOrDefault(s => string.Equals(s.Url, url, StringComparison.Ordinal));
        if (entry is null)
        {
            var name = ExtractDisplayNameFromUrl(url, fallback: $"Sub {subs.Count + 1}");
            entry = new SubscriptionEntry
            {
                Name = name,
                Url = url,
                Enabled = false,
            };
            subs.Add(entry);
            if (!AndroidStorage.SetSubscriptions(subs))
            {
                _subs = AndroidStorage.GetSubscriptions();
                ShowMenuFeedback(Localization.SmpQrSubscriptionFailed);
                return;
            }
            RebuildSubsList();
        }

        ShowMenuFeedback(Localization.SmpQrSubscriptionFetching);
        int fetchedCount;
        try
        {
            fetchedCount = await RefreshStoredSubscriptionAsync(entry, command);
        }
        catch (UnsupportedSubscriptionChainException ex)
        {
            if (AndroidStorage.IsCurrentUiCommand(command)) ShowMenuFeedback(ex.Message);
            return;
        }
        catch (Exception)
        {
            global::Android.Util.Log.Warn("VpnRouter.QrScan",
                "Subscription refresh or save failed");
            if (AndroidStorage.IsCurrentUiCommand(command)) ShowMenuFeedback(Localization.SmpQrSubscriptionFailed);
            return;
        }

        if (!AndroidStorage.IsCurrentUiCommand(command)) return;
        if (fetchedCount == 0)
        {
            ShowMenuFeedback(Localization.SmpQrSubscriptionEmpty);
            return;
        }

        _ccMode = "subscribe";
        ApplyCcModeVisuals();
        ShowMenuFeedback(Localization.SmpQrConnecting);
        CloseAdvancedShell();
        UpdateConfigSummary();

        Dispatcher.UIThread.Post(() =>
        {
            MainActivity.Instance?.RequestConnect(command);
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
