using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public class TunSettings
{
    public const int MinimumMtu = 576;
    public const int MinimumIpv6Mtu = 1280;
    public const int DefaultMtu = 1420;
    public const int MaximumMtu = 1500;

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

    [YamlMember(Alias = "mtu")]
    public int Mtu { get; set; } = DefaultMtu;

    [YamlMember(Alias = "auto_route")]
    public bool AutoRoute { get; set; } = true;

    [YamlMember(Alias = "strict_route")]
    public bool StrictRoute { get; set; } = false;

    [YamlMember(Alias = "route_exclude_address")]
    public List<string> RouteExcludeAddress { get; set; } = new();

    [YamlIgnore]
    [JsonIgnore]
    public List<string> AutoDetectedExcludeAddress { get; set; } = new();

    public List<string> GetEffectiveRouteExcludeAddress()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
