using Android.App;
using Android.Content;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

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

    // ── v2.32.0 (AND-CC): config mode + custom sing-box JSON ────────────
    //
    // Three-way enum mirroring desktop's AppSettings.App.ConfigMode:
    //   • "subscribe" — fetch server pool from a subscription URL
    //     (KeySubscriptionUrl + KeyServersJson hold the data)
    //   • "manual"    — single share-link URI (KeyVlessUri holds the data)
    //   • "custom"    — user-pasted full sing-box JSON (KeyCustomConfigJson
    //     holds the raw text, KeyCustomConfigName labels it for the UI)
    //
    // Pre-2.32.0 the "mode" was implicit: GetActiveServer() returned the
    // selected subscription server first, falling back to the manual URI.
    // With custom JSON in the mix we need an explicit selector — desktop
    // does the same (MainWindowViewModel.cs writes ConfigMode in
    // SaveSettings, line 1544).
    private const string KeyConfigMode = "config_mode";              // "subscribe" | "manual" | "custom"
    private const string KeyCustomConfigJson = "custom_config_json"; // raw user-pasted sing-box JSON
    private const string KeyCustomConfigName = "custom_config_name"; // display name for the UI (optional)

    public static string GetConfigMode()
    {
        var stored = GetString(KeyConfigMode);
        if (stored == "subscribe" || stored == "manual" || stored == "custom")
            return stored;

        // Legacy fallback for installs from before AND-CC: derive from
        // whichever single-source key happened to be populated. Subscription
        // wins because that's the historical default flow.
        if (!string.IsNullOrEmpty(GetString(KeySubscriptionUrl))) return "subscribe";
        if (!string.IsNullOrEmpty(GetString(KeyVlessUri))) return "manual";
        return "subscribe";
    }
    public static bool SetConfigMode(string value) => SetString(KeyConfigMode, value);

    public static string? GetCustomConfigJson() => GetString(KeyCustomConfigJson);
    public static bool SetCustomConfigJson(string? value) => SetString(KeyCustomConfigJson, value);

    public static string GetCustomConfigName() => GetString(KeyCustomConfigName) ?? "custom";
    public static bool SetCustomConfigName(string? value) => SetString(KeyCustomConfigName, value);

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
    /// <para>v2.32.0 (Android self-repair) — JSON parse failure no longer
    /// silently returns empty; <see cref="StorageBlobRecovery.LoadOrRecover{T}"/>
    /// classifies it, the bad payload is quarantined to a sibling
    /// SharedPreferences key (forensic trail), and a recovery notice is
    /// stamped so the UI banner can surface "we reset your subscriptions
    /// because the saved data was unreadable".</para>
    /// </summary>
    public static List<SubscriptionEntry> GetSubscriptions()
    {
        var json = GetString(KeySubscriptions);
        var result = StorageBlobRecovery.LoadOrRecover<List<SubscriptionEntry>>(
            json,
            j => JsonConvert.DeserializeObject<List<SubscriptionEntry>>(j));

        if (result.Loaded)
            return result.Value!;

        if (result.ShouldRecover)
        {
            QuarantineBadValue(KeySubscriptions, json);
            StampRecoveryNotice(
                $"subscriptions cache unreadable ({result.Reason}: {result.Detail}); reset to defaults");
        }

        // NotFound (no value yet) — try the legacy single-URL migration.
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
    /// unparseable. v2.32.0: routes through
    /// <see cref="StorageBlobRecovery.LoadOrRecover{T}"/> so a corrupt
    /// payload is quarantined + the user gets a recovery notice instead
    /// of a silent empty list.
    /// </summary>
    public static List<VlessServerEntry> GetServers()
    {
        var json = GetString(KeyServersJson);
        var result = StorageBlobRecovery.LoadOrRecover<List<VlessServerEntry>>(
            json,
            j => JsonConvert.DeserializeObject<List<VlessServerEntry>>(j));

        if (result.Loaded)
            return result.Value!;

        if (result.ShouldRecover)
        {
            QuarantineBadValue(KeyServersJson, json);
            StampRecoveryNotice(
                $"server cache unreadable ({result.Reason}: {result.Detail}); reset to empty");
        }
        return new List<VlessServerEntry>();
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
    /// nothing like desktop on first launch). v2.32.0 SR-1: unknown
    /// values quarantined and replaced with default.
    /// </summary>
    public static string GetTheme() =>
        ValidateOrDefault(KeyTheme, GetString(KeyTheme), AllowedThemes, "light");
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

    public static string GetPerAppMode()
    {
        // v2.32.0 SR-1 — surface (but tolerate) unknown values. Core's
        // PerAppFilterMode.Normalize maps anything unrecognised to "off"
        // silently; we additionally stamp a recovery notice so the user
        // knows their setting was reset rather than mysteriously reverting
        // to full-tunnel. Empty / null is the first-run path — no notice.
        var raw = GetString(KeyPerAppMode);
        var normalized = VPNRouter.Core.Models.PerAppFilterMode.Normalize(raw);
        if (!string.IsNullOrWhiteSpace(raw) &&
            !string.Equals(raw, normalized, StringComparison.OrdinalIgnoreCase))
        {
            QuarantineBadValue(KeyPerAppMode, raw);
            StampRecoveryNotice(
                $"per-app filter mode '{raw}' unknown; reset to '{normalized}'");
            SetString(KeyPerAppMode, normalized);
        }
        return normalized;
    }
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
        var json = GetString(KeyPerAppPackages);
        var result = StorageBlobRecovery.LoadOrRecover<List<string>>(
            json,
            j => JsonConvert.DeserializeObject<List<string>>(j));

        if (result.Loaded)
            return result.Value!;

        if (result.ShouldRecover)
        {
            QuarantineBadValue(KeyPerAppPackages, json);
            StampRecoveryNotice(
                $"per-app package list unreadable ({result.Reason}: {result.Detail}); reset to empty");
        }
        return new List<string>();
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

    // ── v2.32.0 AND-NETRES (2026-05-07) — Reliability section settings ──
    //
    // The Java VpnRouterService reads these directly (same SharedPreferences
    // file, same key strings). Keep keys in sync with VpnRouterService.java
    // PREFS_NAME / KEY_* constants. Defaults: auto-reconnect ON because
    // sing-box's interface monitor needs the platform default-network
    // updates to bind upstream sockets correctly on Wi-Fi ↔ cellular handoff.
    private const string KeyAutoReconnectOnNetworkChange = "auto_reconnect_on_network_change";

    // v2.32.0 SR-1 — semantic validation. Each enum getter normalises the
    // raw stored value through ValidateOrDefault, so a typoed / older /
    // hand-edited preference can't surface as an unsupported string deep
    // inside the routing engine. Bad values are quarantined and replaced
    // with the documented default; the user sees a recovery notice.
    private static readonly HashSet<string> AllowedRoutingModes =
        new(StringComparer.OrdinalIgnoreCase) { "split", "full" };
    private static readonly HashSet<string> AllowedDnsStrategies =
        new(StringComparer.OrdinalIgnoreCase) { "ipv4_only", "ipv6_only", "prefer_ipv4", "prefer_ipv6", "default" };
    private static readonly HashSet<string> AllowedUpdateChannels =
        new(StringComparer.OrdinalIgnoreCase) { "stable", "experimental" };
    private static readonly HashSet<string> AllowedThemes =
        new(StringComparer.OrdinalIgnoreCase) { "light", "dark", "system" };

    public static string GetRoutingMode() =>
        ValidateOrDefault(KeyRoutingMode, GetString(KeyRoutingMode), AllowedRoutingModes, "split");
    public static bool SetRoutingMode(string value) => SetString(KeyRoutingMode, value);

    public static bool GetBypassRussianTraffic() => GetBool(KeyBypassRussianTraffic, defaultValue: true);
    public static bool SetBypassRussianTraffic(bool value) => SetBool(KeyBypassRussianTraffic, value);

    public static bool GetBlockOnVpnFail() => GetBool(KeyBlockOnVpnFail, defaultValue: false);
    public static bool SetBlockOnVpnFail(bool value) => SetBool(KeyBlockOnVpnFail, value);

    public static string GetDnsStrategy() =>
        ValidateOrDefault(KeyDnsStrategy, GetString(KeyDnsStrategy), AllowedDnsStrategies, "ipv4_only");
    public static bool SetDnsStrategy(string value) => SetString(KeyDnsStrategy, value);

    public static string GetUpdateChannel() =>
        ValidateOrDefault(KeyUpdateChannel, GetString(KeyUpdateChannel), AllowedUpdateChannels, "stable");
    public static bool SetUpdateChannel(string value) => SetString(KeyUpdateChannel, value);

    public static bool GetAutostartVpn() => GetBool(KeyAutostartVpn, defaultValue: false);
    public static bool SetAutostartVpn(bool value) => SetBool(KeyAutostartVpn, value);

    public static bool GetAutostartZapret() => GetBool(KeyAutostartZapret, defaultValue: false);
    public static bool SetAutostartZapret(bool value) => SetBool(KeyAutostartZapret, value);

    public static bool GetAutostartTgProxy() => GetBool(KeyAutostartTgProxy, defaultValue: false);
    public static bool SetAutostartTgProxy(bool value) => SetBool(KeyAutostartTgProxy, value);

    // ── v2.32.0 (AND-ZAPRET, 2026-05-07) — DPI bypass mode (handbook §7 Phase 8.4) ──
    //
    // Android equivalent of desktop's Zapret feature. Desktop runs winws.exe
    // (a userspace WinDivert-based DPI-bypass tool) — that approach can't
    // run on non-rooted Android. Instead we lean on sing-box's native
    // outbound dialer options (tls_fragment + udp_fragment) which do
    // packet-level fragmentation inside the tunnel without any extra
    // userspace process. AndroidConfigBuilder.InjectDpiBypass picks up
    // this value and mutates the proxy outbounds in the generated /
    // user-supplied JSON before it reaches libbox.
    //
    // Three values mirror the desktop "Strategy" picker:
    //   • "off"        — no fragmentation (pre-AND-ZAPRET behaviour)
    //   • "standard"   — moderate fragments (10–100 B, 10–50 ms sleep)
    //                    works against most Russian ISP DPI layers
    //   • "aggressive" — small fragments (5–20 B, 50–150 ms sleep)
    //                    + udp_fragment for QUIC; trades latency for
    //                    bypass success on the most aggressive blocks
    //
    // SR-1 normaliser: an unknown stored value is quarantined and reset
    // to "off" with a recovery notice, same pattern as RoutingMode etc.
    private const string KeyDpiBypassMode = "dpi_bypass_mode";
    private static readonly HashSet<string> AllowedDpiBypassModes =
        new(StringComparer.OrdinalIgnoreCase) { "off", "standard", "aggressive" };

    public static string GetDpiBypassMode() =>
        ValidateOrDefault(KeyDpiBypassMode, GetString(KeyDpiBypassMode), AllowedDpiBypassModes, "off");
    public static bool SetDpiBypassMode(string value) => SetString(KeyDpiBypassMode, value);

    // ── v2.32.0 (AND-PROFILES, 2026-05-08) — active routing profile ─────
    //
    // Stores the *name* of the profile the user last applied (or null when
    // no profile is active). The package list itself is stored in
    // KeyPerAppPackages — applying a profile rewrites that list. We persist
    // the name separately so the Profiles overlay can highlight which card
    // is active even after subscription refreshes / app restart.
    //
    // Multi-select (desktop-style "Discord_Privacy,Work_Suite" merge) is
    // intentionally out of scope for the first port — Android UX is tap-
    // one. Future expansion can swap to a comma-separated list and reuse
    // ProfileManager.MergeProfilesTolerant.
    private const string KeyActiveProfile = "active_profile";

    public static string? GetActiveProfile() => GetString(KeyActiveProfile);
    public static bool SetActiveProfile(string? value) => SetString(KeyActiveProfile, value);

    public static bool GetAutoReconnectOnNetworkChange() =>
        GetBool(KeyAutoReconnectOnNetworkChange, defaultValue: true);
    public static bool SetAutoReconnectOnNetworkChange(bool value) =>
        SetBool(KeyAutoReconnectOnNetworkChange, value);

    // ── v2.32.0 (AND-4): per-server TCP+TLS test history ──────────────────
    //
    // Side-table keyed by VlessServersResolver dedup shape ("Server:Port:Uuid:Flow").
    // Values are ServerTestResultDto JSON-serialized — status int (matches
    // ServerProbeStatus enum), latency ms, last-tested timestamp, optional error.
    // Survives subscription refresh because the dedup key is content-hash based.
    //
    // Pre-AND-4 there was no test history on Android — desktop-only feature
    // via ServerViewModel in-memory (lost on app restart). On Android we
    // persist so the badge survives kill+relaunch — mobile UX expectation
    // is "remembers what I last knew about each server".
    private const string KeyServerTestResults = "server_test_results";

    /// <summary>
    /// v2.32.0 (AND-4): one entry in the persisted test-results map.
    /// JSON-serialized verbatim. Status int corresponds to
    /// <see cref="VPNRouter.Core.Services.ServerProbeStatus"/>.
    /// </summary>
    public sealed class ServerTestResultDto
    {
        [JsonProperty("status")]
        public int Status { get; set; }
        [JsonProperty("latency_ms")]
        public int LatencyMs { get; set; }
        [JsonProperty("last_tested_at")]
        public DateTimeOffset LastTestedAt { get; set; }
        [JsonProperty("error")]
        public string? Error { get; set; }
    }

    /// <summary>
    /// Build the dedup key for a server. Mirrors
    /// <c>VlessServersResolver.Resolve</c> (Server:Port:Uuid:Flow) so test
    /// results survive subscription refresh — same physical server in two
    /// subscriptions (or after re-fetch) keeps its history.
    /// </summary>
    public static string BuildServerKey(VlessServerEntry srv)
        => $"{srv.Server}:{srv.Port}:{srv.Uuid}:{srv.Flow}";

    public static Dictionary<string, ServerTestResultDto> GetServerTestResults()
    {
        try
        {
            var json = GetString(KeyServerTestResults);
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, ServerTestResultDto>(System.StringComparer.OrdinalIgnoreCase);
            var dict = JsonConvert.DeserializeObject<Dictionary<string, ServerTestResultDto>>(json);
            if (dict is null) return new Dictionary<string, ServerTestResultDto>(System.StringComparer.OrdinalIgnoreCase);
            // Ensure case-insensitive comparer on read (JSON deserializer
            // defaults to ordinal — would miss casing changes in host
            // names which DNS treats as case-insensitive).
            return new Dictionary<string, ServerTestResultDto>(dict, System.StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, ServerTestResultDto>(System.StringComparer.OrdinalIgnoreCase);
        }
    }

    public static bool SetServerTestResults(Dictionary<string, ServerTestResultDto>? results)
    {
        try
        {
            if (results is null || results.Count == 0)
                return SetString(KeyServerTestResults, null);
            // Opportunistic prune: drop entries older than 7 days. Keeps
            // the JSON blob from growing unbounded across many
            // subscription re-fetch cycles.
            var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
            var pruned = new Dictionary<string, ServerTestResultDto>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in results)
            {
                if (kvp.Value.LastTestedAt < cutoff) continue;
                pruned[kvp.Key] = kvp.Value;
            }
            var json = JsonConvert.SerializeObject(pruned);
            return SetString(KeyServerTestResults, json);
        }
        catch
        {
            return false;
        }
    }

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

    // ── v2.32.0 Self-repair plumbing (handbook §1.3 mirror of desktop SR-1/3/4) ─

    private static readonly object _recoveryLock = new();
    private static string? _lastRecoveryNotice;

    /// <summary>
    /// Most recent recovery action since the last <see cref="ConsumeRecoveryNotice"/>
    /// call. Mirrors <see cref="VPNRouter.Core.Services.SettingsLoader.LastRecoveryNotice"/>:
    /// stamped each time a corrupt SharedPreferences value was quarantined +
    /// replaced with defaults, or an unknown enum was normalised to its
    /// default. Cleared after a single read so the UI banner doesn't keep
    /// re-surfacing the same message.
    /// </summary>
    public static string? LastRecoveryNotice
    {
        get { lock (_recoveryLock) return _lastRecoveryNotice; }
    }

    /// <summary>
    /// One-shot accessor — atomically returns the current notice and clears
    /// it so the next caller doesn't re-surface the same banner. Called
    /// from <c>AndroidApp.OnFrameworkInitializationCompleted</c> after the
    /// first view is built, then merged with
    /// <see cref="VPNRouter.Core.Services.SettingsLoader.ConsumeRecoveryNotice"/>.
    /// </summary>
    public static string? ConsumeRecoveryNotice()
    {
        lock (_recoveryLock)
        {
            var n = _lastRecoveryNotice;
            _lastRecoveryNotice = null;
            return n;
        }
    }

    /// <summary>
    /// Tests + recovery dispatch reset hook — wipes the in-memory notice
    /// without consuming it (e.g. between scenarios). Production code uses
    /// <see cref="ConsumeRecoveryNotice"/>.
    /// </summary>
    internal static void ResetRecoveryNoticeForTests()
    {
        lock (_recoveryLock) _lastRecoveryNotice = null;
    }

    /// <summary>
    /// Stamp a recovery message; if multiple stamps land before the UI
    /// reads, they're concatenated with "; " so nothing gets lost. Best-
    /// effort: any failure (Application.Context null during early Main,
    /// etc.) is swallowed.
    /// </summary>
    private static void StampRecoveryNotice(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        try
        {
            global::Android.Util.Log.Warn("VpnRouter.SelfRepair", message);
        }
        catch { /* logging itself failed — ignore */ }

        lock (_recoveryLock)
        {
            _lastRecoveryNotice = string.IsNullOrEmpty(_lastRecoveryNotice)
                ? message
                : $"{_lastRecoveryNotice}; {message}";
        }
    }

    /// <summary>
    /// SR-1 normaliser: if <paramref name="raw"/> is null/empty/unknown,
    /// quarantine + return <paramref name="defaultValue"/>; if known,
    /// return the canonical-cased value from <paramref name="allowed"/>.
    /// Idempotent — repeated calls with the same valid value are a no-op
    /// and never stamp a notice.
    /// </summary>
    private static string ValidateOrDefault(
        string key, string? raw, HashSet<string> allowed, string defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        // Normalise to the canonical casing from the allowed set so
        // downstream comparisons can use ordinal equality without case
        // surprises.
        var match = allowed.FirstOrDefault(v =>
            string.Equals(v, raw, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(match)) return match!;

        QuarantineBadValue(key, raw);
        StampRecoveryNotice(
            $"setting '{key}' had unknown value '{raw}'; reset to '{defaultValue}'");
        SetString(key, defaultValue);
        return defaultValue;
    }

    /// <summary>
    /// Move a corrupt SharedPreferences value to a sibling key
    /// <c>{key}__corrupt_{yyyyMMdd_HHmmss}</c> so a future bug report can
    /// inspect it via <c>adb shell run-as com.ninitux.vpnrouter cat
    /// shared_prefs/vpnrouter_settings.xml</c>. Best-effort: a failure here
    /// is logged but never propagates — we still clear the bad key so the
    /// app can keep running.
    /// </summary>
    private static void QuarantineBadValue(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        try
        {
            var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var quarantineKey = $"{key}__corrupt_{ts}";
            SetString(quarantineKey, value);
            // Don't delete the original key here — the caller may want to
            // overwrite it with a fresh default. The companion preserves
            // the original payload for forensics.
        }
        catch (Exception ex)
        {
            try
            {
                global::Android.Util.Log.Warn("VpnRouter.SelfRepair",
                    $"quarantine of '{key}' failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch { /* nothing more we can do */ }
        }
    }

    // ── v2.32.0 (AND-SR-1) central repair pass ────────────────────────────
    //
    // Mirror of desktop's SettingsLoader.LoadCore pipeline (deserialise →
    // EnsureSane → SettingsValidator) scoped to the SharedPreferences keys
    // this layer treats as enums. Pre-AND-SR-1 each enum getter ran its own
    // ValidateOrDefault on first read, which is correct but means the
    // contract drifts per-key whenever Core grows a new invariant. Wiring
    // the central Core helper in once at startup gives us:
    //
    //   • a single call site to extend when desktop adds an invariant to
    //     AppSettingsSane.EnsureSane (covered by the transient AppSettings
    //     pass inside the Core helper — today a no-op, future-proofed),
    //   • eager repair so corrupt values surface in the recovery notice
    //     before the first read instead of being lazily fixed,
    //   • a testable seam — the Core helper is net8.0 and exercised in
    //     VPNRouter.Tests/AndroidStorageSaneTests without an Android device.
    //
    // The existing per-getter ValidateOrDefault calls stay as defense-in-
    // depth (idempotent on a clean store) so a key that bypasses startup
    // for any reason still gets repaired on first read.

    /// <summary>
    /// AND-SR-1 — run the central self-repair pass over every enum-shaped
    /// SharedPreferences key. Returns the count of repairs (0 on a clean
    /// store). Call once from
    /// <c>AndroidApp.OnFrameworkInitializationCompleted</c> before any
    /// consumer reads. Recovery notices accumulated during the pass are
    /// surfaced via <see cref="ConsumeRecoveryNotice"/>.
    /// </summary>
    public static int RepairAllOnLoad()
    {
        try
        {
            var enumKeys = new List<AndroidStorageSane.EnumKeySpec>
            {
                new(KeyRoutingMode, AllowedRoutingModes, "split"),
                new(KeyDnsStrategy, AllowedDnsStrategies, "ipv4_only"),
                new(KeyUpdateChannel, AllowedUpdateChannels, "stable"),
                new(KeyTheme, AllowedThemes, "light"),
                new(KeyDpiBypassMode, AllowedDpiBypassModes, "off"),
            };

            var outcome = AndroidStorageSane.RepairAllOnLoad(
                get: GetString,
                // SetString returns bool (success); the Core helper takes
                // an Action<string,string?>. Wrap so the discard happens
                // here rather than leaking the bool signature into Core.
                set: (k, v) => { SetString(k, v); },
                enumKeys: enumKeys,
                quarantine: QuarantineBadValue);

            foreach (var change in outcome.Changes)
                StampRecoveryNotice(change);

            return outcome.Changes.Count;
        }
        catch (Exception ex)
        {
            try
            {
                global::Android.Util.Log.Warn("VpnRouter.SelfRepair",
                    $"RepairAllOnLoad failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch { /* nothing more we can do */ }
            return 0;
        }
    }

    // ── SR-2 tier-3 safe-mode banner flag ─────────────────────────────────
    //
    // Persisted because the launch that crashed didn't get to OnFrameworkInitialization
    // — by the time OnCreate next runs, the in-memory _lastRecoveryNotice
    // is empty. Storing a one-shot flag in SharedPreferences lets the next
    // successful render pick up the banner. Cleared by ConsumeSafeModeBanner
    // after the UI displays it.
    private const string KeySafeModeBannerPending = "safe_mode_banner_pending";

    /// <summary>
    /// SR-2 tier-3: queue a persistent banner suggesting "Settings > Apps >
    /// VPNRouter > Storage > Clear data" for the next successful UI render.
    /// Survives process restart so a 7th-strike user still sees the prompt
    /// after the launch that crashed.
    /// </summary>
    public static void QueueSafeModeBannerForUi() => SetBool(KeySafeModeBannerPending, true);

    /// <summary>
    /// One-shot read+clear for the pending safe-mode banner. Returns true
    /// exactly once after <see cref="QueueSafeModeBannerForUi"/> was called;
    /// subsequent reads return false until queued again.
    /// </summary>
    public static bool ConsumeSafeModeBanner()
    {
        if (!GetBool(KeySafeModeBannerPending, defaultValue: false)) return false;
        try { SetBool(KeySafeModeBannerPending, false); }
        catch { /* read still succeeded; clear is best-effort */ }
        return true;
    }

    /// <summary>
    /// SR-2 tier-2 (config-reset) target: erase every user-data key in
    /// <c>vpnrouter_settings</c>. Quarantine companions ({key}__corrupt_*)
    /// are kept on purpose so a future bug report can still see what was
    /// there. After this call the next <see cref="GetSubscriptions"/> /
    /// <see cref="GetServers"/> / etc. return defaults.
    /// </summary>
    public static bool ResetUserSettings()
    {
        try
        {
            var ctx = Application.Context;
            if (ctx == null) return false;
            var prefs = ctx.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            if (prefs == null) return false;
            using var editor = prefs.Edit();
            if (editor == null) return false;

            // Remove the documented user-data keys explicitly. Don't use
            // editor.Clear() because that nukes quarantine companions too.
            var liveKeys = new[]
            {
                KeyVlessUri, KeySubscriptionUrl, KeyServersJson,
                KeySelectedServerName, KeyLanguage, KeyTheme,
                KeySubscriptions, KeyPerAppMode, KeyPerAppPackages,
                KeyPerAppLastMode, KeyRoutingMode, KeyBypassRussianTraffic,
                KeyBlockOnVpnFail, KeyDnsStrategy, KeyUpdateChannel,
                KeyAutostartVpn, KeyAutostartZapret, KeyAutostartTgProxy,
                KeyDpiBypassMode,
                KeyActiveProfile,
            };
            foreach (var k in liveKeys) editor.Remove(k);
            return editor.Commit();
        }
        catch
        {
            return false;
        }
    }
}
