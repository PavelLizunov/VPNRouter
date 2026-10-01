#if VPNROUTER_TESTHOOK
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Android.App;
using Android.Content;
using Android.Net;
using Android.Util;
using Avalonia.Threading;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

[BroadcastReceiver(Name = "com.ninitux.vpnrouter.TestHookReceiver", Exported = true, Enabled = true)]
[IntentFilter(new[]
{
    "com.ninitux.vpnrouter.TEST_DUMP_STATE",
    "com.ninitux.vpnrouter.TEST_SET_CONFIG",
    "com.ninitux.vpnrouter.TEST_CONNECT",
    "com.ninitux.vpnrouter.TEST_DISCONNECT",
    "com.ninitux.vpnrouter.TEST_RESET",
})]
public sealed class TestHookReceiver : BroadcastReceiver
{
    internal const string LogTag = "VpnRouterTest";

    private const string ActDumpState = "com.ninitux.vpnrouter.TEST_DUMP_STATE";
    private const string ActSetConfig = "com.ninitux.vpnrouter.TEST_SET_CONFIG";
    private const string ActConnect = "com.ninitux.vpnrouter.TEST_CONNECT";
    private const string ActDisconnect = "com.ninitux.vpnrouter.TEST_DISCONNECT";
    private const string ActReset = "com.ninitux.vpnrouter.TEST_RESET";

    public override void OnReceive(Context? context, Intent? intent)
    {
        var action = intent?.Action;
        if (context is null || intent is null || string.IsNullOrEmpty(action)) return;

        string json;
        try
        {
            json = action switch
            {
                ActDumpState => DumpState(context),
                ActSetConfig => OnUi(() => AndroidApp.TestHookSetConfig(intent.GetStringExtra("value"))),
                ActConnect => OnUi(AndroidApp.TestHookConnect),
                ActDisconnect => OnUi(AndroidApp.TestHookDisconnect),
                ActReset => OnUi(AndroidApp.TestHookReset),
                _ => TestHookJson.Result(action, false, w => w.WriteString("error", "unknown-action")),
            };
        }
        catch (Exception ex)
        {
            json = TestHookJson.Result(action, false, w => w.WriteString("error", ex.GetType().Name));
        }

        Log.Info(LogTag, json);
        if (IsOrderedBroadcast) ResultData = json;
    }

    private static string OnUi(Func<string> work) =>
        Dispatcher.UIThread.CheckAccess() ? work() : Dispatcher.UIThread.Invoke(work);

    private static string DumpState(Context context)
    {
        var snapshot = AndroidStorage.ResolveVpnState();
        var prefs = context.GetSharedPreferences("vpnrouter_settings", FileCreationMode.Private);
        var servers = AndroidStorage.GetServers();
        var subscriptions = AndroidStorage.GetSubscriptions();
        var subscriptionServers = 0;
        foreach (var sub in subscriptions) subscriptionServers += sub?.Servers?.Count ?? 0;
        var active = AndroidStorage.GetActiveServer();

        return TestHookJson.Result(ActDumpState, true, w =>
        {
            w.WriteString("version", VPNRouter.Core.AppVersion.Version);
            w.WriteNumber("sdk", (int)global::Android.OS.Build.VERSION.SdkInt);
            w.WriteString("processArch", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
            w.WriteStartArray("abis");
            foreach (var abi in global::Android.OS.Build.SupportedAbis ?? Array.Empty<string>()) w.WriteStringValue(abi);
            w.WriteEndArray();

            w.WriteStartObject("vpn");
            w.WriteString("state", snapshot.State.ToString().ToLowerInvariant());
            w.WriteString("reason", TestHookJson.Scrub(snapshot.Reason));
            w.WriteNumber("ownerPid", snapshot.OwnerPid);
            w.WriteNumber("myPid", global::Android.OS.Process.MyPid());
            w.WriteStartObject("raw");
            w.WriteString("state", prefs?.GetString("vpn_state", null));
            w.WriteString("reason", TestHookJson.Scrub(prefs?.GetString("vpn_state_reason", null)));
            w.WriteNumber("pid", prefs?.GetInt("vpn_state_pid", 0) ?? 0);
            w.WriteNumber("atMs", prefs?.GetLong("vpn_state_at_ms", 0) ?? 0);
            w.WriteEndObject();
            w.WriteBoolean("tunnelLive", AndroidStorage.GetTunnelLive());
            w.WriteBoolean("intendedConnected", MainActivity.IntendedConnected);
            w.WriteBoolean("consentGranted", VpnService.Prepare(context) is null);
            w.WriteEndObject();

            w.WriteStartObject("config");
            w.WriteString("mode", AndroidStorage.GetConfigMode());
            w.WriteBoolean("hasShareLink", !string.IsNullOrWhiteSpace(AndroidStorage.GetVlessUri()));
            w.WriteBoolean("hasSubscriptionUrl", !string.IsNullOrWhiteSpace(AndroidStorage.GetSubscriptionUrl()));
            w.WriteBoolean("hasCustomJson", !string.IsNullOrWhiteSpace(AndroidStorage.GetCustomConfigJson()));
            w.WriteNumber("servers", servers.Count);
            w.WriteNumber("subscriptions", subscriptions.Count);
            w.WriteNumber("subscriptionServers", subscriptionServers);
            w.WriteString("activeServer", TestHookJson.Scrub(active?.Name));
            w.WriteBoolean("hasLastGood", !string.IsNullOrEmpty(prefs?.GetString("last_good_config_json", null)));
            w.WriteEndObject();

            w.WriteStartObject("app");
            w.WriteString("perAppMode", AndroidStorage.GetPerAppMode());
            w.WriteNumber("perAppPackages", AndroidStorage.GetPerAppPackages().Count);
            w.WriteBoolean("externalControl", AndroidStorage.GetExternalControlEnabled());
            w.WriteBoolean("uiReady", AndroidApp.TestHookUiReady());
            w.WriteEndObject();

            if (Dispatcher.UIThread.CheckAccess()) AndroidApp.TestHookWriteLayout(w);
            else
            {
                var buffer = new MemoryStream();
                Dispatcher.UIThread.Invoke(() =>
                {
                    using var inner = new Utf8JsonWriter(buffer);
                    inner.WriteStartObject();
                    AndroidApp.TestHookWriteLayout(inner);
                    inner.WriteEndObject();
                });
                using var doc = System.Text.Json.JsonDocument.Parse(buffer.ToArray());
                w.WritePropertyName("layout");
                doc.RootElement.GetProperty("layout").WriteTo(w);
            }
        });
    }
}

internal static class TestHookJson
{
    private static readonly Regex UrlLike = new(@"[A-Za-z][A-Za-z0-9+.\-]*://\S+", RegexOptions.Compiled);
    private static readonly Regex UuidLike = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
    private static readonly Regex LongToken = new(@"[A-Za-z0-9_\-]{24,}", RegexOptions.Compiled);

    /// <summary>Removes URLs, UUIDs and long token-like runs so a log line never carries a credential.</summary>
    internal static string? Scrub(string? value, int maxLength = 160)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var text = UrlLike.Replace(value, "<url>");
        text = UuidLike.Replace(text, "<id>");
        text = LongToken.Replace(text, "<token>");
        return text.Length > maxLength ? text.Substring(0, maxLength) : text;
    }

    internal static string Result(string action, bool ok, Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("action", action.Substring(action.LastIndexOf('.') + 1));
            w.WriteBoolean("ok", ok);
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
#endif
