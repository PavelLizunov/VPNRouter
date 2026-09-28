#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VPNRouter.Core.Services;

public static class CanaryTargets
{
    public static readonly TimeSpan ReviewTtl = TimeSpan.FromDays(45);

    public static readonly DateTimeOffset BuiltInReviewedAt = new(2026, 7, 9, 0, 0, 0, TimeSpan.Zero);

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

    private static string OverridePath => Path.Combine(AppPaths.CacheDir, "canary_targets.json");

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
        catch { }
        return BuiltIn;
    }
}
