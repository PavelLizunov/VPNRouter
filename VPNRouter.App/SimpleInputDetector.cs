using System;

namespace VPNRouter.App;

/// <summary>What the user pasted into the Simple-mode input field.</summary>
public enum SmpInputKind
{
    /// <summary>Empty, whitespace, or unknown prefix.</summary>
    Invalid,

    /// <summary>
    /// Single-server share-link URI in any supported scheme:
    /// <c>vless://</c> / <c>hysteria2://</c> / <c>hy2://</c> /
    /// <c>tuic://</c> / <c>ss://</c>.
    /// (Renamed from <c>Vless</c> for v2.30.1-r3 multi-protocol support;
    /// <c>Vless</c> kept as a back-compat alias below.)
    /// </summary>
    ServerUri,

    /// <summary>Back-compat alias for <see cref="ServerUri"/>.</summary>
    [System.Obsolete("Use ServerUri — Simple input now accepts any share-link scheme, not only VLESS.")]
    Vless = ServerUri,

    /// <summary>http(s)://... — subscription URL returning base64 or newline-delimited share-link URIs.</summary>
    SubscriptionUrl,
}

/// <summary>
/// Prefix-based classifier for the Simple-mode input. Cheap and
/// unambiguous — no regex, no network.
/// </summary>
public static class SimpleInputDetector
{
    public static SmpInputKind Classify(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return SmpInputKind.Invalid;
        var trimmed = input.Trim();

        // v2.30.1-r3 / r8: any supported share-link scheme — VLESS, Hysteria2,
        // TUIC, Shadowsocks, NaiveProxy (Windows/Linux runtime). Subscriber/Simple
        // paths both delegate the actual parsing to ServerUriParser.
        if (trimmed.StartsWith("vless://",       StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("hysteria2://",   StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("hy2://",         StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("tuic://",        StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ss://",          StringComparison.OrdinalIgnoreCase) ||
            // r8 #4: NaiveProxy share-links (Win/Linux runtime; platform-gated at
            // apply time so the parser doesn't blame an "invalid link" on macOS).
            trimmed.StartsWith("naive://",       StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("naive+https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("naive+quic://",  StringComparison.OrdinalIgnoreCase))
            return SmpInputKind.ServerUri;

        if (trimmed.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return SmpInputKind.SubscriptionUrl;

        return SmpInputKind.Invalid;
    }
}
