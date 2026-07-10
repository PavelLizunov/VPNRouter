#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace VPNRouter.Core.Services;

/// <summary>
/// Tier of a blocked-target canary. Control canaries prove the tunnel carries normal
/// internet at all; blocked-target canaries prove censorship bypass for user-visible
/// targets. Popular targets (YouTube/Discord/Telegram) are strong but can get special
/// treatment; less-popular targets round out the picture.
/// </summary>
public enum CanaryTier
{
    /// <summary>Proves normal proxied internet works (e.g. gstatic / cloudflare trace).</summary>
    Control,
    /// <summary>High-signal, widely-blocked/degraded target (YouTube, Discord, Telegram, …).</summary>
    PopularBlocked,
    /// <summary>Less-popular blocked target, from an updateable / user-supplied list.</summary>
    LessPopularBlocked,
}

/// <summary>
/// One canary target. Pure data — the actual probing (network) is a deferred, opt-in,
/// via-VPN-by-default step (<c>plans/urltest-verification-deferred-risky-2026-07-09.md</c> R4).
/// </summary>
public sealed record CanaryTarget(
    string Url,
    CanaryTier Tier,
    string Category,
    DateTimeOffset LastReviewed,
    string? Source = null,
    string? RiskNotes = null);

/// <summary>Aggregate canary conclusion, reduced to the <see cref="ServerHealthPhases.BlockedTargetCanary"/> outcome.</summary>
public sealed record CanaryAggregate(PhaseOutcome BlockedTargetCanary, bool StaleOrAmbiguous, string Reason);

/// <summary>
/// Pure, network-free rules for the blocked-target canary layer: URL redaction for logs,
/// staleness (TTL) classification, and reducing a set of per-target outcomes to a single
/// <see cref="PhaseOutcome"/> for <see cref="ServerHealthClassifier"/>. No probing here.
/// </summary>
public static class CanaryPolicy
{
    /// <summary>Default control canaries (timeless, safe to hardcode — no user intent revealed).</summary>
    public static readonly IReadOnlyList<string> DefaultControlUrls = new[]
    {
        "https://www.gstatic.com/generate_204",
        "https://www.cloudflare.com/cdn-cgi/trace",
    };

    /// <summary>
    /// Audit safe default: DIRECT (non-VPN) probes to blocked targets can reveal user intent
    /// to the ISP, so they are OFF unless the user explicitly opts in via an advanced action.
    /// The future prober (deferred R4) must consult this; blocked-target canaries run
    /// via-VPN only by default.
    /// </summary>
    public const bool DirectProbesDefaultEnabled = false;

    /// <summary>
    /// Strip everything but scheme + host for logging, so a canary/probe never records a
    /// full path or query string. Control-canary paths (e.g. <c>/generate_204</c>) and any
    /// user-supplied fragments are dropped. Never throws on a malformed url — returns a
    /// coarse redaction instead.
    /// </summary>
    public static string RedactUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(none)";
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            return $"{u.Scheme}://{u.Host}";
        // Malformed: keep only up to the first '/', '?' or '#'.
        var end = url.IndexOfAny(new[] { '/', '?', '#' });
        return end >= 0 ? url[..end] : url;
    }

    /// <summary>
    /// A blocked-target canary is stale when it hasn't been reviewed within <paramref name="ttl"/>
    /// — block state changes fast, so an old entry is ambiguous. Control canaries are timeless
    /// (fixed endpoints) and are never stale.
    /// </summary>
    public static bool IsStale(CanaryTarget target, DateTimeOffset now, TimeSpan ttl)
    {
        if (target is null) return false;
        if (target.Tier == CanaryTier.Control) return false;
        return now - target.LastReviewed > ttl;
    }

    /// <summary>
    /// Reduce per-blocked-target outcomes to the <see cref="ServerHealthPhases.BlockedTargetCanary"/>
    /// phase, given whether the control canary passed:
    /// <list type="bullet">
    ///   <item>control did not pass → <see cref="PhaseOutcome.Unknown"/> (can't judge bypass without a working tunnel).</item>
    ///   <item>every non-stale blocked target passed → clean <see cref="PhaseOutcome.Pass"/> (bypass proven).</item>
    ///   <item>some non-stale targets passed but another failed → <see cref="PhaseOutcome.Pass"/> +
    ///     StaleOrAmbiguous (partial — e.g. YouTube ok but a less-popular target still fails; the
    ///     audit's rule: partial is NOT a clean global OK).</item>
    ///   <item>at least one non-stale blocked target failed and none passed → <see cref="PhaseOutcome.Fail"/> (control-only).</item>
    ///   <item>only stale/ambiguous results → <see cref="PhaseOutcome.Unknown"/> + StaleOrAmbiguous.</item>
    /// </list>
    /// </summary>
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
