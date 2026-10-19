using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>What a server is good for, from its protocol and transport. The server lists show it as a coloured icon in
/// front of the name and as the use-case chip; desktop and Android use the same groups, icons and colour tokens.</summary>
public enum ProtocolGroup
{
    Daily,
    StealthWeb,
    WebFallback,
    GamesVoice,
    LowPing,
    WebOnly,
    Fallback,
    Emergency,
}

public static class ProtocolGroups
{
    public static ProtocolGroup Of(VlessServerEntry? entry)
    {
        if (entry?.Awg != null) return ProtocolGroup.LowPing;
        if (entry?.IsDnsTunnel == true) return ProtocolGroup.Emergency;
        var protocol = (entry?.Protocol ?? "vless").ToLowerInvariant();
        var transport = entry?.Transport?.Type?.ToLowerInvariant();
        return protocol switch
        {
            "hysteria2" or "hy2" or "tuic" => ProtocolGroup.GamesVoice,
            "naive" => ProtocolGroup.WebOnly,
            "amneziawg" or "awg" => ProtocolGroup.LowPing,
            "dns-tunnel" => ProtocolGroup.Emergency,
            "shadowsocks" or "ss" => ProtocolGroup.Fallback,
            _ when transport is "xhttp" => ProtocolGroup.StealthWeb,
            _ when transport is "ws" or "grpc" => ProtocolGroup.WebFallback,
            _ => ProtocolGroup.Daily,
        };
    }

    /// <summary>The Lucide icon name (<see cref="UiIcons"/>) of the group.</summary>
    public static string Icon(ProtocolGroup group) => group switch
    {
        ProtocolGroup.StealthWeb => UiIcons.Ghost,
        ProtocolGroup.WebFallback => UiIcons.Globe,
        ProtocolGroup.GamesVoice => UiIcons.Zap,
        ProtocolGroup.LowPing => UiIcons.Activity,
        ProtocolGroup.WebOnly => UiIcons.Compass,
        ProtocolGroup.Fallback => UiIcons.LifeBuoy,
        ProtocolGroup.Emergency => UiIcons.Siren,
        _ => UiIcons.VenetianMask,
    };

    /// <summary>The prefix of the group's colour tokens in Tokens.axaml: "{prefix}FgBrush" for the icon and
    /// "{prefix}BgBrush" for the disc behind it.</summary>
    public static string TokenPrefix(ProtocolGroup group) => "Proto" + group;
}
