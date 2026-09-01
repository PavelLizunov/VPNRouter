using System.Linq;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public class VlessConfig
{
    // ── Legacy single-server fields (backward compatible) ──────────────────
    [YamlMember(Alias = "server")]
    public string Server { get; set; } = string.Empty;

    [YamlMember(Alias = "port")]
    public int Port { get; set; } = 443;

    [YamlMember(Alias = "uuid")]
    public string Uuid { get; set; } = string.Empty;

    /// <summary>xtls-rprx-vision for Reality, empty for plain VLESS</summary>
    [YamlMember(Alias = "flow")]
    public string Flow { get; set; } = string.Empty;

    /// <summary>tls | reality</summary>
    [YamlMember(Alias = "security")]
    public string Security { get; set; } = "reality";

    [YamlMember(Alias = "reality")]
    public VlessRealityConfig Reality { get; set; } = new();

    /// <summary>Fallback plain TLS config (used when security = tls)</summary>
    [YamlMember(Alias = "tls")]
    public VlessTlsConfig Tls { get; set; } = new();

    /// <summary>tcp | ws | grpc — tcp is default for Reality+XTLS</summary>
    [YamlMember(Alias = "transport")]
    public VlessTransportConfig Transport { get; set; } = new();

    // ── Multi-server support ───────────────────────────────────────────────
    /// <summary>
    /// List of VLESS servers. When 2+ servers, urltest outbound is used for
    /// automatic failover. When empty, legacy single-server fields are used.
    /// </summary>
    [YamlMember(Alias = "servers")]
    public List<VlessServerEntry> Servers { get; set; } = new();

    /// <summary>
    /// Name of the actively selected server. Only this server (and its
    /// TCP/UDP pair with same IP) is used for routing. Other servers remain
    /// in the list but are NOT included in the generated config.
    /// When empty, the first server is used.
    /// </summary>
    [YamlMember(Alias = "active_server")]
    public string ActiveServer { get; set; } = string.Empty;

    /// <summary>
    /// Builds the effective server list. If 'servers' is populated, returns it.
    /// Otherwise creates a single entry from the legacy fields.
    /// </summary>
    /// <summary>
    /// Returns the full list of servers (for UI display).
    /// Use <see cref="GetActiveServers"/> for the servers to actually route through.
    /// </summary>
    public List<VlessServerEntry> GetEffectiveServers()
    {
        if (Servers != null && Servers.Count > 0)
            return Servers;

        // Backward compat: build from legacy scalar fields
        if (!string.IsNullOrEmpty(Server))
        {
            return new List<VlessServerEntry>
            {
                new()
                {
                    Server = Server,
                    Port = Port,
                    Uuid = Uuid,
                    Flow = Flow,
                    Security = Security ?? "reality",
                    Reality = Reality ?? new VlessRealityConfig(),
                    Tls = Tls ?? new VlessTlsConfig(),
                    Transport = Transport ?? new VlessTransportConfig()
                }
            };
        }

        return new List<VlessServerEntry>();
    }

    /// <summary>
    /// Backlog A (opt-in): when true, <see cref="GetActiveServers"/> returns a
    /// same-protocol pool of the subscription's servers instead of just the selected
    /// one, so the generated sing-box config wraps them in a <c>urltest</c> group and
    /// auto-selects the fastest reachable node. Off by default — preserves the
    /// single-server behaviour. Toggle lives on the Subscribe page. Same-protocol
    /// only (consistent exit; avoids the cross-protocol exit-IP inconsistency).
    /// </summary>
    public bool AutoSelectBestServer { get; set; } = false;

    /// <summary>
    /// Returns ONLY the servers to route through — the active server and its same-IP
    /// TCP/UDP pair, OR — when <see cref="AutoSelectBestServer"/> is on — the active
    /// server's same-protocol pool (urltest auto-select). This is what
    /// ConfigGenerator uses to build sing-box outbounds.
    /// </summary>
    public List<VlessServerEntry> GetActiveServers()
    {
        var all = GetEffectiveServers();
        if (all.Count <= 1) return all;

        // Find active by name
        VlessServerEntry? active = null;
        if (!string.IsNullOrEmpty(ActiveServer))
            active = all.FirstOrDefault(s =>
                s.Name?.Equals(ActiveServer, StringComparison.OrdinalIgnoreCase) == true);

        // Fallback: first server
        active ??= all[0];

        // Opt-in auto-select: route through a same-protocol pool so the generator
        // wraps it in a urltest group (fastest reachable node wins).
        if (AutoSelectBestServer)
            return BuildAutoSelectPool(all, active);

        // Default: only the active server + its same-IP TCP/UDP pair.
        var activeIp = active.Server;
        return all.Where(s => s.Server == activeIp).ToList();
    }

    /// <summary>
    /// Same-protocol bundle for opt-in auto-select. Includes every server sharing the
    /// active server's protocol; for VLESS-vision (flow set) it keeps only flow entries
    /// so the urltest group is a clean set of TCP/vision nodes (UDP rides the vision
    /// flow) — preventing a cross-node TCP/UDP split when the subscription also carries
    /// no-flow siblings. Never empty (falls back to the active server).
    /// </summary>
    private List<VlessServerEntry> BuildAutoSelectPool(List<VlessServerEntry> all, VlessServerEntry active)
    {
        var proto = active.Protocol ?? "vless";
        var pool = all.Where(s =>
            string.Equals(s.Protocol ?? "vless", proto, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!string.IsNullOrEmpty(active.Flow))
        {
            var flowOnly = pool.Where(s => !string.IsNullOrEmpty(s.Flow)).ToList();
            if (flowOnly.Count > 0) pool = flowOnly;
        }

        return pool.Count > 0 ? pool : new List<VlessServerEntry> { active };
    }
}
