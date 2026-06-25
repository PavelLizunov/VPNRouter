using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public class AppSettings
{
    /// <summary>
    /// Schema version for forward-compatibility migrations. Bumped only
    /// when the yaml layout changes in a breaking way (renamed field,
    /// dropped section, etc.). Older configs get picked up by
    /// <see cref="VPNRouter.Core.Services.SettingsMigrator"/> and
    /// rewritten to the current schema on load.
    ///
    /// <para>v3 bump (2026-05-11, AM-1 + F-B): adds
    /// <see cref="AppConfig.RoutingAppsMode"/> + the include/exclude
    /// split lists, and seeds the F-B legacy <c>vless.servers</c>
    /// cleanup pass for users with shadow-override entries from a
    /// pre-subscription manual paste. See
    /// <c>plans/r10-stas-confirmed-and-apps-2mode.md</c> §1 Fix-B / §2.</para>
    ///
    /// <para>v4 bump (2026-05-12, W-2): wgturn-cli binary moved from
    /// shared <c>bin/</c> into dedicated <c>wgturn/bin/</c> subtree
    /// (parallel to <c>zapret/</c>, <c>tg-proxy/</c>) ahead of the W-1
    /// on-demand download flow. Migration moves any pre-existing
    /// binary + version stamp out of the legacy location. See
    /// <c>plans/wgturn-on-demand-download.md</c> §3 + §5.</para>
    ///
    /// <para>v5 bump (2026-05-19, Wave 39): adds
    /// <see cref="AppConfig.DnsLeakLockdown"/> firewall-level DNS-port
    /// block toggle. <strong>BR-10 (2026-05-20, post-v2.35.0)</strong>:
    /// default is <c>false</c> for ALL installs (fresh + upgrade). User
    /// must explicitly enable via Settings → Leak Protection. Original
    /// BR-5 default-on was too disruptive for LAN-DNS-proxy users
    /// (dnscrypt-proxy, AdGuard Home on a sibling NIC); sing-box already
    /// routes app DNS via VLESS:443 (DoH), so the firewall block is
    /// belt-and-suspenders defense-in-depth, not a baseline requirement.
    /// See <c>plans/hotfix-dns-leak-firewall-lockdown-2026-05-19.md</c>.</para>
    /// </summary>
    public const int CurrentSchemaVersion = 6;

    [YamlMember(Alias = "schema_version")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [YamlMember(Alias = "app")]
    public AppConfig App { get; set; } = new();

    [YamlMember(Alias = "profile_sources")]
    public List<ProfileSource> ProfileSources { get; set; } = new();

    [YamlMember(Alias = "active_profile")]
    public string ActiveProfile { get; set; } = string.Empty;

    [YamlMember(Alias = "vless")]
    public VlessConfig Vless { get; set; } = new();

    [YamlMember(Alias = "tun")]
    public TunSettings Tun { get; set; } = new();

    [YamlMember(Alias = "dns")]
    public DnsSettings Dns { get; set; } = new();

    [YamlMember(Alias = "singbox")]
    public SingBoxSettings SingBox { get; set; } = new();

    [YamlMember(Alias = "monitoring")]
    public MonitoringSettings Monitoring { get; set; } = new();

    /// <summary>
    /// Custom app exe names added by user via GUI (e.g. ["spotify.exe", "slack.exe"]).
    /// These are added as a "_custom" profile with each exe as a process rule.
    /// </summary>
    [YamlMember(Alias = "custom_apps")]
    public List<string> CustomApps { get; set; } = new();

    /// <summary>
    /// User-added apps per group (Discord, Browsers, etc.). Merged with
    /// defaults from profiles/default.json on load. Allows adding/removing
    /// custom apps in any group without touching the bundled defaults.
    /// </summary>
    [YamlMember(Alias = "custom_group_apps")]
    public Dictionary<string, List<string>> CustomGroupApps { get; set; } = new();

    /// <summary>User-created categories (beyond the bundled default groups).</summary>
    [YamlMember(Alias = "custom_categories")]
    public List<CustomCategory> CustomCategories { get; set; } = new();

    /// <summary>
    /// v2.32.x (Bug-r9-I): process names the user has individually unchecked
    /// inside an active default profile group (e.g. unchecked Firefox inside
    /// the Browsers group so RU sites stay on the direct route). Pre-r9-I the
    /// per-app checkbox was a transient view state — only the group's
    /// IsChecked was persisted via <see cref="ActiveProfile"/>. The
    /// unchecked specific app reverted on every restart. Persisting the
    /// exclusion list closes that gap.
    ///
    /// <para>Entries are stored in the same form as
    /// <c>AppItemViewModel.ProcessName</c> (with <c>.exe</c> suffix on
    /// Windows, stripped on macOS/Linux). <see cref="VPNRouter.Core.Services.VpnEngine"/>
    /// normalises both sides at filter time so the suffix variance does
    /// not matter at the engine layer.</para>
    /// </summary>
    [YamlMember(Alias = "excluded_apps")]
    public List<string> ExcludedApps { get; set; } = new();

    [YamlMember(Alias = "update")]
    public UpdateSettings Update { get; set; } = new();

    /// <summary>
    /// r9 Phase 2 — emergency fallback channel settings (wgturn-core
    /// integration). Persists the share URL + VK link the user pasted
    /// in the future Phase-3 UI. The actual lifecycle service is
    /// <see cref="VPNRouter.Core.Services.EmergencyChannel.EmergencyChannelEngine"/>.
    /// </summary>
    [YamlMember(Alias = "emergency_channel")]
    public EmergencyChannelSettings EmergencyChannel { get; set; } = new();
}

/// <summary>
/// A single proxy server entry with all connection parameters.
///
/// <para>Despite the legacy name <c>VlessServerEntry</c>, this type now
/// holds entries for any supported protocol (VLESS+Reality, Hysteria2,
/// TUIC v5, Shadowsocks 2022, optionally with ShadowTLS v3 plugin or
/// Hysteria2 Salamander obfuscation). The <see cref="Protocol"/>
/// discriminator controls which subset of fields is meaningful at
/// outbound-generation time. The class name is preserved to avoid
/// breaking the existing YAML alias mapping.</para>
/// </summary>
public class VlessServerEntry
{
    /// <summary>Optional display name for logs (e.g. "main", "backup")</summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Protocol discriminator. One of:
    /// <list type="bullet">
    /// <item><c>vless</c> (default) — VLESS+Reality, optionally over TCP/WS/gRPC</item>
    /// <item><c>hysteria2</c> — Hysteria2 (with optional Salamander obfs)</item>
    /// <item><c>tuic</c> — TUIC v5</item>
    /// <item><c>shadowsocks</c> — Shadowsocks 2022 (with optional ShadowTLS v3 plugin)</item>
    /// <item><c>naive</c> — NaiveProxy (HTTP/2 or HTTP/3 via Cronet; Windows + Linux only)</item>
    /// <item><c>dns-tunnel</c> — VLESS tunnelled over DNS via a local slipstream-client
    /// sidecar (last-resort transport; Windows + Linux only). Carries
    /// <see cref="DnsDomain"/> / <see cref="DnsResolvers"/> / <see cref="DnsLeafFingerprint"/>
    /// + reuses <see cref="Uuid"/>; the VLESS outbound is generated against
    /// 127.0.0.1:&lt;localPort&gt; with no TLS (the tunnel does its own QUIC-TLS).</item>
    /// </list>
    /// New entries default to "vless" so legacy settings.yaml without
    /// this field stays valid.
    /// </summary>
    [YamlMember(Alias = "protocol")]
    public string Protocol { get; set; } = "vless";

    /// <summary>
    /// True when this entry is a DNS-tunnel (slipstream) server. Field-based
    /// (not just the <see cref="Protocol"/> string) so it survives a
    /// serialization round-trip that drops <c>Protocol</c> back to its "vless"
    /// default. v2.42.0-r2 symptom: a dns-tunnel server ran fine on Android but
    /// its list subtitle showed "tcp + reality" because the JSON cache lost
    /// <c>Protocol</c> while the dns-tunnel payload (<see cref="DnsDomain"/> /
    /// <see cref="DnsResolvers"/> / <see cref="DnsLeafCertPem"/>) survived — and
    /// those fields ONLY exist on a dns-tunnel entry, so their presence
    /// unambiguously identifies one.
    /// </summary>
    [YamlIgnore]
    [JsonIgnore]
    public bool IsDnsTunnel =>
        string.Equals(Protocol, "dns-tunnel", System.StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(DnsDomain)
        || (DnsResolvers != null && DnsResolvers.Count > 0)
        || !string.IsNullOrWhiteSpace(DnsLeafCertPem);

    [YamlMember(Alias = "server")]
    public string Server { get; set; } = string.Empty;

    [YamlMember(Alias = "port")]
    public int Port { get; set; } = 443;

    [YamlMember(Alias = "uuid")]
    public string Uuid { get; set; } = string.Empty;

    [YamlMember(Alias = "flow")]
    public string Flow { get; set; } = string.Empty;

    [YamlMember(Alias = "security")]
    public string Security { get; set; } = "reality";

    [YamlMember(Alias = "reality")]
    public VlessRealityConfig Reality { get; set; } = new();

    [YamlMember(Alias = "tls")]
    public VlessTlsConfig Tls { get; set; } = new();

    [YamlMember(Alias = "transport")]
    public VlessTransportConfig Transport { get; set; } = new();

    // ── Non-VLESS fields ────────────────────────────────────────────────────
    // All optional. Populated only when Protocol != "vless".

    /// <summary>
    /// NaiveProxy basic-auth username (paired with <see cref="Password"/>).
    /// Empty for non-naive protocols.
    /// </summary>
    [YamlMember(Alias = "username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Authentication password.
    /// <list type="bullet">
    /// <item>Hysteria2 — auth password (URL userinfo)</item>
    /// <item>TUIC — auth password (URL userinfo, paired with Uuid)</item>
    /// <item>Shadowsocks — encryption key / password (paired with Method)</item>
    /// <item>NaiveProxy — auth password (paired with Username)</item>
    /// </list>
    /// </summary>
    [YamlMember(Alias = "password")]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Shadowsocks cipher method (e.g. <c>2022-blake3-aes-256-gcm</c>,
    /// <c>aes-256-gcm</c>, <c>chacha20-ietf-poly1305</c>). Ignored for
    /// non-Shadowsocks protocols.
    /// </summary>
    [YamlMember(Alias = "method")]
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// TUIC congestion-control algorithm. <c>bbr</c> | <c>cubic</c> |
    /// <c>new_reno</c>. Default <c>bbr</c>.
    /// </summary>
    [YamlMember(Alias = "congestion_control")]
    public string CongestionControl { get; set; } = "bbr";

    /// <summary>
    /// TUIC UDP relay mode. <c>native</c> | <c>quic</c>.
    /// </summary>
    [YamlMember(Alias = "udp_relay_mode")]
    public string UdpRelayMode { get; set; } = "native";

    /// <summary>
    /// Hysteria2 Salamander obfuscation. Empty = obfs disabled.
    /// </summary>
    [YamlMember(Alias = "obfs_type")]
    public string ObfsType { get; set; } = string.Empty;

    /// <summary>Salamander password (paired with <see cref="ObfsType"/> = "salamander").</summary>
    [YamlMember(Alias = "obfs_password")]
    public string ObfsPassword { get; set; } = string.Empty;

    /// <summary>
    /// Shadowsocks plugin name (e.g. <c>shadow-tls</c> for ShadowTLS v3).
    /// Empty = no plugin.
    /// </summary>
    [YamlMember(Alias = "plugin")]
    public string Plugin { get; set; } = string.Empty;

    /// <summary>
    /// Shadowsocks plugin opts (semicolon-delimited <c>key=value</c> pairs:
    /// <c>version=3;password=xxx;host=cdn.example.com</c>).
    /// </summary>
    [YamlMember(Alias = "plugin_opts")]
    public string PluginOpts { get; set; } = string.Empty;

    /// <summary>
    /// r5: co-located server pairing tag from the subscription (<c>pair=</c>
    /// query param). NaiveProxy can't carry UDP, so a naive server and its
    /// same-node UDP-capable sibling (e.g. Hysteria2) share an identical
    /// PairGroup; VPNRouter routes UDP through the sibling (same physical node →
    /// same exit IP). Empty = no pairing.
    /// </summary>
    [YamlMember(Alias = "pair")]
    public string PairGroup { get; set; } = string.Empty;

    /// <summary>
    /// r7 #1: NaiveProxy HTTP/3-over-QUIC transport. Set from a
    /// <c>naive+quic://</c> share-link; emitted as the naive outbound's
    /// <c>quic</c> boolean. False (default) = HTTP/2. Naive-only.
    /// </summary>
    [YamlMember(Alias = "naive_quic")]
    public bool NaiveQuic { get; set; }

    // ── DNS-tunnel (slipstream) fields ──────────────────────────────────────
    // Populated only when Protocol == "dns-tunnel". Parsed from a
    // dns-tunnel:// share-link (base64url-JSON payload). See
    // plans/dns-tunnel-slipstream-integration-2026-06-10.md.

    /// <summary>
    /// dns-tunnel: the tunnel domain (slipstream-client <c>-d</c>). Also mirrored
    /// into <see cref="Server"/> for dedup/display identity. Empty for other
    /// protocols.
    /// </summary>
    [YamlMember(Alias = "dns_domain")]
    public string DnsDomain { get; set; } = string.Empty;

    /// <summary>
    /// dns-tunnel: НСДИ resolver endpoints (<c>ip:port</c>, e.g.
    /// <c>195.208.4.1:53</c>) — slipstream-client repeats <c>-r</c> for each
    /// (multipath). Empty for other protocols.
    /// </summary>
    [YamlMember(Alias = "dns_resolvers")]
    public List<string> DnsResolvers { get; set; } = new();

    /// <summary>
    /// dns-tunnel: when true, prefer the OS/operator default resolver(s) discovered
    /// at connect time over the hardcoded <see cref="DnsResolvers"/> (desktop: the
    /// active NIC's DNS servers; Android: the active network's
    /// <c>ConnectivityManager.getLinkProperties().getDnsServers()</c>). Set by a
    /// <c>"system"</c>/<c>"auto"</c>/<c>"os"</c> sentinel token in the link's <c>r</c>
    /// array. This is the operator-agnostic WL-BYPASS path: on a strict RU mobile
    /// whitelist the only reachable DNS is the operator's own resolver, so a link
    /// cannot hardcode НСДИ IPs and work for every operator. Any concrete IPs that
    /// ALSO appear in <c>r</c> stay in <see cref="DnsResolvers"/> as a fallback for
    /// when the OS resolver can't be discovered.
    /// </summary>
    [YamlMember(Alias = "dns_use_system_resolver")]
    public bool DnsUseSystemResolver { get; set; }

    /// <summary>
    /// dns-tunnel: OPTIONAL authoritative DNS endpoint(s) (<c>ip:port</c>, e.g.
    /// <c>213.155.15.93:53</c>) — slipstream-client repeats <c>--authoritative</c>
    /// for each. Queries the tunnel server's authoritative NS DIRECTLY, bypassing
    /// the recursive resolver. The recursive НСДИ resolvers rate-limit the covert
    /// query stream after ~1.5-3 min (→ QUIC idle-timeout 0x433); the authoritative
    /// path has no such limit, so where the network allows direct UDP to it the
    /// tunnel is stable. Passed ALONGSIDE <see cref="DnsResolvers"/> (multipath:
    /// authoritative when reachable, recursive as the censorship-resilient
    /// fallback). Empty for other protocols / when the server publishes none.
    /// </summary>
    [YamlMember(Alias = "dns_authoritative")]
    public List<string> DnsAuthoritative { get; set; } = new();

    /// <summary>
    /// dns-tunnel: the full server leaf certificate (PEM, with BEGIN/END
    /// markers). Load-bearing — slipstream-client verifies the server-presented
    /// leaf against this via <c>--cert</c>. Carried in the profile (self-contained,
    /// supports multi-server / rotation without rebuilding the client) rather than
    /// bundled with the binary. SlipstreamManager writes it to
    /// <see cref="AppPaths.SlipstreamActiveCertPath"/> at launch. A leaf cert is
    /// public material (the server presents it in every TLS handshake), so storing
    /// it in the profile / on disk is not a secret leak. Empty for other protocols.
    /// </summary>
    [YamlMember(Alias = "dns_leaf_cert")]
    public string DnsLeafCertPem { get; set; } = string.Empty;

    /// <summary>
    /// dns-tunnel: OPTIONAL sha256 of the leaf cert (hex), for display + an
    /// integrity cross-check against <see cref="DnsLeafCertPem"/>. Not a pin
    /// (slipstream-client has no <c>--pin</c>); when present, SlipstreamManager
    /// verifies <c>sha256(PEM) == fingerprint</c> and refuses on mismatch. Empty
    /// for other protocols / when not supplied.
    /// </summary>
    [YamlMember(Alias = "dns_leaf_fingerprint")]
    public string DnsLeafFingerprint { get; set; } = string.Empty;
}

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
