#if VPNROUTER_TESTHOOK
using System;
using System.Collections.Generic;
using Android.Content;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

/// <summary>
/// UI-side actions of <see cref="TestHookReceiver"/>. They reuse the handlers behind the real buttons so a hook call
/// leaves the same state as a user action. Compiled only into builds made with -p:VpnRouterTestHook=true.
/// </summary>
public partial class AndroidApp
{
    private static AndroidApp? TestHookApp => Avalonia.Application.Current as AndroidApp;

    internal static bool TestHookUiReady() => TestHookApp?._serverInput is not null && MainActivity.Instance is not null;

    /// <summary>Window and layout numbers for the inset checks (read-only, no secrets).</summary>
    internal static void TestHookWriteLayout(System.Text.Json.Utf8JsonWriter w)
    {
        w.WriteStartObject("layout");
        try
        {
            var app = TestHookApp;
            var scroller = app?._mainScroller;
            if (app is null || scroller is null)
            {
                w.WriteString("error", "ui-not-ready");
            }
            else
            {
                static string T(Avalonia.Thickness t) => $"{t.Left:0.#},{t.Top:0.#},{t.Right:0.#},{t.Bottom:0.#}";
                static string R(Avalonia.Rect r) => $"x{r.X:0.#} y{r.Y:0.#} w{r.Width:0.#} h{r.Height:0.#}";
                w.WriteString("activitySafeArea", T(MainActivity.CurrentSafeArea));
                w.WriteString("appliedSafeArea", T(app._currentSafeArea));
                w.WriteString("scrollerPadding", T(scroller.Padding));
                w.WriteString("scrollerBounds", R(scroller.Bounds));
                var top = Avalonia.Controls.TopLevel.GetTopLevel(scroller);
                if (top is not null)
                {
                    w.WriteString("topLevelSize", $"{top.Bounds.Width:0.#}x{top.Bounds.Height:0.#}");
                    w.WriteNumber("renderScaling", top.RenderScaling);
                    var mgr = top.InsetsManager;
                    if (mgr is not null)
                    {
                        w.WriteString("avaloniaSafeArea", T(mgr.SafeAreaPadding));
                        foreach (var name in new[] { "DisplayEdgeToEdgePreference", "DisplayEdgeToEdge" })
                        {
                            var prop = mgr.GetType().GetProperty(name);
                            if (prop is not null) w.WriteString(name, prop.GetValue(mgr)?.ToString());
                        }
                    }
                }
                if (scroller.Content is Avalonia.Controls.Control content)
                {
                    w.WriteString("contentBounds", R(content.Bounds));
                    if (content is Avalonia.Controls.Panel panel && panel.Children.Count > 0)
                        w.WriteString("wrapperBounds", R(panel.Children[0].Bounds));
                }
                var metrics = MainActivity.Instance?.Resources?.DisplayMetrics;
                if (metrics is not null) w.WriteNumber("density", metrics.Density);
            }
        }
        catch (Exception ex)
        {
            w.WriteString("error", ex.GetType().Name);
        }
        w.WriteEndObject();
    }

    internal static string TestHookSetConfig(string? value)
    {
        const string action = "TEST_SET_CONFIG";
        var app = TestHookApp;
        if (app?._serverInput is null)
            return TestHookJson.Result(action, false, w => w.WriteString("error", "ui-not-ready"));

        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0)
            return TestHookJson.Result(action, false, w => w.WriteString("error", "empty-value"));

        if (raw.StartsWith("{", StringComparison.Ordinal))
        {
            var (isValid, errors) = CustomConfigInjector.Validate(raw);
            AndroidStorage.SetCustomConfigJson(raw);
            AndroidStorage.SetConfigMode("custom");
            app._ccMode = "custom";
            app.ApplyCcModeVisuals();
            app.UpdateConfigSummary();
            return TestHookJson.Result(action, true, w =>
            {
                w.WriteString("kind", "custom-json");
                w.WriteString("mode", AndroidStorage.GetConfigMode());
                w.WriteBoolean("valid", isValid);
                w.WriteNumber("validationErrors", errors.Count);
            });
        }

        string kind;
        if (ServerUriParser.IsSupportedScheme(raw)) kind = "share-link";
        else if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) kind = "subscription-url";
        else
            return TestHookJson.Result(action, false, w => w.WriteString("error", "unsupported-value"));

        app._serverInput.Text = raw;
        app.OnSaveClicked(null, new Avalonia.Interactivity.RoutedEventArgs());
        var rejected = app._serverInputError?.IsVisible == true;
        return TestHookJson.Result(action, !rejected, w =>
        {
            w.WriteString("kind", kind);
            w.WriteString("mode", AndroidStorage.GetConfigMode());
            w.WriteBoolean("rejectedByInputCheck", rejected);
        });
    }

    internal static string TestHookConnect()
    {
        const string action = "TEST_CONNECT";
        var app = TestHookApp;
        if (app is null || MainActivity.Instance is null)
            return TestHookJson.Result(action, false, w => w.WriteString("error", "ui-not-ready"));
        if (MainActivity.IntendedConnected)
            return TestHookJson.Result(action, false, w => w.WriteString("error", "already-intended-connected"));

        app.OnConnectClicked(null, new Avalonia.Interactivity.RoutedEventArgs());
        return TestHookJson.Result(action, true, w => w.WriteString("note", "requested-through-the-connect-button-handler"));
    }

    internal static string TestHookDisconnect()
    {
        const string action = "TEST_DISCONNECT";
        var activity = MainActivity.Instance;
        if (activity is null)
            return TestHookJson.Result(action, false, w => w.WriteString("error", "activity-not-ready"));

        activity.RequestDisconnect();
        return TestHookJson.Result(action, true, w => w.WriteBoolean("wasIntendedConnected", MainActivity.IntendedConnected));
    }

    internal static string TestHookReset()
    {
        const string action = "TEST_RESET";
        var app = TestHookApp;
        if (MainActivity.IntendedConnected) MainActivity.Instance?.RequestDisconnect();

        AndroidStorage.SetVlessUri(null);
        AndroidStorage.SetSubscriptionUrl(null);
        AndroidStorage.SetServers(null);
        AndroidStorage.SetSelectedServerName(null);
        AndroidStorage.SetSubscriptions(null);
        AndroidStorage.SetCustomConfigJson(null);
        AndroidStorage.SetConfigMode("manual");

        var prefs = global::Android.App.Application.Context.GetSharedPreferences("vpnrouter_settings", FileCreationMode.Private);
        var editor = prefs?.Edit();
        if (editor is not null)
        {
            foreach (var key in new[]
            {
                "vpn_state", "vpn_state_reason", "vpn_state_pid", "vpn_state_at_ms",
                "last_good_config_json", "last_good_per_app_mode", "last_good_per_app_packages_lines",
                "last_good_dns_tunnel_domain", "last_good_dns_tunnel_resolvers_lines",
                "last_good_dns_tunnel_cert", "last_good_dns_tunnel_port",
            })
            {
                editor.Remove(key);
            }
            editor.PutBoolean("tunnel_live", false);
            editor.Commit();
        }

        if (app is not null)
        {
            app._ccMode = "manual";
            if (app._serverInput is not null) app._serverInput.Text = string.Empty;
            app._cachedServers = new List<VlessServerEntry>();
            app.UpdateServerListView();
            app.ApplyCcModeVisuals();
            app.UpdateConfigSummary();
        }

        return TestHookJson.Result(action, true, w => w.WriteBoolean("uiUpdated", app is not null));
    }
}
#endif
