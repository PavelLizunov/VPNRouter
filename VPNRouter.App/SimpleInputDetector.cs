using System;

namespace VPNRouter.App;

/// <summary>What the user pasted into the Simple-mode input field.</summary>
public enum SmpInputKind
{
    /// <summary>Empty, whitespace, or unknown prefix.</summary>
    Invalid,

    /// <summary>vless://uuid@server:port?... — single-server VLESS URI.</summary>
    Vless,

    /// <summary>http(s)://... — subscription URL returning base64 or newline-delimited VLESS URIs.</summary>
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

        if (trimmed.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            return SmpInputKind.Vless;

        if (trimmed.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return SmpInputKind.SubscriptionUrl;

        return SmpInputKind.Invalid;
    }
}
