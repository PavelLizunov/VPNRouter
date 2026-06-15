using Android.App;
using Android.Content;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VPNRouter.Android.Json;
using VPNRouter.Core.Json;
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
/// overhead. JSON for the server list, originally via Newtonsoft.Json
/// (used across <see cref="VPNRouter.Core"/>). Phase 3B (2026-05-18) migrated
/// to <see cref="System.Text.Json.JsonSerializer"/> — AOT-friendly + 2-5×
/// faster + ships with the runtime (a future AOT-build can drop a 600 KB
/// dependency once every Newtonsoft call site is migrated).
///
/// <para>Wire-format compat: existing on-disk SharedPreferences JSON
/// uses <see cref="VlessServerEntry"/> / <see cref="SubscriptionEntry"/> /
/// <see cref="CustomCategory"/> serialized by Newtonsoft default conventions
/// (PascalCase property names — Newtonsoft preserves C# property names
/// when no <c>[JsonProperty]</c> is set). STJ default conventions are
/// identical (it also uses C# property names verbatim by default).
/// Combined with <c>PropertyNameCaseInsensitive=true</c> in
/// <see cref="JsonOptions"/>, this gives lossless round-trip with all
/// legacy SharedPreferences blobs written by pre-3B installs.</para>
///
/// <para>Phase 2+: replace SharedPreferences entirely with <c>SettingsLoader</c>
/// pointed at <c>Application.FilesDir/config.yaml</c>. Then desktop and
/// Android share the same on-disk schema. Until then, this thin facade
/// keeps the keys in one place.</para>
/// </summary>
public static class AndroidStorage
{
    private const string PrefsName = "vpnrouter_settings";

    /// <summary>
    /// Phase 3B (2026-05-18) — STJ options used for every SharedPreferences
    /// blob in this class. <c>PropertyNameCaseInsensitive=true</c> matches
    /// Newtonsoft's default lookup behaviour so a JSON written by the
    /// pre-3B build (Newtonsoft default conventions) round-trips back into
    /// the same C# objects under the new serializer. <c>WriteIndented=false</c>
    /// keeps SharedPreferences blobs compact (no human readability needed —
    /// these are not on-disk diagnostic files).
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        // Phase 5 — Wave 25 AOT-2 (2026-05-18): SubscriptionEntry +
        // VlessServerEntry + their List<T> wrappers are registered in
        // AppJsonContext so the SharedPreferences read/write paths
        // (GetSubscriptions / SetSubscriptions / GetServers / SetServers)
        // route through generated JsonTypeInfo on AOT builds.
        // Phase 6 — Wave 28 6-AJ-1 (2026-05-18): AndroidJsonContext
        // wires the Android-side shapes that Core cannot reach
        // (ServerTestResultDto + its Dictionary wrapper, CustomCategory
        // + its List wrapper, and the per-app-packages List<string>).
        // Chain order: AndroidJsonContext first (Android-specific takes
        // priority), then AppJsonContext (Core types), then reflective
        // fallback (for any one-off anonymous shapes — none today).
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            AndroidJsonContext.Default,
            AppJsonContext.Default,
            new DefaultJsonTypeInfoResolver()),
    };

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

    // Bug-AND-023 v4 (2026-05-17) — one-shot migration flag for the
    // "subscription servers shouldn't appear on the Servers tab too"
    // cleanup. v3 had two distinct paths writing the same content into
    // KeyServersJson:
    //   (a) SetSubscriptions implicitly rebuilt KeyServersJson as the
    //       union of every enabled subscription's Servers (aggregated
    //       pool) — making KeyServersJson a duplicate of subscription
    //       content rather than a separate "manual" list.
    //   (b) ApplyScannedSubscriptionUrlAsync (Bug-AND-023 v3) further
    //       merged the freshly-fetched subscription's Servers into
    //       KeyServersJson, redundantly.
    // v4 cuts both paths; KeyServersJson is now a pure standalone list
    // (free-config "Use" picks + manual vless:// URIs + QR vless:// scans).
    // The Servers tab UI already only renders _srvCurrentSub?.Servers
    // (the in-memory current SubscriptionEntry's list, not GetServers),
    // so this change is invisible to callers — except for users who
    // upgraded from v3 with already-populated KeyServersJson dupes.
    // PruneSubServerDuplicatesOnce strips those.
    private const string KeyV4SubServersPruneDone = "v4_sub_servers_prune_done";

    // v2.32.3 (2026-05-17, Z:\kanareik incident) — one-shot migration flag
    // for the "permanently exorcise PlaceholderVlessUri leftovers from
    // every user's storage" cleanup. Mirrors the desktop SettingsMigrator
    // sibling pass — walks both KeyServersJson and KeySubscriptions[].Servers
    // and removes any entry whose Reality pubkey / short_id / server IP
    // matches the known stas-class placeholder fingerprints. Counter is
    // surfaced via KeyPlaceholderPruneCount so the AndroidApp banner can
    // tell the user how many credentials were yanked on this upgrade.
    private const string KeyV4PlaceholderPruneDone = "v4_placeholder_prune_done";
    private const string KeyPlaceholderPruneCount = "v4_placeholder_prune_count";

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
        // 2026-05-10 revert per user: first-launch default must show
        // "manual · split" matching desktop v2.32.0 (AppSettings.ConfigMode
        // default = "generated" displays as "manual"). Pre-fix returned
        // "subscribe" forcing fresh installs into subscription·all mode.
        return "manual";
    }
    public static bool SetConfigMode(string value) => SetString(KeyConfigMode, value);

    // v2.42.0 resume re-sync — authoritative live tunnel state, written by the
    // Java VpnRouterService in lockstep with the TUNNEL_UP/DOWN broadcasts
    // (same "vpnrouter_settings" prefs file == PrefsName). Read on
    // MainActivity.OnResume to demote a stale "Connected" status card when a
    // TUNNEL_DOWN broadcast was lost because no Activity (hence no receiver)
    // was alive at send time. See TunnelStateResync for the demote-only
    // decision + plans/android-status-card-stale-lifecycle-investigation-2026-06-13.md.
    private const string KeyTunnelLive = "tunnel_live";

    /// <summary>
    /// Service-persisted authoritative tunnel live-state: true between a
    /// TUNNEL_UP and the next TUNNEL_DOWN. Defaults to false (no tunnel).
    /// </summary>
    public static bool GetTunnelLive() => GetBool(KeyTunnelLive, false);

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
        // Phase 3B (2026-05-18) — STJ migration. Same wire shape as the
        // Newtonsoft predecessor (PascalCase C# property names by default
        // on VlessServerEntry / SubscriptionEntry), case-insensitive
        // lookup keeps legacy blobs readable.
        var result = StorageBlobRecovery.LoadOrRecover<List<SubscriptionEntry>>(
            json,
            j => JsonSerializer.Deserialize<List<SubscriptionEntry>>(j, JsonOptions));

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
    /// clear.
    /// <para>Bug-AND-023 v4 (2026-05-17, user-reported "сервера подписки
    /// также продублировались из страницы подписки на страницу сервер"):
    /// pre-v4 this method also flushed an aggregated <see cref="GetServers"/>
    /// pool (union of every enabled subscription's Servers). That made
    /// KeyServersJson a redundant copy of subscription content, which
    /// showed up as "every server in Subscribe tab also appears in Servers
    /// tab". v4 drops the implicit rebuild — KeyServersJson is now a pure
    /// standalone list (free-config picks, manual URIs, QR vless scans).
    /// <see cref="GetActiveServer"/> was extended in the same fix to walk
    /// both standalone Servers AND every subscription's in-memory Servers
    /// list so the connect path keeps working without the duplicate
    /// storage.</para>
    /// </summary>
    public static bool SetSubscriptions(IEnumerable<SubscriptionEntry>? subs)
    {
        try
        {
            var list = subs is null ? new List<SubscriptionEntry>() : new List<SubscriptionEntry>(subs);
            var json = JsonSerializer.Serialize(list, JsonOptions);
            return SetString(KeySubscriptions, json);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Bug-AND-023 v4 (2026-05-17) — one-shot cleanup for users upgrading
    /// from v3 where SetSubscriptions auto-rebuilt KeyServersJson as the
    /// aggregated subscription pool. After upgrade their KeyServersJson
    /// contains a copy of every subscription server (host:port:uuid
    /// identical), and Advanced → Servers shows the same rows the
    /// Subscribe tab does.
    ///
    /// Strategy: read both lists, remove from standalone any entry whose
    /// host:port:uuid matches a subscription server. Persist the trimmed
    /// standalone list, mark the flag, never run again. Idempotent on
    /// fresh installs (no subs OR no standalone → no work).
    ///
    /// Risk: a user manually added a server with the same host:port:uuid
    /// AS a subscription server (e.g. they pasted the same URL into both
    /// the Add-VLESS box and the Add-Subscription box). Their manual row
    /// will be removed by the prune. Acceptable: same endpoint = same
    /// tunnel, the subscription copy still works. The prune is the lesser
    /// evil compared to surfacing every subscription server twice.
    /// </summary>
    internal static void PruneSubServerDuplicatesOnce()
    {
        if (GetBool(KeyV4SubServersPruneDone, defaultValue: false)) return;

        try
        {
            var subsJson = GetString(KeySubscriptions);
            if (string.IsNullOrWhiteSpace(subsJson))
            {
                // Fresh install — nothing to prune. Stamp the flag so we
                // don't pay the parse cost on every cold start.
                SetBool(KeyV4SubServersPruneDone, true);
                return;
            }

            List<SubscriptionEntry>? subs;
            try
            {
                subs = JsonSerializer.Deserialize<List<SubscriptionEntry>>(subsJson, JsonOptions);
            }
            catch
            {
                // Corrupt blob — let GetSubscriptions handle the recovery
                // path; we'll re-attempt the prune on the next launch.
                return;
            }
            if (subs == null || subs.Count == 0)
            {
                SetBool(KeyV4SubServersPruneDone, true);
                return;
            }

            var subKeys = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var s in subs)
            {
                if (s?.Servers == null) continue;
                foreach (var srv in s.Servers)
                {
                    if (srv == null) continue;
                    subKeys.Add($"{srv.Server}:{srv.Port}:{srv.Uuid}");
                }
            }
            if (subKeys.Count == 0)
            {
                SetBool(KeyV4SubServersPruneDone, true);
                return;
            }

            var standalone = GetServers();
            int before = standalone.Count;
            standalone.RemoveAll(s =>
                s != null && subKeys.Contains($"{s.Server}:{s.Port}:{s.Uuid}"));

            if (standalone.Count != before)
            {
                global::Android.Util.Log.Info("VpnRouter.Storage",
                    $"PruneSubServerDuplicatesOnce: removed {before - standalone.Count} duplicate(s) from KeyServersJson");
                SetServers(standalone);
            }
            SetBool(KeyV4SubServersPruneDone, true);
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.Storage",
                $"PruneSubServerDuplicatesOnce threw: {ex.GetType().Name}: {ex.Message}");
            // Don't stamp the flag on error — retry on next launch.
        }
    }

    /// <summary>
    /// v2.32.3 (2026-05-17, Z:\kanareik incident) — one-shot cleanup of
    /// placeholder Reality credentials in storage. The fingerprint set is
    /// the same one <see cref="PlaceholderGuard"/> uses everywhere; this
    /// method is the Android-side equivalent of desktop's
    /// <c>SettingsMigrator.PruneKnownPlaceholders</c>.
    ///
    /// <para>What gets pruned:</para>
    /// <list type="bullet">
    ///   <item>Entries from <c>KeyServersJson</c> (standalone manual list)
    ///   whose pubkey / short_id / server IP matches a placeholder.</item>
    ///   <item>Entries from each <see cref="SubscriptionEntry.Servers"/> in
    ///   <c>KeySubscriptions</c> with the same match.</item>
    ///   <item><see cref="KeySelectedServerName"/> is cleared if it pointed
    ///   to a now-removed entry — connect path won't silently fall back to
    ///   a deleted name.</item>
    /// </list>
    ///
    /// <para>Idempotent: stamps <c>KeyV4PlaceholderPruneDone</c> on success
    /// (or on a confirmed-clean store) so the parse cost is paid exactly
    /// once per install. Updates <c>KeyPlaceholderPruneCount</c> for the
    /// UI banner. Best-effort: any exception is logged + swallowed so the
    /// app keeps launching.</para>
    /// </summary>
    internal static void PruneKnownPlaceholdersOnce()
    {
        if (GetBool(KeyV4PlaceholderPruneDone, defaultValue: false)) return;

        try
        {
            int totalRemoved = 0;

            // (a) Standalone list — KeyServersJson.
            var standalone = GetServers();
            int beforeStandalone = standalone.Count;
            standalone.RemoveAll(s => PlaceholderGuard.IsPlaceholder(s));
            int removedStandalone = beforeStandalone - standalone.Count;
            if (removedStandalone > 0)
            {
                SetServers(standalone);
                totalRemoved += removedStandalone;
                global::Android.Util.Log.Info("VpnRouter.Storage",
                    $"PruneKnownPlaceholdersOnce: removed {removedStandalone} placeholder entry(ies) from KeyServersJson");
            }

            // (b) Per-subscription Servers lists — KeySubscriptions[].Servers.
            // Read directly via JsonSerializer (Phase 3B switched from
            // Newtonsoft's JsonConvert) so we don't recurse into
            // GetSubscriptions's legacy-migration / recovery wrapping during
            // this early-boot pass — same pattern as PruneSubServerDuplicatesOnce.
            var subsJson = GetString(KeySubscriptions);
            if (!string.IsNullOrWhiteSpace(subsJson))
            {
                List<SubscriptionEntry>? subs = null;
                try
                {
                    subs = JsonSerializer.Deserialize<List<SubscriptionEntry>>(subsJson, JsonOptions);
                }
                catch
                {
                    // Corrupt blob — let GetSubscriptions handle it on
                    // first access; retry prune on next launch.
                    return;
                }
                if (subs != null)
                {
                    int removedFromSubs = 0;
                    foreach (var sub in subs)
                    {
                        if (sub?.Servers == null) continue;
                        var before = sub.Servers.Count;
                        sub.Servers.RemoveAll(s => PlaceholderGuard.IsPlaceholder(s));
                        removedFromSubs += before - sub.Servers.Count;
                    }
                    if (removedFromSubs > 0)
                    {
                        SetSubscriptions(subs);
                        totalRemoved += removedFromSubs;
                        global::Android.Util.Log.Info("VpnRouter.Storage",
                            $"PruneKnownPlaceholdersOnce: removed {removedFromSubs} placeholder entry(ies) across {subs.Count} subscription(s)");
                    }
                }
            }

            // (c) Selected-server-name dangling pointer. After (a)+(b), if
            // KeySelectedServerName no longer matches any standalone OR any
            // subscription entry, clear it. GetActiveServer's tier fallback
            // chain (v4) will pick a healthy default on next connect.
            var selectedName = GetSelectedServerName();
            if (!string.IsNullOrEmpty(selectedName))
            {
                bool stillExists = standalone.Any(s =>
                    string.Equals(s?.Name, selectedName, System.StringComparison.OrdinalIgnoreCase));
                if (!stillExists && !string.IsNullOrWhiteSpace(subsJson))
                {
                    try
                    {
                        var freshSubs = JsonSerializer.Deserialize<List<SubscriptionEntry>>(GetString(KeySubscriptions) ?? "[]", JsonOptions);
                        stillExists = freshSubs?.Any(sub =>
                            sub?.Servers?.Any(srv =>
                                string.Equals(srv?.Name, selectedName, System.StringComparison.OrdinalIgnoreCase)) == true) ?? false;
                    }
                    catch { /* swallow — treat as missing */ }
                }
                if (!stillExists)
                {
                    SetSelectedServerName(null);
                    global::Android.Util.Log.Info("VpnRouter.Storage",
                        $"PruneKnownPlaceholdersOnce: cleared dangling KeySelectedServerName='{selectedName}' (entry no longer in storage)");
                }
            }

            // Persist count for AndroidApp banner pickup on first frame.
            if (totalRemoved > 0)
            {
                SetInt(KeyPlaceholderPruneCount, totalRemoved);
            }

            SetBool(KeyV4PlaceholderPruneDone, true);
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.Storage",
                $"PruneKnownPlaceholdersOnce threw: {ex.GetType().Name}: {ex.Message}");
            // Don't stamp the flag on error — retry on next launch.
        }
    }

    /// <summary>
    /// v2.32.3 — count of placeholder entries removed by the most recent
    /// <see cref="PruneKnownPlaceholdersOnce"/> pass. Read by AndroidApp
    /// on first frame to render the migration banner; should be cleared
    /// after the banner is dismissed by the user (via
    /// <see cref="ClearPlaceholderPruneCount"/>).
    /// </summary>
    public static int GetPlaceholderPruneCount() => GetInt(KeyPlaceholderPruneCount, defaultValue: 0);

    /// <summary>Clear the banner counter so it doesn't show again on next launch.</summary>
    public static void ClearPlaceholderPruneCount() => SetInt(KeyPlaceholderPruneCount, 0);

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
            j => JsonSerializer.Deserialize<List<VlessServerEntry>>(j, JsonOptions));

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
            var json = JsonSerializer.Serialize(list, JsonOptions);
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
    /// Resolve the active server entry. Resolution order:
    /// <list type="number">
    ///   <item>Explicit selection by Name (Subscribe / Servers tab tap).</item>
    ///   <item>First server in the cached subscription pool (DEFCT-005
    ///   2026-05-10 fallback — see below).</item>
    ///   <item>Manual single-URI mode (<see cref="GetVlessUri"/>).</item>
    /// </list>
    /// Returns null only when truly nothing is configured. The connect path
    /// must surface that as an explicit "no server" error rather than
    /// silently picking a hardcoded placeholder — see MainActivity.cs.
    ///
    /// <para><b>DEFCT-005 (2026-05-10):</b> pre-fix this method only
    /// honoured an explicit <c>SetSelectedServerName</c>. Tapping a
    /// SubscribePage row's <i>name column</i> set the name, but tapping
    /// any other column on the same row didn't — so a user who added a
    /// subscription and immediately hit Start VPN got <c>null</c> here,
    /// fell through to a hardcoded placeholder VLESS URI in MainActivity,
    /// and connected to a dead test server. UI showed "Connected" but no
    /// traffic actually flowed (every VLESS handshake EOF'd). The
    /// auto-pick-first fallback closes the gap until the row tap target
    /// is widened to the whole row (separate UX polish task).</para>
    /// </summary>
    public static VlessServerEntry? GetActiveServer()
    {
        var selectedName = GetSelectedServerName();
        var servers = GetServers();

        // ── Tier 1: explicit selection by Name in the standalone list ───
        if (!string.IsNullOrEmpty(selectedName))
        {
            foreach (var s in servers)
            {
                if (string.Equals(s.Name, selectedName, System.StringComparison.OrdinalIgnoreCase))
                    return s;
            }
        }

        // ── Tier 2 (Bug-AND-023 v4, 2026-05-17): explicit selection by Name
        //    in any enabled subscription's Servers list.
        // Pre-v4 the connect resolver only knew about the aggregated
        // KeyServersJson pool, which SetSubscriptions kept in sync by
        // duplicating every subscription server into it. v4 stops the
        // duplication (see SetSubscriptions docstring); GetActiveServer
        // walks subscriptions in-memory instead. Same lookup cost, no
        // visible duplicate rows on the Servers tab.
        var subscriptions = GetSubscriptionsBare();
        if (!string.IsNullOrEmpty(selectedName))
        {
            foreach (var sub in subscriptions)
            {
                if (sub == null || !sub.Enabled || sub.Servers == null) continue;
                foreach (var srv in sub.Servers)
                {
                    if (srv == null) continue;
                    if (string.Equals(srv.Name, selectedName, System.StringComparison.OrdinalIgnoreCase))
                        return srv;
                }
            }
        }

        // ── Tier 3: DEFCT-005 fallback — first cached server in the
        //    standalone list. Persist the choice for the next render.
        if (servers.Count > 0)
        {
            var first = servers[0];
            if (!string.IsNullOrEmpty(first.Name))
                SetSelectedServerName(first.Name);
            return first;
        }

        // ── Tier 4 (Bug-AND-023 v4): no standalone selection? Pick the
        //    first server from the first enabled subscription. Same
        //    "auto-pick on first connect after add" affordance the
        //    aggregated-pool flow gave us pre-v4.
        foreach (var sub in subscriptions)
        {
            if (sub == null || !sub.Enabled || sub.Servers == null || sub.Servers.Count == 0) continue;
            var first = sub.Servers[0];
            if (first == null) continue;
            if (!string.IsNullOrEmpty(first.Name))
                SetSelectedServerName(first.Name);
            return first;
        }

        // ── Tier 5: legacy single-URI mode (manual paste / pre-2.32.0). ──
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

    /// <summary>
    /// Bug-AND-023 v4 (2026-05-17) — bare deserialize without the
    /// legacy-migration / recovery wrapping that <see cref="GetSubscriptions"/>
    /// applies. Used by <see cref="GetActiveServer"/> so we don't recurse
    /// into the prune-once cleanup (which itself reads subscriptions).
    /// Returns empty list on any parse error.
    /// </summary>
    private static List<SubscriptionEntry> GetSubscriptionsBare()
    {
        var json = GetString(KeySubscriptions);
        if (string.IsNullOrWhiteSpace(json)) return new List<SubscriptionEntry>();
        try
        {
            return JsonSerializer.Deserialize<List<SubscriptionEntry>>(json, JsonOptions) ?? new List<SubscriptionEntry>();
        }
        catch
        {
            return new List<SubscriptionEntry>();
        }
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
            j => JsonSerializer.Deserialize<List<string>>(j, JsonOptions));

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
            var json = JsonSerializer.Serialize(list, JsonOptions);
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
    // Content section parity with desktop NetworkPage (mirrors AppSettings.App.BlockAds).
    // Persists user intent today; AndroidConfigBuilder route wiring (geosite-ads → reject)
    // is a follow-up — desktop reads BlockAds in ConfigGenerator.cs:96 to inject AdGuard
    // DoH + an ads rule_set; Android still uses the user-supplied DNS unchanged.
    private const string KeyBlockAds = "block_ads";                    // bool
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

    // v2.40.0 AND-NODOZE (2026-06-02) — proactive battery-optimization prompt.
    // Set true once we've shown the user the native exemption dialog at their
    // first successful connect, so we ask exactly once and never nag. A user
    // who declines (or revokes later) can still grant from Settings →
    // Reliability. Pure C#-side flag — the Java service reads the live
    // PowerManager state, not this, so it doesn't need to stay in sync.
    private const string KeyBatteryOptPromptShown = "battery_opt_prompt_shown";

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

    // F6 (2026-06-15, plans/android-deep-qa-perf-2026-06-15.md) — RoutingMode
    // is no longer independent storage. On Android the VpnService per-app
    // filter (PerAppMode) is the only thing that drives split-vs-full: the
    // generated sing-box config is ALWAYS full-tunnel (AndroidConfigBuilder
    // hard-sets settings.App.RoutingMode="full"), so a separate routing_mode
    // key was dead state that drifted out of sync with PerAppMode (the Simple
    // page seeded its radio from PerAppMode while Advanced→Routing seeded from
    // routing_mode → contradictory radios, device-confirmed on A101BM where
    // Simple showed "All traffic" and Advanced showed "Split Tunnel" in the
    // same session). We now mirror desktop, where a SINGLE IsSplitTunnel bool
    // backs both the Simple and Settings routing radios: GetRoutingMode /
    // SetRoutingMode are pure projections of PerAppMode (the single source of
    // truth), so the two surfaces can never disagree. Every existing caller
    // (BuildSettingsRoutingSection, ReseedNetworkTabState, OnSettingsRouting-
    // Changed, ApplyProfile, AndroidConfigShare) keeps working unchanged.
    //
    // The legacy routing_mode SharedPreferences key is intentionally LEFT in
    // RepairAllOnLoad / ResetUserSettings (AllowedRoutingModes still backs the
    // self-repair spec) purely so stale values written by older installs get
    // validated + wiped on factory reset; nothing reads routing_mode anymore.
    public static string GetRoutingMode() =>
        VPNRouter.Core.Models.PerAppFilterMode.RoutingModeFor(GetPerAppMode());

    public static bool SetRoutingMode(string value)
    {
        var newPerAppMode = VPNRouter.Core.Models.PerAppFilterMode.PerAppModeForRoutingChange(
            value, GetPerAppMode(), GetPerAppLastMode());
        // null = no-op (already in the requested split/full state) — skip the
        // write so we don't churn PerAppMode or its sticky last-mode key.
        return newPerAppMode is null || SetPerAppMode(newPerAppMode);
    }

    public static bool GetBypassRussianTraffic() => GetBool(KeyBypassRussianTraffic, defaultValue: true);
    public static bool SetBypassRussianTraffic(bool value) => SetBool(KeyBypassRussianTraffic, value);

    public static bool GetBlockOnVpnFail() => GetBool(KeyBlockOnVpnFail, defaultValue: false);
    public static bool SetBlockOnVpnFail(bool value) => SetBool(KeyBlockOnVpnFail, value);

    public static string GetDnsStrategy() =>
        ValidateOrDefault(KeyDnsStrategy, GetString(KeyDnsStrategy), AllowedDnsStrategies, "ipv4_only");
    public static bool SetDnsStrategy(string value) => SetString(KeyDnsStrategy, value);

    public static bool GetBlockAds() => GetBool(KeyBlockAds, defaultValue: false);
    public static bool SetBlockAds(bool value) => SetBool(KeyBlockAds, value);

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

    // v2.40.0 AND-NODOZE — has the proactive battery-opt exemption dialog
    // been shown at least once? Default false so the first successful connect
    // triggers it.
    public static bool GetBatteryOptPromptShown() =>
        GetBool(KeyBatteryOptPromptShown, defaultValue: false);
    public static bool SetBatteryOptPromptShown(bool value) =>
        SetBool(KeyBatteryOptPromptShown, value);

    // ── AND-ADV-TOOLS-PUBLIC (2026-05-10) — Phase E persistence ─────────
    //
    // Public tab has two sub-tabs (Search / Saved). Persist the last-active
    // sub-tab so it stays selected across overlay opens (matches desktop
    // FreeConfigsPage's SelectedFreeTabIndex round-trip via settings.yaml).
    //
    // Stored as a bool (false = Search, true = Saved) — only 2 sub-tabs,
    // no need for an int-typed key. Default false (Search) matches desktop
    // first-launch behaviour.
    private const string KeyPublicActiveSubTab = "public_active_sub_tab";

    public static bool GetPublicActiveSubTabIsSaved() =>
        GetBool(KeyPublicActiveSubTab, defaultValue: false);
    public static bool SetPublicActiveSubTabIsSaved(bool value) =>
        SetBool(KeyPublicActiveSubTab, value);

    // ── AND-ADV-SHELL (2026-05-09): Advanced overlay last-active tab ────
    //
    // Stores the AdvancedTab enum NAME of the tab the user last viewed in
    // the tab-based Advanced overlay. Reopen lands on the same tab.
    //
    // AND-ADV-CHROME (2026-05-10): switched from int-index storage to
    // enum-name storage so the v2.32.0 desktop-parity rename
    // (Subscriptions→Subscribe, Network→Settings, FreeConfigs→Public,
    // Apps→Applications, DpiBypass+Telegram→Tools) doesn't silently shift
    // existing users to a different tab. Legacy stored values (old enum
    // names + old 0-based int indices) are translated to the new names on
    // read so an existing install keeps its last-active tab.
    //
    // Persisted as a string for parity with other GetString-based keys;
    // unrecognised values fall back to "Servers".
    private const string KeyAdvancedActiveTab = "advanced_active_tab";

    public static string GetAdvancedActiveTab()
    {
        var raw = GetString(KeyAdvancedActiveTab);
        if (string.IsNullOrEmpty(raw)) return "Servers";

        // Legacy int-index migration (pre-AND-ADV-CHROME — was Subscriptions=1,
        // Apps=2, Network=3, DpiBypass=4, Telegram=5, FreeConfigs=6).
        if (int.TryParse(raw, out var idx))
        {
            return idx switch
            {
                0 => "Servers",
                1 => "Subscribe",        // was "Subscriptions"
                2 => "Applications",     // was "Apps"
                3 => "Settings",         // was "Network"
                4 => "Tools",            // was "DpiBypass" — merged into Tools
                5 => "Tools",             // was "Telegram"   — merged into Tools
                6 => "Public",           // was "FreeConfigs"
                _ => "Servers",
            };
        }

        // Legacy enum-name migration — translate old names to new names so
        // the user's last-active tab survives the rename. Same mapping as
        // the int-index translation above.
        return raw switch
        {
            "Subscriptions" => "Subscribe",
            "Apps"          => "Applications",
            "Network"       => "Settings",
            "DpiBypass"     => "Tools",
            "Telegram"      => "Tools",
            "FreeConfigs"   => "Public",
            // Already a current name — pass through.
            "Servers" or "Subscribe" or "Settings" or "Applications" or "Tools" or "Public" => raw,
            // Unrecognised — fall back to Servers rather than crash on enum-parse.
            _ => "Servers",
        };
    }

    public static bool SetAdvancedActiveTab(string tabName)
        => SetString(KeyAdvancedActiveTab, tabName ?? "Servers");

    // Phase C (2026-05-10): remembers which Settings-tab sub-section the
    // user was last viewing so re-opening Advanced > Settings restores
    // the same pane. Stored as the integer index 0..5 matching desktop's
    // SelectedSettingsIndex (Routing / Rules / Leak / Content / Updates /
    // Autostart). Default 0 = Routing.
    private const string KeySettingsActiveSubSection = "settings_active_subsection";

    public static int GetSettingsActiveSubSection()
    {
        var raw = GetString(KeySettingsActiveSubSection);
        if (string.IsNullOrEmpty(raw)) return 0;
        return int.TryParse(raw, out var idx) && idx >= 0 && idx <= 5 ? idx : 0;
    }

    public static bool SetSettingsActiveSubSection(int index)
        => SetString(KeySettingsActiveSubSection, index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // ── Phase D (AND-ADV-APPS-CATEGORIES, 2026-05-10) — Applications tab ──
    //
    // Two pieces of state for the new category sidebar:
    //   • KeyApplicationsActiveCategory: internal id of the last-selected
    //     category ("Discord_Privacy", "Browsers", "Custom", a custom id…).
    //     Empty / null means no category selected — right pane shows the
    //     "← Select a category" placeholder.
    //   • KeyCustomCategoriesJson: list of user-defined CustomCategory
    //     entries (mirrors desktop AppSettings.CustomCategories shape). Each
    //     entry has Name + Apps[] (package names on Android) + Enabled.
    //     Built-in categories (Discord/Browsers/etc.) live in code via
    //     AndroidCategoryDefaults — only user-created ones are persisted.
    private const string KeyApplicationsActiveCategory = "applications_active_category";
    private const string KeyCustomCategoriesJson = "custom_categories_json";

    public static string? GetApplicationsActiveCategory()
        => GetString(KeyApplicationsActiveCategory);
    public static bool SetApplicationsActiveCategory(string? id)
        => SetString(KeyApplicationsActiveCategory, id);

    public static List<CustomCategory> GetCustomCategories()
    {
        var json = GetString(KeyCustomCategoriesJson);
        var result = StorageBlobRecovery.LoadOrRecover<List<CustomCategory>>(
            json,
            j => JsonSerializer.Deserialize<List<CustomCategory>>(j, JsonOptions));

        if (result.Loaded) return result.Value!;

        if (result.ShouldRecover)
        {
            QuarantineBadValue(KeyCustomCategoriesJson, json);
            StampRecoveryNotice(
                $"custom categories cache unreadable ({result.Reason}: {result.Detail}); reset to empty");
        }
        return new List<CustomCategory>();
    }

    public static bool SetCustomCategories(IEnumerable<CustomCategory>? cats)
    {
        try
        {
            if (cats is null) return SetString(KeyCustomCategoriesJson, null);
            var list = new List<CustomCategory>(cats);
            var json = JsonSerializer.Serialize(list, JsonOptions);
            return SetString(KeyCustomCategoriesJson, json);
        }
        catch
        {
            return false;
        }
    }

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
        // Phase 3B (2026-05-18) — migrated to System.Text.Json
        // [JsonPropertyName] attributes. Wire-format-compat with pre-3B
        // blobs: the snake_case keys (status / latency_ms / last_tested_at /
        // error) match exactly what the Newtonsoft [JsonProperty] writer
        // produced, so existing SharedPreferences entries deserialize
        // unchanged.
        [JsonPropertyName("status")]
        public int Status { get; set; }
        [JsonPropertyName("latency_ms")]
        public int LatencyMs { get; set; }
        [JsonPropertyName("last_tested_at")]
        public DateTimeOffset LastTestedAt { get; set; }
        [JsonPropertyName("error")]
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

    /// <summary>
    /// v2.32.0 (SR-3 parity): routes through
    /// <see cref="StorageBlobRecovery.LoadOrRecover{T}"/> so a corrupt
    /// payload is quarantined to <c>server_test_results__corrupt_{ts}</c>
    /// and surfaces a recovery notice — same shape as
    /// <see cref="GetSubscriptions"/> / <see cref="GetServers"/> /
    /// <see cref="GetPerAppPackages"/>. Returned dictionary is always
    /// wrapped with an OrdinalIgnoreCase comparer so casing drift on host
    /// names doesn't fragment the cache.
    /// </summary>
    public static Dictionary<string, ServerTestResultDto> GetServerTestResults()
    {
        var json = GetString(KeyServerTestResults);
        var result = StorageBlobRecovery.LoadOrRecover<Dictionary<string, ServerTestResultDto>>(
            json,
            j => JsonSerializer.Deserialize<Dictionary<string, ServerTestResultDto>>(j, JsonOptions));

        if (result.Loaded)
            return new Dictionary<string, ServerTestResultDto>(result.Value!, System.StringComparer.OrdinalIgnoreCase);

        if (result.ShouldRecover)
        {
            QuarantineBadValue(KeyServerTestResults, json);
            StampRecoveryNotice(
                $"server test history unreadable ({result.Reason}: {result.Detail}); reset to empty");
        }
        return new Dictionary<string, ServerTestResultDto>(System.StringComparer.OrdinalIgnoreCase);
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
            var json = JsonSerializer.Serialize(pruned, JsonOptions);
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
            // A4 (B3): Apply() — async, non-blocking. These are ordinary UI prefs
            // (tab/selection/per-app toggles); the service's recovery-critical
            // last-good-config is persisted separately via the Java apply() path.
            editor.Apply();
            return true;
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
            editor.Apply(); // A4 (B3): async, non-blocking — ordinary UI pref
            return true;
        }
        catch
        {
            return false;
        }
    }

    // v2.32.3 (2026-05-17) — int helpers added for KeyPlaceholderPruneCount.
    // Same pattern as Bool variants; reuse PrefsName + Application.Context.
    private static int GetInt(string key, int defaultValue)
    {
        try
        {
            var ctx = Application.Context;
            if (ctx == null) return defaultValue;
            var prefs = ctx.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            return prefs?.GetInt(key, defaultValue) ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static bool SetInt(string key, int value)
    {
        try
        {
            var ctx = Application.Context;
            if (ctx == null) return false;
            var prefs = ctx.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            if (prefs == null) return false;
            using var editor = prefs.Edit();
            if (editor == null) return false;
            editor.PutInt(key, value);
            editor.Apply(); // A4 (B3): async, non-blocking — ordinary UI pref
            return true;
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

    // ── F-10 kebab parity (2026-05-09) — Safe Mode flag ────────────────
    //
    // Mirrors desktop's `--safe` command-line flag without process args.
    // The kebab "Restart in Safe Mode" handler sets this to true, exits
    // the process, and the Application.OnCreate path on next launch reads
    // it via ConsumeSafeModeOnNextLaunch (one-shot) to skip auto-connect /
    // auto-update / heavy bootstrap so the user can recover from a crash
    // loop. Stored in SharedPreferences so it survives `JavaSystem.Exit`.
    private const string KeySafeModeOnNextLaunch = "safe_mode_on_next_launch";

    /// <summary>
    /// Queue safe-mode for the next process startup. Cleared on read by
    /// <see cref="ConsumeSafeModeOnNextLaunch"/> so a single setting only
    /// affects one launch.
    /// </summary>
    public static bool SetSafeModeOnNextLaunch(bool value)
        => SetBool(KeySafeModeOnNextLaunch, value);

    /// <summary>
    /// One-shot read+clear for the pending safe-mode flag. Returns true
    /// exactly once after <see cref="SetSafeModeOnNextLaunch"/> was called
    /// with true; subsequent reads return false until queued again.
    /// </summary>
    public static bool ConsumeSafeModeOnNextLaunch()
    {
        if (!GetBool(KeySafeModeOnNextLaunch, defaultValue: false)) return false;
        try { SetBool(KeySafeModeOnNextLaunch, false); }
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
                KeyBlockOnVpnFail, KeyDnsStrategy, KeyBlockAds, KeyUpdateChannel,
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
