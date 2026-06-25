using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public class TunSettings
{
    [YamlMember(Alias = "interface_name")]
    public string InterfaceName { get; set; } = "VPNRouter-TUN";

    [YamlMember(Alias = "ipv4_address")]
    public string Ipv4Address { get; set; } = "172.19.0.1/30";

    [YamlMember(Alias = "ipv6_enabled")]
    public bool Ipv6Enabled { get; set; } = false;

    // v2.42.0-r3: was 9000 (sing-box jumbo default). With stack=system that
    // 9000-byte TUN MTU put oversized HTTP/2 segments on the wire that the real
    // 1500-MTU path can't carry; with PMTUD broken they were RST -> browsers got
    // ERR_CONNECTION_CLOSED on YouTube/Google over TCP-only (VLESS) proxies while
    // small clients (curl --http1.1, PowerShell) squeaked through and UDP/QUIC
    // proxies bypassed it. 1280 (IPv6 minimum) traverses ANY path. Confirmed via
    // diagnose.ps1 on a real user (h2 FAIL + tun mtu 9000). SettingsMigrator
    // v5->v6 lowers existing 9000 configs.
    [YamlMember(Alias = "mtu")]
    public int Mtu { get; set; } = 1280;

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
    /// (<see cref="RouteExcludeAddress"/>) followed by the freshly
    /// auto-detected WG/AWG subnets (<see cref="AutoDetectedExcludeAddress"/>),
    /// de-duplicated case-insensitively. User entries are preserved verbatim
    /// and win on collision; the persisted list itself is never mutated.
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
