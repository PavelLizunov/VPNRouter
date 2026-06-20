using System;
using VPNRouter.Core.Localization;

namespace VPNRouter.Core.Services;

/// <summary>
/// P2 (2026-06-21) — parsed view of the <c>Subscription-Userinfo</c> response header
/// most VLESS/Clash subscription panels send (XrayR / V2Board / sing-box panels /
/// Hiddify-compatible). Format: <c>upload=&lt;bytes&gt;; download=&lt;bytes&gt;;
/// total=&lt;bytes&gt;; expire=&lt;unix-seconds&gt;</c> (any subset, any order).
/// Shared by desktop + Android so the remaining-traffic / days-left card renders
/// identically. Pure + side-effect-free → unit-tested in VPNRouter.Tests.
/// </summary>
public sealed record SubscriptionUserInfo(long Upload, long Download, long Total, DateTimeOffset? Expire)
{
    public long Used => Upload + Download;

    /// <summary>Remaining quota in bytes, or null when the provider sent no total.</summary>
    public long? RemainingBytes => Total > 0 ? Math.Max(0, Total - Used) : (long?)null;

    /// <summary>Whole days until expiry (floored, never negative), or null when no expiry.</summary>
    public int? DaysLeft(DateTimeOffset now)
        => Expire is { } e ? (int)Math.Max(0, Math.Floor((e - now).TotalDays)) : (int?)null;

    public bool HasAnything => Total > 0 || Used > 0 || Expire is not null;

    /// <summary>
    /// Parse the raw header. Returns null for null/blank/unparseable input (so callers
    /// can null-coalesce). Tolerant: ignores unknown keys, missing keys, and whitespace.
    /// </summary>
    public static SubscriptionUserInfo? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        long up = 0, down = 0, total = 0;
        DateTimeOffset? expire = null;
        var any = false;
        foreach (var part in header.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part.Substring(0, eq).Trim().ToLowerInvariant();
            var val = part.Substring(eq + 1).Trim();
            if (!long.TryParse(val, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var n))
                continue;
            switch (key)
            {
                case "upload": up = n; any = true; break;
                case "download": down = n; any = true; break;
                case "total": total = n; any = true; break;
                case "expire":
                    if (n > 0)
                    {
                        try { expire = DateTimeOffset.FromUnixTimeSeconds(n); any = true; }
                        catch { /* out-of-range epoch — ignore */ }
                    }
                    break;
            }
        }
        return any ? new SubscriptionUserInfo(up, down, total, expire) : null;
    }

    private static string HumanBytes(long bytes)
    {
        if (bytes <= 0) return "0";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return (u >= 2 ? v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                       : v.ToString("0", System.Globalization.CultureInfo.InvariantCulture))
               + " " + units[u];
    }

    /// <summary>
    /// One-line human summary for the subscription card, localized via Strings.Ru.
    /// e.g. "6.1 / 100 GB · ост. 42 дн" (RU) or "6.1 / 100 GB · 42 days left" (EN).
    /// Returns empty when there's nothing to show.
    /// </summary>
    public string FormatSummary(DateTimeOffset now)
    {
        if (!HasAnything) return string.Empty;
        var ru = Strings.Ru;
        var parts = new System.Collections.Generic.List<string>(2);
        if (Total > 0)
            parts.Add($"{HumanBytes(Used)} / {HumanBytes(Total)}");
        else if (Used > 0)
            parts.Add(HumanBytes(Used));
        var dl = DaysLeft(now);
        if (dl is { } d)
            parts.Add(ru ? $"ост. {d} дн" : $"{d} days left");
        return string.Join(" · ", parts);
    }
}
