// PlaceholderGuard — single source of truth for "is this a known-bad
// placeholder credential" checks.
//
// v2.32.3 (2026-05-17): centralized helper introduced after F-E ran
// repeatedly on real user configs (Z:\kanareik-class incident, Bug-AND-023
// session). Pre-v2.32.3 each layer that wanted to reject placeholder
// credentials reached into ConfigSanityCheck's hash-sets directly:
//   - ConfigSanityCheck.CheckBeforeStart    (sing-box JObject scan)
//   - VlessServersResolver F-A guard        (resolver scope guard)
//   - SettingsLoader F-B migration          (legacy vless.servers strip)
//   - LeakProtection F-D check              (scope-aware)
//   - Android MainActivity PlaceholderVlessUri sniff (removed DEFCT-005)
// The lists were duplicated across files and at risk of drifting. v2.32.3
// promotes the three sets to PlaceholderGuard and adds typed Inspect()
// entry-points so callers don't have to know how a "match" is computed.
//
// Goal: every code path that ingests a vless:// (parser, subscription
// fetcher, custom-config injector, Android QR scanner, manual paste) +
// every code path that loads persisted state (settings migrator, Android
// storage) routes through PlaceholderGuard. Once a placeholder is gone
// from those gates, the only way it can show up at runtime is via a fresh
// download from a hostile / broken subscription provider — F-E (the
// runtime sanity check) still catches that.
//
// We deliberately keep the fingerprint lists narrow. False-positive bans
// kill VPN for the user. Add new entries here ONLY after confirming via
// a concrete user-report or stas-style evidence file that the fingerprint
// is bait.

using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Centralized "is this credential placeholder bait" check. All input
/// gates (parser / subscription / custom-config / QR) and migration
/// helpers (settings loader / Android storage prune) MUST route through
/// this class instead of touching <see cref="ConfigSanityCheck"/>'s
/// hash-sets directly.
/// </summary>
public static class PlaceholderGuard
{
    /// <summary>
    /// Reality public-key fingerprints we know to be placeholder bait.
    /// Mirrors <see cref="ConfigSanityCheck.KnownPlaceholderPubkeys"/> —
    /// kept as a separate reference so future additions only touch one
    /// list (consider folding ConfigSanityCheck's sets back into here
    /// in v2.32.4 when the rebase footprint is smaller).
    /// </summary>
    public static IReadOnlySet<string> KnownPubkeys => ConfigSanityCheck.KnownPlaceholderPubkeys;

    /// <summary>Reality short_id fingerprints. See <see cref="KnownPubkeys"/>.</summary>
    public static IReadOnlySet<string> KnownShortIds => ConfigSanityCheck.KnownPlaceholderShortIds;

    /// <summary>Server-IP fingerprints. See <see cref="KnownPubkeys"/>.</summary>
    public static IReadOnlySet<string> KnownServers => ConfigSanityCheck.KnownPlaceholderServers;

    /// <summary>
    /// Returns the first matching field name for a placeholder-tagged
    /// <see cref="VlessServerEntry"/>, or <c>null</c> when the entry is
    /// clean. Field names follow the convention used in
    /// <see cref="ConfigSanityCheck"/> (<c>"reality.public_key"</c>,
    /// <c>"reality.short_id"</c>, <c>"server"</c>) so log output and
    /// recovery notices stay consistent across layers.
    /// </summary>
    public static string? Inspect(VlessServerEntry? entry)
    {
        if (entry is null) return null;

        // Pubkey check first — most reliable fingerprint (stas-class
        // placeholders all share the same Android PlaceholderVlessUri
        // pubkey regardless of server IP).
        var pubkey = entry.Reality?.PublicKey;
        if (!string.IsNullOrEmpty(pubkey) && KnownPubkeys.Contains(pubkey))
            return "reality.public_key";

        var shortId = entry.Reality?.ShortId;
        if (!string.IsNullOrEmpty(shortId) && KnownShortIds.Contains(shortId))
            return "reality.short_id";

        var server = entry.Server;
        if (!string.IsNullOrEmpty(server) && KnownServers.Contains(server))
            return "server";

        return null;
    }

    /// <summary>
    /// Tri-field overload for code paths that don't have a full
    /// <see cref="VlessServerEntry"/> in hand (e.g. raw sing-box JSON
    /// outbound parse, custom-config injector). Passes any one of the
    /// three as <c>null</c> when not applicable. Same return convention
    /// as <see cref="Inspect(VlessServerEntry?)"/>.
    /// </summary>
    public static string? Inspect(string? realityPubkey, string? realityShortId, string? server)
    {
        if (!string.IsNullOrEmpty(realityPubkey) && KnownPubkeys.Contains(realityPubkey))
            return "reality.public_key";
        if (!string.IsNullOrEmpty(realityShortId) && KnownShortIds.Contains(realityShortId))
            return "reality.short_id";
        if (!string.IsNullOrEmpty(server) && KnownServers.Contains(server))
            return "server";
        return null;
    }

    /// <summary>
    /// Boolean convenience for hot-paths that don't need the field-name
    /// detail. Equivalent to <c>Inspect(...) != null</c>.
    /// </summary>
    public static bool IsPlaceholder(string? realityPubkey, string? realityShortId, string? server) =>
        Inspect(realityPubkey, realityShortId, server) != null;

    /// <summary>
    /// Convenience overload for entry-typed callers.
    /// </summary>
    public static bool IsPlaceholder(VlessServerEntry? entry) => Inspect(entry) != null;

    /// <summary>
    /// Inspect a raw share-link (<c>vless://</c>, <c>hy2://</c>, etc.).
    /// Returns the placeholder field name (e.g. <c>"reality.public_key"</c>)
    /// or null when the URI is clean / unparseable.
    /// <para>v2.32.3 (subtle): we deliberately bypass
    /// <see cref="ServerUriParser.TryParse"/> here. TryParse swallows
    /// <see cref="PlaceholderConfigException"/> as part of its "drop bad
    /// entries in a multi-entry loop" contract — but that would make this
    /// helper report "clean" for a placeholder URI, defeating the whole
    /// point. We call <see cref="ServerUriParser.Parse"/> directly and
    /// promote the typed exception into the field-name return.</para>
    /// <para>Parse failures unrelated to placeholders (FormatException for
    /// garbage strings) are squashed to null so callers don't have to
    /// distinguish "couldn't parse" from "no placeholder found".</para>
    /// </summary>
    public static string? InspectUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        try
        {
            var parsed = ServerUriParser.Parse(uri);
            return Inspect(parsed);
        }
        catch (PlaceholderConfigException ex)
        {
            return ex.OffendingField;
        }
        catch
        {
            // FormatException / argument errors — garbage input. Not the
            // guard's concern; let the upstream parser surface that.
            return null;
        }
    }
}

/// <summary>
/// Thrown by input gates (parser / subscription / custom-config / QR)
/// when ingested credentials match a known placeholder fingerprint.
/// Carries enough detail for the UI to render an actionable error card
/// (which field tripped, what hint to show the user). Distinct from
/// <see cref="System.FormatException"/> so callers can choose to dispatch
/// "fix your provider URL" vs "fix your typo" guidance.
/// </summary>
public sealed class PlaceholderConfigException : Exception
{
    /// <summary>Which field tripped the guard — same convention as
    /// <see cref="PlaceholderGuard.Inspect(VlessServerEntry?)"/>.</summary>
    public string OffendingField { get; }

    /// <summary>The placeholder value itself (truncated to first 12 chars
    /// for the user-facing message — full value stays in the log).</summary>
    public string OffendingValue { get; }

    public PlaceholderConfigException(string offendingField, string offendingValue)
        : base($"Credential rejected: {offendingField} matches a known placeholder fingerprint ({TruncateForMessage(offendingValue)}). " +
               "Get a real vless:// URL from your VPN provider.")
    {
        OffendingField = offendingField;
        OffendingValue = offendingValue;
    }

    private static string TruncateForMessage(string s) =>
        string.IsNullOrEmpty(s) ? "(empty)" :
        s.Length <= 16 ? s :
        $"{s[..8]}…{s[^4..]}";
}
