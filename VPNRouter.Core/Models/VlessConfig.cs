using System.Linq;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public class VlessConfig
{
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

    [YamlMember(Alias = "servers")]
    public List<VlessServerEntry> Servers { get; set; } = new();

    [YamlMember(Alias = "active_server")]
    public string ActiveServer { get; set; } = string.Empty;

    public List<VlessServerEntry> GetEffectiveServers()
    {
        if (Servers != null && Servers.Count > 0)
            return Servers;

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

    public bool AutoSelectBestServer { get; set; } = false;

    public List<VlessServerEntry> GetActiveServers()
    {
        var all = GetEffectiveServers();
        if (all.Count == 0) return all;

        VlessServerEntry? active = null;
        if (!string.IsNullOrEmpty(ActiveServer))
            active = all.FirstOrDefault(s =>
                s.Name?.Equals(ActiveServer, StringComparison.OrdinalIgnoreCase) == true);

        active ??= all[0];

        if (!string.IsNullOrEmpty(active.DetourVia))
            return WithRequiredHop(active, all);

        if (active.Dnstt != null || active.IsTurnRelay || active.RequiresSingleOutbound() ||
            (!AutoSelectBestServer && string.Equals(active.Protocol, "vmess", StringComparison.OrdinalIgnoreCase)))
            return new List<VlessServerEntry> { active };
        if (all.Count <= 1) return all;

        if (AutoSelectBestServer)
            return BuildAutoSelectPool(all, active);

        var activeIp = active.Server;
        return all.Where(s => s.Server == activeIp && s.Dnstt == null && !s.IsTurnRelay &&
            string.IsNullOrEmpty(s.DetourVia)).ToList();
    }

    internal static List<VlessServerEntry> WithRequiredHop(VlessServerEntry active, IEnumerable<VlessServerEntry> servers)
    {
        if (string.IsNullOrEmpty(active.DetourVia)) return [active];

        var upstreams = servers.Where(server =>
            string.IsNullOrEmpty(server.DetourVia) &&
            string.Equals(server.OutboundId, active.DetourVia, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return upstreams.Count == 1 ? [active, upstreams[0]] : [active];
    }

    private List<VlessServerEntry> BuildAutoSelectPool(List<VlessServerEntry> all, VlessServerEntry active)
    {
        var proto = active.Protocol ?? "vless";
        var pool = all.Where(s =>
            string.IsNullOrEmpty(s.DetourVia) && !s.IsTurnRelay &&
            string.Equals(s.Protocol ?? "vless", proto, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!string.IsNullOrEmpty(active.Flow))
        {
            var flowOnly = pool.Where(s => !string.IsNullOrEmpty(s.Flow)).ToList();
            if (flowOnly.Count > 0) pool = flowOnly;
        }

        return pool.Count > 0 ? pool : new List<VlessServerEntry> { active };
    }
}
