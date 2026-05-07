using Android.App;
using Android.Content;
using System.Collections.Generic;
using Newtonsoft.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 Phase 1.H (2026-05-04) — extended persistence on top of the
/// Phase 1.F single-URI MVP. Adds:
/// <list type="bullet">
///   <item>Subscription URL (HTTP-fetched server pool)</item>
///   <item>Cached server list (JSON-serialised <see cref="VlessServerEntry"/>[])</item>
///   <item>Selected server identity (by Name)</item>
///   <item>Theme + language preference (mirrors desktop's <c>app.theme</c> /
///   <c>app.language</c>)</item>
/// </list>
///
/// Storage backend remains <c>SharedPreferences</c> for the same reasons as
/// 1.F — Android sandboxed FilesDir, atomic commits, no <c>YamlDotNet</c>
/// overhead. JSON for the server list because we already use Newtonsoft.Json
/// across <see cref="VPNRouter.Core"/>; YAML would require a Yaml→Yaml round
/// trip with Android's restricted reflection.
///
/// <para>Phase 2+: replace SharedPreferences entirely with <c>SettingsLoader</c>
/// pointed at <c>Application.FilesDir/config.yaml</c>. Then desktop and
/// Android share the same on-disk schema. Until then, this thin facade
/// keeps the keys in one place.</para>
/// </summary>
public static class AndroidStorage
{
    private const string PrefsName = "vpnrouter_settings";

    // Phase 1.F
    private const string KeyVlessUri = "vless_uri";
    // Phase 1.H
    private const string KeySubscriptionUrl = "subscription_url";
    private const string KeyServersJson = "servers_json";
    private const string KeySelectedServerName = "selected_server_name";
    private const string KeyLanguage = "language";   // "ru" | "en"
    private const string KeyTheme = "theme";         // "dark" | "light" | "system"
    // v2.32.0 (2026-05-07) — multi-subscription parity with desktop
    // SubscribePage. Persists List<SubscriptionEntry> from Core verbatim
    // so the model is shared with the desktop YAML schema (fields:
    // id/name/url/enabled/last_refreshed_at/last_server_count/servers).
    // Pre-2.32.0 only KeySubscriptionUrl existed (single URL); on first
    // read of KeySubscriptions we migrate the legacy single URL into a
    // one-entry list.
    private const string KeySubscriptions = "subscriptions_json";

    // ── Phase 1.F: single-URI manual mode ───────────────────────────────────

    public static string? GetVlessUri() => GetString(KeyVlessUri);
    public static bool SetVlessUri(string? value) => SetString(KeyVlessUri, value);

    // ── Phase 1.H: subscription mode ────────────────────────────────────────

    public static string? GetSubscriptionUrl() => GetString(KeySubscriptionUrl);
    public static bool SetSubscriptionUrl(string? value) => SetString(KeySubscriptionUrl, value);

    // ── v2.32.0: multi-subscription list (desktop SubscribePage parity) ─

    /// <summary>
    /// Persisted list of <see cref="SubscriptionEntry"/> objects (one per
    /// subscription source). Mirrors desktop's <c>app.subscriptions[]</c>
    /// YAML array, using the same Core model so refresh/parse logic is
    /// shared.
    /// <para>If <c>subscriptions_json</c> is missing, falls back to the
    /// legacy single <c>KeySubscriptionUrl</c> from Phase 1.H — wraps it
    /// in a one-entry list called "Default" so old installs auto-migrate
    /// on first open of the new SubscribePage. Migration is read-only;
    /// the next <see cref="SetSubscriptions"/> persists the new schema.</para>
    /// </summary>
    public static List<SubscriptionEntry> GetSubscriptions()
    {
        try
        {
            var json = GetString(KeySubscriptions);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var list = JsonConvert.DeserializeObject<List<SubscriptionEntry>>(json);
                if (list != null) return list;
            }

            var legacy = GetString(KeySubscriptionUrl);
            if (!string.IsNullOrWhiteSpace(legacy))
            {
                return new List<SubscriptionEntry>
                {
                    new SubscriptionEntry
                    {
                        Name = "Default",
                        Url = legacy,
                        Enabled = true,
                        Servers = GetServers(),
                    }
                };
            }
        }
        catch
        {
            // fall through to empty
        }
        return new List<SubscriptionEntry>();
    }

    /// <summary>
    /// Replace the list of subscriptions atomically. Pass null/empty to
    /// clear. Also flushes the aggregated <see cref="GetServers"/> pool
    /// (union of all entries' Servers, dedup by Server:Port:Uuid:Flow)
    /// so the connect path keeps working without a separate rebuild
    /// step.
    /// </summary>
    public static bool SetSubscriptions(IEnumerable<SubscriptionEntry>? subs)
    {
        try
        {
            var list = subs is null ? new List<SubscriptionEntry>() : new List<SubscriptionEntry>(subs);
            var json = JsonConvert.SerializeObject(list);
            var ok = SetString(KeySubscriptions, json);

            // Rebuild aggregated server pool — dedup matches desktop
            // VlessServersResolver.Resolve key shape (Server:Port:Uuid:Flow).
            var pool = new List<VlessServerEntry>();
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var s in list)
            {
                if (!s.Enabled || s.Servers == null) continue;
                foreach (var srv in s.Servers)
                {
                    var key = $"{srv.Server}:{srv.Port}:{srv.Uuid}:{srv.Flow}";
                    if (seen.Add(key)) pool.Add(srv);
                }
            }
            SetServers(pool);
            return ok;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Persisted list of servers from the last successful subscription fetch.
    /// Returns empty list if no fetch has happened yet or the cache is
    /// unparseable.
    /// </summary>
    public static List<VlessServerEntry> GetServers()
    {
        try
        {
            var json = GetString(KeyServersJson);
            if (string.IsNullOrWhiteSpace(json)) return new List<VlessServerEntry>();
            var list = JsonConvert.DeserializeObject<List<VlessServerEntry>>(json);
            return list ?? new List<VlessServerEntry>();
        }
        catch
        {
            return new List<VlessServerEntry>();
        }
    }

    /// <summary>
    /// Replace the cached server list. Pass empty / null to clear. Atomic via
    /// SharedPreferences.commit (per-key, not per-batch).
    /// </summary>
    public static bool SetServers(IEnumerable<VlessServerEntry>? servers)
    {
        try
        {
            if (servers == null)
                return SetString(KeyServersJson, null);
            var list = new List<VlessServerEntry>(servers);
            var json = JsonConvert.SerializeObject(list);
            return SetString(KeyServersJson, json);
        }
        catch
        {
            return false;
        }
    }

    public static string? GetSelectedServerName() => GetString(KeySelectedServerName);
    public static bool SetSelectedServerName(string? value) => SetString(KeySelectedServerName, value);

    /// <summary>
    /// Resolve the active server entry — checks subscription server list
    /// first (if a selection exists), falls back to any single manual URI.
    /// Returns null if nothing's configured (caller falls back to a
    /// placeholder for smoke-test).
    /// </summary>
    public static VlessServerEntry? GetActiveServer()
    {
        var selectedName = GetSelectedServerName();
        if (!string.IsNullOrEmpty(selectedName))
        {
            var servers = GetServers();
            foreach (var s in servers)
            {
                if (string.Equals(s.Name, selectedName, System.StringComparison.OrdinalIgnoreCase))
                    return s;
            }
        }

        var manualUri = GetVlessUri();
        if (!string.IsNullOrWhiteSpace(manualUri))
        {
            try
            {
                // v3.0 Phase 6.4 (2026-05-04) — accept any supported scheme
                // (vless / hysteria2 / tuic / ss), not just vless. Mirrors
                // desktop simple-mode paste (Phase v2.30.1-r3+).
                return VPNRouter.Core.Services.ServerUriParser.Parse(manualUri);
            }
            catch
            {
                // Fall through — invalid stored URI shouldn't crash the app.
            }
        }

        return null;
    }

    // ── Phase 1.H: UI preferences ───────────────────────────────────────────

    /// <summary>
    /// "ru" / "en" — explicit user choice. Returns null when never set,
    /// caller falls back to system Locale.
    /// </summary>
    public static string? GetLanguage() => GetString(KeyLanguage);
    public static bool SetLanguage(string? value) => SetString(KeyLanguage, value);

    /// <summary>
    /// "dark" / "light" / "system" — explicit preference. Defaults to
    /// "light" because that's what desktop ships (Phase 3 visual parity
    /// rewrite — pre-3 default was dark, which made the Android UI look
    /// nothing like desktop on first launch).
    /// </summary>
    public static string GetTheme() => GetString(KeyTheme) ?? "light";
    public static bool SetTheme(string? value) => SetString(KeyTheme, value);

    // ── Phase 7.5 (2026-05-04): per-app filter (handbook §5.5) ──────────
    //
    // Mode values:
    //   "off"     → no per-app filter; whole VpnService routes via tunnel
    //              (matching desktop's "All traffic" tunnel mode).
    //   "include" → ONLY listed packages are routed via the tunnel; all
    //              others use the underlying network directly.
    //   "exclude" → listed packages BYPASS the tunnel (useful for banking
    //              apps that block VPN, or known-CDN apps that don't
    //              benefit from proxying).
    // The package list itself is a JSON-serialized List<string> so we can
    // round-trip through SharedPreferences without inventing a delimiter.
    private const string KeyPerAppMode = "per_app_mode";
    private const string KeyPerAppPackages = "per_app_packages";
    // v3.0 v2.32.0 (2026-05-07): when the user toggles the form's split
    // radio off (mode="off"), we still want to remember whether their
    // last active mode was "include" or "exclude" so toggling split back
    // on restores it instead of defaulting to "include". Without this,
    // an exclude-mode user who briefly switches to "All traffic" would
    // silently lose their exclude intent.
    private const string KeyPerAppLastMode = "per_app_last_mode";

    public static string GetPerAppMode() =>
        VPNRouter.Core.Models.PerAppFilterMode.Normalize(GetString(KeyPerAppMode));
    public static bool SetPerAppMode(string? value) => SetString(KeyPerAppMode, value);

    /// <summary>
    /// Last non-"off" mode the user actively chose ("include" or "exclude").
    /// Used to restore the picker mode when the user toggles full-tunnel off
    /// (mode="off") and then back on. Defaults to "include" — first-time
    /// users who hit the split radio start in selected-only mode, matching
    /// the most common per-app filter intent. Resolution lives in
    /// <see cref="VPNRouter.Core.Models.PerAppFilterMode.ResolveLastMode"/>
    /// so VPNRouter.Tests can pin the rule on net8.0.
    /// </summary>
    public static string GetPerAppLastMode() =>
        VPNRouter.Core.Models.PerAppFilterMode.ResolveLastMode(GetString(KeyPerAppLastMode));
    public static bool SetPerAppLastMode(string? value) => SetString(KeyPerAppLastMode, value);

    public static List<string> GetPerAppPackages()
    {
        try
        {
            var json = GetString(KeyPerAppPackages);
            if (string.IsNullOrWhiteSpace(json)) return new List<string>();
            var list = JsonConvert.DeserializeObject<List<string>>(json);
            return list ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static bool SetPerAppPackages(IEnumerable<string>? packages)
    {
        try
        {
            if (packages is null) return SetString(KeyPerAppPackages, null);
            var list = new List<string>(packages);
            var json = JsonConvert.SerializeObject(list);
            return SetString(KeyPerAppPackages, json);
        }
        catch
        {
            return false;
        }
    }

    // ── v2.32.0 Settings parity (handbook §3.1, mirrors desktop NetworkPage) ──
    //
    // Four sub-sections persisted as discrete SharedPreferences keys so the
    // Android Settings overlay can read/write each control independently:
    //   • Routing: routing mode + Russian-traffic bypass
    //   • Leak protection: block_on_vpn_fail master + DNS strategy
    //   • Updates: channel selector (stable / experimental / placeholder)
    //   • Autostart: 3 component flags (vpn / zapret / tgproxy) — currently
    //     no-op on Android (no BOOT_COMPLETED receiver + no Service-mode),
    //     but persisted so a future BootCompletedReceiver can act on them.
    //
    // Defaults match the desktop AppSettings.App defaults (RoutingMode="split",
    // BypassRussianTraffic=true, BlockOnVpnFail=false, ForceIpv4Only=true,
    // Channel="stable", Autostart*=false).

    private const string KeyRoutingMode = "routing_mode";              // "split" | "full"
    private const string KeyBypassRussianTraffic = "bypass_ru";        // bool
    private const string KeyBlockOnVpnFail = "block_on_vpn_fail";      // bool — Leak ➜ setBlocking
    private const string KeyDnsStrategy = "dns_strategy";              // "ipv4_only" | "prefer_ipv4" | "prefer_ipv6"
    private const string KeyUpdateChannel = "update_channel";          // "stable" | "experimental"
    private const string KeyAutostartVpn = "autostart_vpn";            // bool
    private const string KeyAutostartZapret = "autostart_zapret";      // bool
    private const string KeyAutostartTgProxy = "autostart_tgproxy";    // bool

    public static string GetRoutingMode() => GetString(KeyRoutingMode) ?? "split";
    public static bool SetRoutingMode(string value) => SetString(KeyRoutingMode, value);

    public static bool GetBypassRussianTraffic() => GetBool(KeyBypassRussianTraffic, defaultValue: true);
    public static bool SetBypassRussianTraffic(bool value) => SetBool(KeyBypassRussianTraffic, value);

    public static bool GetBlockOnVpnFail() => GetBool(KeyBlockOnVpnFail, defaultValue: false);
    public static bool SetBlockOnVpnFail(bool value) => SetBool(KeyBlockOnVpnFail, value);

    public static string GetDnsStrategy() => GetString(KeyDnsStrategy) ?? "ipv4_only";
    public static bool SetDnsStrategy(string value) => SetString(KeyDnsStrategy, value);

    public static string GetUpdateChannel() => GetString(KeyUpdateChannel) ?? "stable";
    public static bool SetUpdateChannel(string value) => SetString(KeyUpdateChannel, value);

    public static bool GetAutostartVpn() => GetBool(KeyAutostartVpn, defaultValue: false);
    public static bool SetAutostartVpn(bool value) => SetBool(KeyAutostartVpn, value);

    public static bool GetAutostartZapret() => GetBool(KeyAutostartZapret, defaultValue: false);
    public static bool SetAutostartZapret(bool value) => SetBool(KeyAutostartZapret, value);

    public static bool GetAutostartTgProxy() => GetBool(KeyAutostartTgProxy, defaultValue: false);
    public static bool SetAutostartTgProxy(bool value) => SetBool(KeyAutostartTgProxy, value);

    // ── Internals ───────────────────────────────────────────────────────────

    private static string? GetString(string key)
    {
        try
        {
            var ctx = Application.Context;
            if (ctx == null) return null;
            var prefs = ctx.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            var v = prefs?.GetString(key, null);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch
        {
            return null;
        }
    }

    private static bool SetString(string key, string? value)
    {
        try
        {
            var ctx = Application.Context;
            if (ctx == null) return false;
            var prefs = ctx.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            if (prefs == null) return false;
            using var editor = prefs.Edit();
            if (editor == null) return false;
            if (string.IsNullOrWhiteSpace(value))
                editor.Remove(key);
            else
                editor.PutString(key, value);
            return editor.Commit();
        }
        catch
        {
            return false;
        }
    }

    private static bool GetBool(string key, bool defaultValue)
    {
        try
        {
            var ctx = Application.Context;
            if (ctx == null) return defaultValue;
            var prefs = ctx.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            return prefs?.GetBoolean(key, defaultValue) ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static bool SetBool(string key, bool value)
    {
        try
        {
            var ctx = Application.Context;
            if (ctx == null) return false;
            var prefs = ctx.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            if (prefs == null) return false;
            using var editor = prefs.Edit();
            if (editor == null) return false;
            editor.PutBoolean(key, value);
            return editor.Commit();
        }
        catch
        {
            return false;
        }
    }
}
