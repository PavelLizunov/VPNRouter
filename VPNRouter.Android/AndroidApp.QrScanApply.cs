#nullable enable

using System;
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
            lowered.StartsWith("vmess://") ||
            lowered.StartsWith("hy2://") ||
            lowered.StartsWith("hysteria2://") ||
            lowered.StartsWith("tuic://") ||
            lowered.StartsWith("ss://") ||
            NativeTransportUriParser.HasScheme(trimmed))
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
            if (!AndroidStorage.SelectSingleServer(parsed, uri, command)) return;
        }
        catch (Exception)
        {
            ShowMenuFeedback(Localization.FcApplyFailed);
            return;
        }

        RefreshConfigModeUiFromStorage();
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
        var command = reserved ?? ReserveConnectCommand();
        if (command == null || !AndroidStorage.IsCurrentUiCommand(command)) return;
        var subs = AndroidStorage.GetSubscriptions();
        _subs = subs;

        var selectedId = AndroidStorage.SelectedSubscriptionId;
        var entry = subs.FirstOrDefault(s => s != null && s.Url == url && s.Id == selectedId)
            ?? subs.FirstOrDefault(s => s != null && s.Url == url);
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
        RefreshConfigModeUiFromStorage();
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
