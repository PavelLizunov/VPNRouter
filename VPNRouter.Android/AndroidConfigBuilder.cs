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
    public static string BuildConfigJson(string vlessUri, string? logOutputPath = null)
    {
        // v3.0 Phase 6.4 — multi-protocol parser (vless/hysteria2/tuic/ss).
        var entry = ServerUriParser.Parse(vlessUri);
        return BuildConfigJson(entry, logOutputPath);
    }

    /// <summary>
    /// v3.0 Phase 1.H (2026-05-04) — overload that takes an already-parsed
    /// <see cref="VlessServerEntry"/>. Used by the subscription path which
    /// gets entries straight from <see cref="SubscriptionFetcher"/> without
    /// round-tripping back to a URI string.
    ///
    /// <para>v3.0 Phase 6.1 (2026-05-04) — added <paramref name="logOutputPath"/>
    /// so MainActivity can route sing-box's own log to a world-readable file
    /// (<c>getExternalFilesDir()/singbox.log</c>). Pre-6.1 we only had the
    /// Go-stderr redirect set up via <c>Libbox.redirectStderr</c>, but sing-box
    /// errors that come from its internal logger (e.g. dial failures, TLS
    /// handshake failures, route mismatches) bypass stderr and go to its own
    /// log writer. With <c>log.output</c> unset, that writer falls back to
    /// stderr → logcat — but logcat on a non-rooted device only ships a
    /// truncated subset of the entry, and we couldn't see the actual sing-box
    /// error message. Routing log.output to a file we can <c>adb shell cat</c>
    /// finally surfaces those errors.</para>
    /// </summary>
    public static string BuildConfigJson(VlessServerEntry entry, string? logOutputPath = null)
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
        // v2.32.0 — surface persisted settings into the generated config
        // so the Android Settings overlay actually affects the sing-box
        // pipeline (handbook §1.5 — "settings must do something").
        // BypassRussianTraffic + ForceIpv4Only are read by ConfigGenerator
        // when building DNS / route rules. The other Settings flags
        // (BlockOnVpnFail / Autostart*) live at the VpnService.Builder
        // layer or in the AndroidStorage cache without affecting the
        // sing-box JSON.
        settings.App.BypassRussianTraffic = AndroidStorage.GetBypassRussianTraffic();
        settings.App.ForceIpv4Only = AndroidStorage.GetDnsStrategy() switch
        {
            "prefer_ipv6" => false,
            "prefer_ipv4" => false,
            _ => true,                          // ipv4_only (default)
        };
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
            BlockOnVpnFail = AndroidStorage.GetBlockOnVpnFail(),
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
        var patched = PatchLogPathForAndroid(json, logOutputPath);

        // v2.32.0 (AND-ZAPRET, 2026-05-07) — Android equivalent of desktop's
        // Zapret feature. Mutates the generated config to add tls_fragment
        // / udp_fragment to all real proxy outbounds when the user has
        // picked a non-"off" DPI bypass mode in AndroidStorage. No-op
        // when the mode is "off" (the default), so unrelated build paths
        // — tests, fresh installs, users who never enabled DPI bypass —
        // get exactly the pre-AND-ZAPRET JSON.
        return InjectDpiBypass(patched, AndroidStorage.GetDpiBypassMode());
    }

    /// <summary>
    /// v2.32.0 (AND-CC, 2026-05-07) — build a sing-box config from a
    /// user-pasted raw JSON. Mirrors desktop's "Custom" ConfigMode flow
    /// (VPNRouter.Core.Services.CustomConfigInjector). The raw JSON is
    /// run through Inject + StripUnsupportedFeatures so we get all the
    /// sing-box 1.13 migration logic (legacy DNS schemas, dns/block
    /// outbound types, missing default_domain_resolver, etc.) for free.
    ///
    /// <para>Process-name routing rules are NOT injected: Android does
    /// per-app filtering at the VpnService.Builder layer
    /// (addAllowed/Disallowed), not via sing-box's process_name (which
    /// requires kernel /proc reads that don't work on non-rooted
    /// Android). We pass an empty <c>processNames</c> set so the
    /// injector skips that branch. The user's raw JSON's existing route
    /// rules + outbounds are preserved verbatim.</para>
    ///
    /// <para><see cref="CustomConfigInjector.Inject"/> calls
    /// <c>EnsureClashApi(...)</c> which writes the desktop default
    /// (<c>127.0.0.1:9090</c>) — harmless on Android, libbox doesn't
    /// expose that port to the network namespace anyway. We don't drop
    /// it because the desktop "Stats" path may want to read it later.</para>
    /// </summary>
    public static string BuildConfigJsonFromCustom(string rawJson, string? logOutputPath = null)
    {
        var settings = new AppSettings();
        // Same Android-specific routing semantics as the
        // BuildConfigJson(VlessServerEntry) path: full tunnel (no
        // process_name rules), info-level log. RoutingMode controls
        // whether CustomConfigInjector forces route.final="direct"
        // (split) — on Android we want it to stay whatever the user
        // pasted, so "full" tunnel is the safer default. The user's
        // route.final wins if they set one in the pasted JSON.
        settings.App.RoutingMode = "full";
        settings.App.LogLevel = "info";
        settings.App.ConfigMode = "custom";
        settings.App.BypassRussianTraffic = AndroidStorage.GetBypassRussianTraffic();
        settings.App.ForceIpv4Only = AndroidStorage.GetDnsStrategy() switch
        {
            "prefer_ipv6" => false,
            "prefer_ipv4" => false,
            _ => true,
        };

        // Empty process list — Android per-app filter is at the
        // VpnService.Builder layer, not sing-box's process_name. The
        // injector still re-runs StripUnsupportedFeatures on the JSON
        // so legacy schemas migrate cleanly.
        var processNames = System.Array.Empty<string>();

        var injectedJson = CustomConfigInjector.Inject(rawJson, processNames, settings);

        // Reuse the desktop-→Android post-processor (auto_route=false,
        // stack=gvisor, MTU=1500, log.output rewrite). It's tolerant of
        // arbitrary user JSON because it parses, walks, and re-emits;
        // anything it doesn't recognise stays untouched.
        var patched = PatchLogPathForAndroid(injectedJson, logOutputPath);

        // v2.32.0 (AND-ZAPRET, 2026-05-07) — apply DPI-bypass on the
        // user-pasted custom JSON too. The user might paste a clean
        // Hysteria2 config and toggle DPI bypass on — we still inject
        // tls_fragment so the bypass applies regardless of which
        // ConfigMode they chose. Mode="off" makes this a no-op.
        return InjectDpiBypass(patched, AndroidStorage.GetDpiBypassMode());
    }

    /// <summary>
    /// v3.0 Phase 4 (2026-05-04) — strip the desktop-only log path AND
    /// adjust the TUN inbound for Android.
    ///
    /// <para>Pre-4: kept TUN inbound with desktop's
    /// <c>auto_route=true, strict_route=false</c> — but on Android libbox
    /// owns TUN via the openTun callback in VpnRouterService.java, NOT
    /// sing-box's auto_route. Leaving auto_route=true makes sing-box try
    /// to manipulate kernel routes itself (which Android doesn't permit
    /// from a non-root app), producing silent failures that look like
    /// "tunnel up but no traffic flows".</para>
    ///
    /// <para>Phase 4 fix: <c>auto_route=false</c> on Android — sing-box
    /// just reads/writes the TUN fd we hand it via libbox; the
    /// VpnService.Builder routes are what actually direct kernel
    /// packets into the TUN.</para>
    /// </summary>
    /// <summary>
    /// v2.32.0 (AND-ZAPRET, 2026-05-07) — Android-native DPI bypass via
    /// sing-box's <c>tls_fragment</c> / <c>udp_fragment</c> outbound dialer
    /// options. Mirrors desktop's Zapret UX intent (winws.exe + WinDivert)
    /// with a config-only mechanism that works on non-rooted Android —
    /// no external userspace process, no kernel module, no /proc reads.
    ///
    /// <para>Implementation lives in
    /// <see cref="VPNRouter.Core.Services.AndroidDpiBypassInjector"/> so
    /// it can be unit-tested by <c>VPNRouter.Tests</c> without
    /// ProjectReference'ing the Android-targeted assembly. This wrapper
    /// stays as the public Android-side entry-point so callers don't
    /// need to know about the Core helper.</para>
    /// </summary>
    /// <param name="json">A pretty-printed sing-box JSON config.</param>
    /// <param name="mode">"off" | "standard" | "aggressive".</param>
    /// <returns>JSON with the strategy applied to all proxy outbounds.</returns>
    public static string InjectDpiBypass(string json, string mode)
        => AndroidDpiBypassInjector.Inject(json, mode);

    private static string PatchLogPathForAndroid(string json, string? logOutputPath)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null) return json;

            // 1. log.output → either logOutputPath (Phase 6.1, world-readable
            //    file under getExternalFilesDir()) or remove entirely so libbox
            //    falls back to stderr → logcat.
            if (root["log"] is JsonObject logObj)
            {
                if (!string.IsNullOrEmpty(logOutputPath))
                {
                    logObj["output"] = logOutputPath;
                }
                else
                {
                    logObj.Remove("output");
                }
                // Bump log level to debug for now so we can see why
                // upstream connections fail. Phase 5 dial back to info
                // once routing is solid.
                logObj["level"] = "debug";
            }

            // 2. inbounds[*].type=tun → set Android-friendly TUN options
            if (root["inbounds"] is JsonArray inbounds)
            {
                foreach (var inboundNode in inbounds)
                {
                    if (inboundNode is not JsonObject inb) continue;
                    var type = inb["type"]?.GetValue<string>();
                    if (type != "tun") continue;

                    // libbox owns the TUN — don't let sing-box mess with
                    // kernel routes (auto_route requires root on Android).
                    inb["auto_route"] = false;
                    inb["strict_route"] = false;

                    // v3.0 Phase 6.3 (2026-05-04) — TCP routing fix.
                    //
                    // Pre-6.3 we inherited stack="system" from the desktop
                    // ConfigGenerator. On Linux desktop the system stack
                    // works because the daemon runs with CAP_NET_ADMIN +
                    // CAP_NET_RAW. On Android (non-rooted) sing-box has
                    // neither — so the system stack silently passes UDP
                    // (which doesn't need raw sockets to receive) but TCP
                    // SYN packets are dropped before they can reach the
                    // sing-box TCP handler. Symptom: UDP/QUIC traffic
                    // proxies cleanly; every TCP HTTPS request times out.
                    //
                    // Switch to "gvisor" — pure user-mode TCP/IP stack
                    // written in Go, ships inside libbox.aar via the
                    // with_gvisor build tag. Works without privileged
                    // capabilities. Slightly slower than system stack but
                    // correctness > a few % CPU.
                    inb["stack"] = "gvisor";

                    // v3.0 Phase 6.3 — drop MTU 9000 → 1500. The desktop
                    // 9000 MTU was tuned for Wireguard-style native TUN
                    // where the kernel handles fragmentation. On Android
                    // the VpnService.Builder MTU must match what the OS
                    // can actually deliver, and the underlying network
                    // (wifi/cellular) is almost always 1500 or smaller.
                    // Setting 9000 means every IP packet ≥1500 bytes from
                    // an app gets fragmented or dropped before reaching
                    // sing-box, which can manifest as connection hangs.
                    //
                    // 1500 matches Android system VPN default and what
                    // sing-box-for-android uses out of the box.
                    inb["mtu"] = 1500;
                }
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
