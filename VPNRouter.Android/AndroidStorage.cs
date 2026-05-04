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

    // ── Phase 1.F: single-URI manual mode ───────────────────────────────────

    public static string? GetVlessUri() => GetString(KeyVlessUri);
    public static bool SetVlessUri(string? value) => SetString(KeyVlessUri, value);

    // ── Phase 1.H: subscription mode ────────────────────────────────────────

    public static string? GetSubscriptionUrl() => GetString(KeySubscriptionUrl);
    public static bool SetSubscriptionUrl(string? value) => SetString(KeySubscriptionUrl, value);

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
                return VPNRouter.Core.Services.VlessUriParser.Parse(manualUri);
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
    /// "dark" because the desktop UI ships dark by default and the design
    /// tokens are tuned for it.
    /// </summary>
    public static string GetTheme() => GetString(KeyTheme) ?? "dark";
    public static bool SetTheme(string? value) => SetString(KeyTheme, value);

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
}
