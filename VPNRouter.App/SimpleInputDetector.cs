using System;
using VPNRouter.Core.Services;

namespace VPNRouter.App;

public enum SmpInputKind
{
    Invalid,

    ServerUri,

    SubscriptionUrl,
}

public static class SimpleInputDetector
{
    public static SmpInputKind Classify(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return SmpInputKind.Invalid;
        var trimmed = input.Trim();

        if (NativeTransportUriParser.HasScheme(trimmed) ||
            trimmed.StartsWith("vless://",       StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("vmess://",       StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("hysteria2://",   StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("hy2://",         StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("tuic://",        StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ss://",          StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("naive://",       StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("naive+https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("naive+quic://",  StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("dns-tunnel://",  StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("dnstt://",       StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("turnrelay://",   StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("awg://",         StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("amneziawg://",   StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("awg3://",        StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("amneziawg3://",  StringComparison.OrdinalIgnoreCase))
            return SmpInputKind.ServerUri;

        if (trimmed.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return SmpInputKind.SubscriptionUrl;

        return SmpInputKind.Invalid;
    }
}
