namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// Built-in list of public sources for VLESS configs.
/// Verified 2026-04-17: all six URLs return raw text (plain vless:// URIs, one per line).
/// </summary>
public static class FreeConfigSources
{
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
            Name = "EtoNeYaProject (github)",
            Url  = "https://raw.githubusercontent.com/EtoNeYaProject/etoneyaproject.github.io/refs/heads/main/1",
            ExpectedCount = 2200,
        },
        new()
        {
            Name = "EtoNeYaProject (a9fm mirror)",
            Url  = "https://etoneya.a9fm.site/1",
            ExpectedCount = 1100,
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
            Name = "ByeWhiteLists2",
            Url  = "https://raw.githubusercontent.com/ByeWhiteLists/ByeWhiteLists2/refs/heads/main/ByeWhiteLists2.txt",
            ExpectedCount = 1000,
        },
        // ── Added v2.13.10 ──
        new()
        {
            Name = "sevcator/5ubscrpt10n",
            Url  = "https://raw.githubusercontent.com/sevcator/5ubscrpt10n/main/protocols/vl.txt",
            ExpectedCount = 22000,
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
            Name = "kort0881/vpn-vless-configs-russia",
            Url  = "https://raw.githubusercontent.com/kort0881/vpn-vless-configs-russia/main/githubmirror/clean/vless.txt",
            ExpectedCount = 3500,
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
