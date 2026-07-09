#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VPNRouter.Core.Services;

/// <summary>
/// urltest R4 — the blocked-target canary list. Built-in defaults (high-signal,
/// widely RU-blocked/degraded targets with lightweight endpoints) plus an
/// optional user-supplied override file <c>cache/canary_targets.json</c>
/// (updateable without an app release — the audit's requirement).
///
/// <para>Safety model: these targets are ONLY probed through the spawned
/// sing-box SOCKS of a deep verify — via-VPN by construction, so the probe
/// never reveals user intent to the local ISP. No direct-from-client probing
/// exists. Logs go through <see cref="CanaryPolicy.RedactUrl"/> (scheme+host
/// only). Staleness: each target carries LastReviewed; past
/// <see cref="ReviewTtl"/> it degrades to ambiguous instead of lying
/// (<see cref="CanaryPolicy.IsStale"/>).</para>
/// </summary>
public static class CanaryTargets
{
    /// <summary>Blocked-target entries older than this are stale (block state moves fast).</summary>
    public static readonly TimeSpan ReviewTtl = TimeSpan.FromDays(45);

    /// <summary>Review stamp for the built-in list — update when re-verifying the targets.</summary>
    public static readonly DateTimeOffset BuiltInReviewedAt = new(2026, 7, 9, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Built-in high-signal canaries. Lightweight endpoints only (a 204 or a tiny
    /// JSON) — a canary pass must not cost real bandwidth through the user's server.
    /// </summary>
    public static IReadOnlyList<CanaryTarget> BuiltIn => new[]
    {
        new CanaryTarget(
            Url: "https://www.youtube.com/generate_204",
            Tier: CanaryTier.PopularBlocked,
            Category: "video",
            LastReviewed: BuiltInReviewedAt,
            Source: "built-in",
            RiskNotes: "RU availability varies by ISP/region — useful, not absolute"),
        new CanaryTarget(
            Url: "https://discord.com/api/v10/gateway",
            Tier: CanaryTier.PopularBlocked,
            Category: "messaging",
            LastReviewed: BuiltInReviewedAt,
            Source: "built-in",
            RiskNotes: "tiny JSON bootstrap endpoint"),
    };

    /// <summary>User-override file (optional): a JSON array of CanaryTarget-shaped objects.</summary>
    private static string OverridePath => Path.Combine(AppPaths.CacheDir, "canary_targets.json");

    /// <summary>
    /// Effective list: the user file when present and parseable (their curation
    /// replaces the defaults entirely — predictable), else the built-ins.
    /// Never throws; a corrupt file falls back to built-ins.
    /// </summary>
    public static IReadOnlyList<CanaryTarget> Load()
    {
        try
        {
            var path = OverridePath;
            if (File.Exists(path))
            {
                var dto = JsonSerializer.Deserialize(
                    File.ReadAllText(path), Json.AppJsonContext.Default.ListCanaryTarget);
                if (dto is { Count: > 0 }) return dto;
            }
        }
        catch { /* corrupt override -> built-ins */ }
        return BuiltIn;
    }
}
