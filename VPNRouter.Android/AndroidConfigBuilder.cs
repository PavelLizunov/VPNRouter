using System.Linq;
using System.Text.Json.Nodes;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 Android Phase 1.E (2026-05-04) — bridge between
/// <see cref="VPNRouter.Core"/> and the Java <c>VpnRouterService</c>.
///
/// <para>Pre-1.E the Activity passed a hand-rolled smoke-test config
/// (direct outbound, no proxy) just to verify libbox initialises and
/// the system TUN works. 1.E replaces that with a real VLESS-Reality
/// outbound generated through <see cref="ConfigGenerator.Generate"/> —
/// the same pipeline desktop uses, modulo Android-specific TUN handling
/// (the OS hands libbox the fd via the platform interface, libbox just
/// needs `inbounds[].type=tun` to route there).</para>
///
/// <para>This class deliberately stays minimal: it accepts a VLESS URI +
/// optional allowed-packages list and returns a sing-box JSON config.
/// Subscription URL fetch, profile selection, app-list UI etc. are
/// later phases (1.F / 1.G / 3). For now MainActivity calls this with
/// a hardcoded test URI; the next iteration will swap that for a
/// stored subscription URL pulled from
/// <see cref="SettingsLoader"/>.</para>
/// </summary>
public static class AndroidConfigBuilder
{
    /// <summary>
    /// Build a sing-box config JSON suitable for libbox.start() given
    /// a single <c>vless://</c> URI. Profile is empty (no process_name
    /// rules — Android filters per-app at <c>VpnService.Builder</c>
    /// level, not via sing-box). DNS mode <c>vpn_only</c> routes all
    /// DNS traffic through the VPN to avoid leaks.
    /// </summary>
    /// <param name="vlessUri">A single <c>vless://...</c> share-link URI.</param>
    /// <returns>Pretty-printed sing-box 1.13+ JSON.</returns>
    public static string BuildConfigJson(string vlessUri)
    {
        var entry = VlessUriParser.Parse(vlessUri);
        return BuildConfigJson(entry);
    }

    /// <summary>
    /// v3.0 Phase 1.H (2026-05-04) — overload that takes an already-parsed
    /// <see cref="VlessServerEntry"/>. Used by the subscription path which
    /// gets entries straight from <see cref="SubscriptionFetcher"/> without
    /// round-tripping back to a URI string.
    /// </summary>
    public static string BuildConfigJson(VlessServerEntry entry)
    {
        var settings = new AppSettings();
        // v3.0 Phase 3 (2026-05-04) — P0 fix.
        //
        // Pre-3 RoutingMode="split" was wrong on Android. Split-tunnel on
        // desktop uses sing-box's process_name rules to decide which app's
        // traffic gets proxied; the rest falls through to `route.final =
        // direct` and the `direct` outbound. On Android there are NO
        // process_name rules (Android's per-app filter is at the
        // VpnService.Builder.addAllowedApplication layer, not inside
        // sing-box), so the empty rule set + final=direct means
        // EVERYTHING goes to `direct` outbound — i.e. the VLESS server
        // is never reached, traffic is just looped through the TUN back
        // out the local interface. User-visible: VPN status icon shows
        // up but no traffic actually proxies.
        //
        // Fix: RoutingMode="full" so every routed packet reaches
        // `route.final = proxy` → VLESS outbound → upstream server.
        // Per-app filtering is still possible via
        // VpnService.Builder.addAllowed/Disallowed before establish() —
        // wired in VpnRouterService.openTun().
        settings.App.RoutingMode = "full";
        settings.App.LogLevel = "info";
        settings.App.ConfigMode = "generated";
        settings.Vless.Servers.Add(entry);

        // Empty profile — Android's per-app filtering happens at the
        // VpnService.Builder layer (addAllowedApplication / addDisallowedApplication)
        // BEFORE establish(), not via sing-box's `process_name` rules.
        // Passing an empty process list keeps ConfigGenerator from
        // emitting any process_name routing entries.
        var profile = new Profile
        {
            Name = "AndroidDefault",
            DnsMode = "vpn_only",
            BlockOnVpnFail = false,            // VpnService handles leak prevention via setBlocking
        };

        var processNames = System.Array.Empty<string>();
        var sbConfig = ConfigGenerator.Generate(profile, processNames, settings);

        var json = ConfigGenerator.Serialize(sbConfig);

        // v3.0 Android Phase 1.G+ (2026-05-04) — strip the desktop log path
        // from the generated config. ConfigGenerator unconditionally sets
        // log.output = AppPaths.SingBoxLogPath, which on Android resolves
        // to /data/data/com.ninitux.vpnrouter/files/.config/vpnrouter/logs/singbox.log
        // — a directory that doesn't exist (libbox sets up basePath +
        // workingPath + tempPath, but not the AppPaths-derived logs dir).
        // libbox throws "start logger: open ... no such file or directory"
        // and the service stopSelf's, leaving the UI stuck on "Connected"
        // (Phase 1.D intent-only).
        //
        // Cheapest fix: rewrite log.output to empty so libbox falls back
        // to stderr → logcat. Long-term Phase 2: make AppPaths Android-
        // aware (Application.Context.FilesDir-based) so the desktop log
        // file pattern carries over with all the rotation logic.
        return PatchLogPathForAndroid(json);
    }

    /// <summary>
    /// Strip <c>log.output</c> from a generated sing-box config so libbox
    /// emits logs to stderr (captured by Android logcat) instead of trying
    /// to open a desktop-style logs/singbox.log path that doesn't exist
    /// on the Android filesystem layout.
    /// </summary>
    private static string PatchLogPathForAndroid(string json)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null) return json;

            if (root["log"] is JsonObject logObj)
            {
                logObj.Remove("output");
                // Optional: keep level + timestamp; the absence of output
                // is what tells libbox "go to stderr".
            }

            return root.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch
        {
            // If parsing failed for any reason, return the original JSON
            // unchanged — libbox will surface its own error which is more
            // useful than us silently breaking the config.
            return json;
        }
    }
}
