#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace VPNRouter.Core.Services;

public enum CanaryTier
{
    Control,
    PopularBlocked,
    LessPopularBlocked,
}

public sealed record CanaryTarget(
    string Url,
    CanaryTier Tier,
    string Category,
    DateTimeOffset LastReviewed,
    string? Source = null,
    string? RiskNotes = null);

public sealed record CanaryAggregate(PhaseOutcome BlockedTargetCanary, bool StaleOrAmbiguous, string Reason);

public static class CanaryPolicy
{
    public const bool DirectProbesDefaultEnabled = false;

    public static string RedactUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(none)";
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            return $"{u.Scheme}://{u.Host}";

        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        var searchStart = schemeEnd >= 0 ? schemeEnd + 3 : 0;
        var relativeEnd = url.IndexOfAny(new[] { '/', '?', '#' }, searchStart);
        var candidate = relativeEnd >= 0 ? url[..relativeEnd] : url;

        var atIndex = candidate.LastIndexOf('@');
        if (atIndex >= 0 && atIndex >= searchStart)
        {
            return schemeEnd >= 0
                ? candidate[..(schemeEnd + 3)] + candidate[(atIndex + 1)..]
                : candidate[(atIndex + 1)..];
        }
        return candidate;
    }

    public static bool IsStale(CanaryTarget target, DateTimeOffset now, TimeSpan ttl)
    {
        if (target is null) return false;
        if (target.Tier == CanaryTier.Control) return false;
        return now - target.LastReviewed > ttl;
    }

    public static CanaryAggregate Evaluate(
        bool controlPassed,
        IEnumerable<(bool Passed, bool Stale)> blockedResults)
    {
        if (!controlPassed)
            return new(PhaseOutcome.Unknown, false, "control canary did not pass — cannot judge bypass");

        var list = (blockedResults ?? Enumerable.Empty<(bool, bool)>()).ToList();
        var fresh = list.Where(r => !r.Stale).ToList();

        if (fresh.Count == 0)
            return new(PhaseOutcome.Unknown, list.Count > 0,
                list.Count > 0 ? "all blocked-target canaries are stale/ambiguous" : "no blocked-target canaries");

        if (fresh.Any(r => r.Passed))
        {
            return fresh.Any(r => !r.Passed)
                ? new(PhaseOutcome.Pass, true,
                    "partial: a blocked-target canary passed but another fresh one failed — not a clean global OK")
                : new(PhaseOutcome.Pass, false, "a blocked-target canary passed — bypass proven");
        }

        return new(PhaseOutcome.Fail, false, "control ok but every fresh blocked-target canary failed");
    }
}
