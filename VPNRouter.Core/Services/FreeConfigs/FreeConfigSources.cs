using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// Built-in list of public sources for VLESS configs.
/// Verified 2026-04-17: all six URLs return raw text (plain vless:// URIs, one per line).
/// </summary>
public static class FreeConfigSources
{
    /// <summary>
    /// v2.14.4: merge built-in sources with user-provided ones (enabled only).
    /// User sources are appended last (lower priority).
    /// </summary>
    public static List<FreeConfigSource> GetAll(AppSettings? settings = null)
    {
        var result = new List<FreeConfigSource>(Default);
        if (settings?.App.UserFreeSources == null) return result;

        foreach (var u in settings.App.UserFreeSources.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)))
        {
            result.Add(new FreeConfigSource
            {
                Name = string.IsNullOrWhiteSpace(u.Name) ? $"👤 {TrimHost(u.Url)}" : $"👤 {u.Name}",
                Url = u.Url,
                Enabled = true,
                ExpectedCount = 0, // unknown for user sources
            });
        }
        return result;
    }

    private static string TrimHost(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host;
        }
        catch { return "user source"; }
    }

    public static IReadOnlyList<FreeConfigSource> Default { get; } = new List<FreeConfigSource>
    {
        new()
        {
            Name = "zieng2/wl",
            Url  = "https://raw.githubusercontent.com/zieng2/wl/main/vless_lite.txt",
            ExpectedCount = 300,
        },
        new()
        {
            Name = "EtoNeYaProject (mirror)",
            Url  = "https://alley.serv00.net/1",
            ExpectedCount = 4900,
        },
        new()
        {
            Name = "0xRadikal/verified",
            Url  = "https://raw.githubusercontent.com/0xRadikal/Free-v2ray-Configs/main/verified/configs.txt",
            ExpectedCount = 1300,
        },
        new()
        {
            Name = "mehrtat/vless",
            Url  = "https://raw.githubusercontent.com/mehrtat/vless-collector/main/vless.txt",
            ExpectedCount = 460,
        },
        new()
        {
            Name = "Delta-Kronecker/reality",
            Url  = "https://raw.githubusercontent.com/Delta-Kronecker/V2ray-Config/main/config/reality/reality.txt",
            ExpectedCount = 500,
        },
        new()
        {
            Name = "Au1rxx/hysteria2",
            Url  = "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/protocol/hysteria2/v2ray-base64-0001.txt",
            ExpectedCount = 600,
        },
        new()
        {
            Name = "igareck/vpn-configs-for-russia",
            Url  = "https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/refs/heads/main/Vless-Reality-White-Lists-Rus-Mobile.txt",
            ExpectedCount = 150,
        },
        new()
        {
            Name = "CidVpn",
            Url  = "https://raw.githubusercontent.com/CidVpn/cid-vpn-config/refs/heads/main/general.txt",
            ExpectedCount = 140,
        },
        new()
        {
            Name = "nowmeow.pw/whitelist",
            Url  = "https://nowmeow.pw/8ybBd3fdCAQ6Ew5H0d66Y1hMbh63GpKUtEXQClIu/whitelist",
            ExpectedCount = 30,
        },
        new()
        {
            Name = "kort0881/ru-sni",
            Url  = "https://raw.githubusercontent.com/kort0881/vpn-vless-configs-russia/main/data/githubmirror/ru-sni/vless.txt",
            ExpectedCount = 800,
        },
        new()
        {
            Name = "sevcator/5ubscrpt10n (mini)",
            Url  = "https://raw.githubusercontent.com/sevcator/5ubscrpt10n/main/mini/m1n1-5ub-1.txt",
            ExpectedCount = 3000,
        },
        new()
        {
            Name = "ebrasha/free-v2ray-public-list",
            Url  = "https://raw.githubusercontent.com/ebrasha/free-v2ray-public-list/main/vless_configs.txt",
            ExpectedCount = 17000,
        },
        new()
        {
            Name = "barry-far/V2ray-config",
            Url  = "https://raw.githubusercontent.com/barry-far/V2ray-config/main/Splitted-By-Protocol/vless.txt",
            ExpectedCount = 1700,
        },
        new()
        {
            Name = "kort0881/clean",
            Url  = "https://raw.githubusercontent.com/kort0881/vpn-vless-configs-russia/main/data/githubmirror/clean/vless.txt",
            ExpectedCount = 12000,
        },
        new()
        {
            Name = "Epodonios/v2ray-configs",
            Url  = "https://raw.githubusercontent.com/Epodonios/v2ray-configs/main/All_Configs_Sub.txt",
            ExpectedCount = 2000,
        },
        new()
        {
            Name = "MatinGhanbari/v2ray-configs (base64)",
            Url  = "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/filtered/subs/vless.txt",
            ExpectedCount = 260,
        },
        new()
        {
            Name = "V2RayRoot/V2RayConfig",
            Url  = "https://raw.githubusercontent.com/V2RayRoot/V2RayConfig/main/Config/vless.txt",
            ExpectedCount = 150,
        },
    };
}
