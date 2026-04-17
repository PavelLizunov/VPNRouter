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
            Name = "EtoNeYaProject",
            Url  = "https://raw.githubusercontent.com/EtoNeYaProject/etoneyaproject.github.io/refs/heads/main/1",
            ExpectedCount = 2200,
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
    };
}
