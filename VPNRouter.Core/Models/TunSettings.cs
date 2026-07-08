using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public class TunSettings
{
    public const int DefaultMtu = 1420;

    public static readonly string[] MandatoryLocalRouteExcludeAddress =
    {
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "169.254.0.0/16",
        "127.0.0.0/8",
        "::1/128",
        "fe80::/10",
        "fc00::/7"
    };

    [YamlMember(Alias = "interface_name")]
    public string InterfaceName { get; set; } = "VPNRouter-TUN";

    [YamlMember(Alias = "ipv4_address")]
    public string Ipv4Address { get; set; } = "172.19.0.1/30";

    [YamlMember(Alias = "ipv6_enabled")]
    public bool Ipv6Enabled { get; set; } = false;

    // v2.46.0-r10: default 1420. Roblox/VLESS path probing showed 1420 passes
    // while 1423 fragments; 1280 was too low for Steam SDR-class game UDP
    // (~1328B IP packets). Users on narrow mobile/PPPoE/nested-VPN paths can
    // still set 1400/1380 explicitly.
    [YamlMember(Alias = "mtu")]
    public int Mtu { get; set; } = DefaultMtu;

    [YamlMember(Alias = "auto_route")]
    public bool AutoRoute { get; set; } = true;

    [YamlMember(Alias = "strict_route")]
    public bool StrictRoute { get; set; } = false;

    /// <summary>
    /// Exclude specific address ranges from TUN routing.
    /// Traffic to these addresses bypasses TUN and uses the system routing table.
    /// Useful for coexistence with other VPNs (e.g. WireGuard/AmneziaWG subnets).
    /// Example: ["10.9.1.0/24", "192.168.100.0/24"]
    ///
    /// <para>This is the USER-AUTHORED list and the ONLY one persisted to
    /// config.yaml. Auto-detected WG/AWG subnets live in the runtime-only
    /// <see cref="AutoDetectedExcludeAddress"/> and are folded in at
    /// config-generation time via <see cref="GetEffectiveRouteExcludeAddress"/>
    /// — they are deliberately never merged here, so a vanished adapter or a
    /// network change can never leave a stale exclude persisted forever.</para>
    /// </summary>
    [YamlMember(Alias = "route_exclude_address")]
    public List<string> RouteExcludeAddress { get; set; } = new();

    /// <summary>
    /// WireGuard/AmneziaWG subnets auto-detected from the live network
    /// interfaces (see <see cref="VPNRouter.Core.Services.NetworkInterfaceDetector"/>),
    /// recomputed fresh on every connect by the startup pipeline.
    ///
    /// <para><b>RUNTIME-ONLY — never serialized.</b> Marked <see cref="YamlIgnoreAttribute"/>
    /// (+ <see cref="JsonIgnoreAttribute"/> for defence-in-depth) precisely
    /// because the previous design merged these into the persisted
    /// <see cref="RouteExcludeAddress"/> additively-and-never-pruned: once an
    /// auto-detected subnet (e.g. 10.9.1.0/24 widened from a /32 point-to-point)
    /// was written to config.yaml it survived forever, even after the WG/AWG
    /// adapter was gone or the user moved networks — sending that subnet DIRECT
    /// (bypassing the VPN) where it shouldn't, or excluding a now-unrelated LAN
    /// range. Keeping the auto set out-of-band means it is present only while
    /// the adapter is, and is dropped the instant it isn't.</para>
    /// </summary>
    [YamlIgnore]
    [JsonIgnore]
    public List<string> AutoDetectedExcludeAddress { get; set; } = new();

    /// <summary>
    /// The EFFECTIVE TUN route-exclude set used to build the sing-box config
    /// and the TUN-change fingerprint: the persisted user list
    /// (<see cref="RouteExcludeAddress"/>), mandatory local networks, then the
    /// freshly auto-detected WG/AWG subnets
    /// (<see cref="AutoDetectedExcludeAddress"/>), de-duplicated
    /// case-insensitively. User entries are preserved verbatim and win on
    /// collision; the persisted list itself is never mutated.
    /// </summary>
    public List<string> GetEffectiveRouteExcludeAddress()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // User-authored entries first, preserved verbatim so the generated
        // config wire-format for an unchanged user list is byte-identical.
        if (RouteExcludeAddress != null)
        {
            foreach (var s in RouteExcludeAddress)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (seen.Add(s.Trim())) result.Add(s);
            }
        }

        foreach (var s in MandatoryLocalRouteExcludeAddress)
        {
            if (seen.Add(s)) result.Add(s);
        }

        // Freshly auto-detected WG/AWG subnets, only those not already covered
        // by a user entry (case/whitespace-insensitive).
        if (AutoDetectedExcludeAddress != null)
        {
            foreach (var s in AutoDetectedExcludeAddress)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (seen.Add(s.Trim())) result.Add(s);
            }
        }

        return result;
    }
}
