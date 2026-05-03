using System.Linq;
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

        var settings = new AppSettings();
        settings.App.RoutingMode = "split";       // Android: VpnService allow-list governs which apps go through TUN
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

        return ConfigGenerator.Serialize(sbConfig);
    }
}
