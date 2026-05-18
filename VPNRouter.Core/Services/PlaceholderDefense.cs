// PlaceholderDefense — consolidated single-source-of-truth for the 6-layer
// placeholder-credential defense (v3.0 Phase 3D, 2026-05-18).
//
// History:
// ────────
// v2.32.3 (2026-05-17): Z:\kanareik incident — Reality public_key placeholder
// "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU" (originally an Android smoke-
// test constant in `PlaceholderVlessUri`, removed in DEFCT-005) leaked into a
// real user's subscription cache, dial-outs failed silently. Five-layer
// defense added:
//   F-A: VlessServersResolver scope guard         — refuses placeholders when
//                                                   deciding which server pool
//                                                   wins (subscription vs.
//                                                   manual / vless.servers).
//   F-B: SettingsMigrator strip                   — one-shot wipe of placeholder
//                                                   entries from persisted YAML
//                                                   (settings.Vless.* trio,
//                                                   settings.Vless.Servers list,
//                                                   each subscription's
//                                                   Servers list).
//   F-C: UI badge (App layer)                     — Settings page surfaces a
//                                                   one-time notice when F-B
//                                                   pruned anything; lives in
//                                                   App, reads our Inspect().
//   F-D: LeakProtection scope-aware validation    — refuses to allow legacy
//                                                   vless.servers entries that
//                                                   match placeholder
//                                                   fingerprints when union-
//                                                   merging with subscription.
//   F-E: ConfigSanityCheck runtime safety net     — final pre-launch gate on
//                                                   the generated sing-box
//                                                   JSON; refuses to start a
//                                                   tunnel pointed at a known
//                                                   placeholder credential.
// Phase 2G (Wave 7c-1, 2026-05-18): added a 6th layer:
//   Layer-6: VlessDeepVerifier fail-fast          — before spawning a probe
//                                                   sing-box, reject entries
//                                                   whose fingerprint matches
//                                                   any placeholder so the
//                                                   verifier doesn't lie
//                                                   ("VERIFIED — host TCP
//                                                   alive, but Reality
//                                                   handshake would never
//                                                   complete").
//
// Phase 3D consolidation (this file, v3.0):
// ─────────────────────────────────────────
// Pre-3D the layer logic was scattered across:
//   VPNRouter.Core/Services/PlaceholderGuard.cs        ← shared Inspect API
//   VPNRouter.Core/Services/ConfigSanityCheck.cs       ← fingerprint sets +
//                                                       F-E sing-box JSON
//                                                       inspection
//   VPNRouter.Core/Services/VlessServersResolver.cs    ← F-A scope guard,
//                                                       IsPlaceholderEntry
//   VPNRouter.Core/Services/AutoFailoverEngine.cs      ← failover candidate
//                                                       filter (per-entry
//                                                       placeholder reject)
//   VPNRouter.Core/Services/LeakProtection.cs          ← F-D union filter via
//                                                       VlessServersResolver.IsPlaceholderEntry
//   VPNRouter.Core/Services/SettingsMigrator.cs        ← F-B strip via
//                                                       PlaceholderGuard.Inspect
//   VPNRouter.Core/Services/VlessUriParser.cs          ← input-gate
//                                                       PlaceholderGuard.Inspect
//   VPNRouter.Core/Services/ServerUriParser.cs         ← input-gate
//                                                       PlaceholderGuard.Inspect
//   VPNRouter.Core/Services/SubscriptionFetcher.cs     ← drop-on-import
//                                                       PlaceholderGuard.IsPlaceholder
//   VPNRouter.Core/Services/CustomConfigInjector.cs    ← FindFirstProxyOutbound
//                                                       + InspectOutbound
//   VPNRouter.Core/Services/VlessDeepVerifier.cs       ← Layer-6
//                                                       PlaceholderGuard.Inspect
//   VPNRouter.Android/AndroidStorage.cs                ← Android-side strip,
//                                                       PlaceholderGuard.IsPlaceholder
//   VPNRouter.Android/AndroidApp.QrScanApply.cs        ← input-gate (catches
//                                                       PlaceholderConfigException)
//
// The fingerprint set itself was duplicated across `ConfigSanityCheck` (the
// hash-sets) and `PlaceholderGuard` (forwarding to those hash-sets), with
// `VlessServersResolver` AND `AutoFailoverEngine` still reaching back into
// `ConfigSanityCheck.KnownPlaceholder*` directly. That drift surface is what
// caused the v2.32.3 ship: the Core list updated, the Android list didn't.
//
// Phase 3D collapses all of the above into this file:
//
//   - The single fingerprint table lives in `KnownFingerprints` — an
//     `IReadOnlyList<PlaceholderFingerprint>` so adding/auditing entries
//     touches exactly one place. The previous `IReadOnlySet<string>` triples
//     are exposed as derived `KnownPubkeys` / `KnownShortIds` /
//     `KnownServers` properties for back-compat with callers that haven't
//     migrated yet.
//   - Each of the six layers has its own `sealed internal static` sub-class
//     (`LayerA_ResolverScopeGuard`, `LayerB_MigratorStrip`,
//     `LayerD_LeakValidation`, `LayerE_RuntimeSanity`, `Layer6_DeepVerify`,
//     plus the input-gate Inspect surface used by parsers and QR scan).
//     The original call sites (VlessServersResolver, SettingsMigrator,
//     LeakProtection, ConfigSanityCheck, VlessDeepVerifier) remain in place
//     — they now forward to the centralized sub-class. The layer logic is
//     identical, just relocated.
//   - `PlaceholderGuard` becomes a thin one-line forwarder for every public
//     method/property it currently exposes. Existing call sites compile
//     unchanged.
//
// Drift verification (CI follow-up in Phase 4): grep for the placeholder
// pubkey string "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU" — after
// consolidation this file is the only PRODUCTION source containing it.
// Test files keep their hard-coded constants (deliberately — they're pinning
// against accidental fingerprint changes).
//
// Conservative wipe policy carries over from v2.32.3: false-positive bans
// kill VPN for the user. Add new fingerprints to `KnownFingerprints` ONLY
// after confirming via a concrete user-report or stas-style evidence file
// that the fingerprint is bait.

#nullable enable

using Newtonsoft.Json.Linq;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Reality / VLESS / Hysteria2 / TUIC / Shadowsocks placeholder fingerprint
/// record. A placeholder is identified by ONE of its fields (pubkey OR
/// short_id OR server IP) — partial matches still bait the gate. The other
/// fields on the record are <c>null</c> for unknown / not-bait values.
/// </summary>
/// <remarks>
/// We deliberately model fingerprints per-record (not as three independent
/// sets) so future entries can carry per-fingerprint provenance via the
/// <see cref="Origin"/> field. The legacy <c>IReadOnlySet&lt;string&gt;</c>
/// triples on <see cref="PlaceholderDefense"/> are derived projections of
/// this list.
/// </remarks>
public sealed record PlaceholderFingerprint
{
    /// <summary>Reality public key string (base64url, exact match). Null when this fingerprint matches by short_id or server only.</summary>
    public string? Pubkey { get; init; }

    /// <summary>Reality short_id (hex, exact match). Null when this fingerprint matches by pubkey or server only.</summary>
    public string? ShortId { get; init; }

    /// <summary>Server hostname / IP (exact match). Null when this fingerprint matches by pubkey or short_id only.</summary>
    public string? Server { get; init; }

    /// <summary>Human-readable provenance — where the bait fingerprint came from. Used in `Add new entries here only after...` reviews; never shown to users.</summary>
    public string Origin { get; init; } = string.Empty;
}

/// <summary>
/// v3.0 Phase 3D (2026-05-18) — consolidated single-source-of-truth for the
/// 6-layer placeholder-credential defense (formerly F-A..F-E +
/// Wave 7c-1's deep-verify fail-fast). See file-level comment for history
/// and migration rationale.
///
/// <para>Public surface — the <see cref="Inspect(VlessServerEntry?)"/> /
/// <see cref="Inspect(string?, string?, string?)"/> / <see cref="InspectUri"/>
/// triple is what every input gate (parser / subscription / custom-config /
/// QR scan) and persistence migrator should call. The boolean
/// <see cref="IsPlaceholder(VlessServerEntry?)"/> / <see cref="IsPlaceholder(string?, string?, string?)"/>
/// convenience overloads exist for hot paths that don't need the field-name
/// detail. Layer-specific helpers live in the <c>LayerX_*</c> internal
/// sub-classes; the original call sites
/// (<see cref="VlessServersResolver"/>, <see cref="SettingsMigrator"/>, etc.)
/// stay put but now forward to the relevant sub-class.</para>
///
/// <para>Back-compat — <see cref="PlaceholderGuard"/> is preserved as a
/// one-line-per-member forwarder so the ~13 existing call sites compile
/// unchanged. The original <see cref="ConfigSanityCheck.KnownPlaceholderPubkeys"/>
/// / <see cref="ConfigSanityCheck.KnownPlaceholderShortIds"/> /
/// <see cref="ConfigSanityCheck.KnownPlaceholderServers"/> static sets are
/// also kept (now sourced from this file's <see cref="KnownFingerprints"/>
/// projection) so direct hash-set callers (AutoFailoverEngine,
/// VlessServersResolver pre-consolidation) keep working.</para>
/// </summary>
public static class PlaceholderDefense
{
    // ─── Known placeholder fingerprint table ─────────────────────────────────
    //
    // Single source of truth. Every layer reads from here (directly or via
    // a derived projection). Add new entries only after confirming via
    // user-report or stas-style evidence that the fingerprint is bait —
    // false-positive bans are catastrophic.

    private static readonly IReadOnlyList<PlaceholderFingerprint> s_known =
        new List<PlaceholderFingerprint>
        {
            // Z:\kanareik / stas-evidence (2026-05-11..17):
            // PlaceholderVlessUri smoke-test constant from pre-r10 Android
            // builds (removed in DEFCT-005). Leaked into real user configs
            // via subscription cache + legacy direct-VLESS hand-off. See
            //   plans/stas-evidence-config.yaml   (active_server: khunrath_ln)
            //   plans/stas-evidence-current.json  (outbound: 195.135.255.216
            //                                      + pubkey DnT9... + sid 78ca7952)
            new PlaceholderFingerprint
            {
                Pubkey = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU",
                Origin = "PlaceholderVlessUri smoke-test (Android pre-r10 / DEFCT-005)",
            },
            new PlaceholderFingerprint
            {
                ShortId = "78ca7952",
                Origin = "PlaceholderVlessUri smoke-test (Android pre-r10 / DEFCT-005)",
            },
            new PlaceholderFingerprint
            {
                Server = "195.135.255.216",
                Origin = "Stas-evidence khunrath_ln endpoint",
            },
        };

    /// <summary>
    /// The single, authoritative list of placeholder fingerprints. Every
    /// layer derives its match logic from this list — there are no
    /// hard-coded copies of pubkey / short_id / server strings elsewhere
    /// in the codebase. (CI grep gate in Phase 4 will enforce this.)
    /// </summary>
    public static IReadOnlyList<PlaceholderFingerprint> KnownFingerprints => s_known;

    // ─── Derived projections (back-compat for existing callers) ───────────────

    private static readonly IReadOnlySet<string> s_knownPubkeys =
        s_known
            .Select(f => f.Pubkey)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

    private static readonly IReadOnlySet<string> s_knownShortIds =
        s_known
            .Select(f => f.ShortId)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

    private static readonly IReadOnlySet<string> s_knownServers =
        s_known
            .Select(f => f.Server)
            .Where(s => !string.IsNullOrEmpty(s))
            // Servers stay case-sensitive (IP literal) — IPs don't mix case
            // legitimately. Keeping ordinal here matches the v2.32.3
            // ConfigSanityCheck.KnownPlaceholderServers behaviour.
            .ToHashSet(StringComparer.Ordinal)!;

    /// <summary>Reality public-key fingerprints (back-compat derived view of <see cref="KnownFingerprints"/>).</summary>
    public static IReadOnlySet<string> KnownPubkeys => s_knownPubkeys;

    /// <summary>Reality short_id fingerprints (back-compat derived view of <see cref="KnownFingerprints"/>).</summary>
    public static IReadOnlySet<string> KnownShortIds => s_knownShortIds;

    /// <summary>Server-IP fingerprints (back-compat derived view of <see cref="KnownFingerprints"/>).</summary>
    public static IReadOnlySet<string> KnownServers => s_knownServers;

    // ─── Inspect API — single shared entry point for every layer ─────────────

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
        if (!string.IsNullOrEmpty(pubkey) && s_knownPubkeys.Contains(pubkey))
            return "reality.public_key";

        var shortId = entry.Reality?.ShortId;
        if (!string.IsNullOrEmpty(shortId) && s_knownShortIds.Contains(shortId))
            return "reality.short_id";

        var server = entry.Server;
        if (!string.IsNullOrEmpty(server) && s_knownServers.Contains(server))
            return "server";

        return null;
    }

    /// <summary>
    /// Tri-field overload for code paths that don't have a full
    /// <see cref="VlessServerEntry"/> in hand (e.g. raw sing-box JSON
    /// outbound parse, custom-config injector). Passes any one of the
    /// three as <c>null</c> when not applicable.
    /// </summary>
    public static string? Inspect(string? realityPubkey, string? realityShortId, string? server)
    {
        if (!string.IsNullOrEmpty(realityPubkey) && s_knownPubkeys.Contains(realityPubkey))
            return "reality.public_key";
        if (!string.IsNullOrEmpty(realityShortId) && s_knownShortIds.Contains(realityShortId))
            return "reality.short_id";
        if (!string.IsNullOrEmpty(server) && s_knownServers.Contains(server))
            return "server";
        return null;
    }

    /// <summary>
    /// Boolean convenience for hot-paths that don't need the field-name
    /// detail. Equivalent to <c>Inspect(...) != null</c>.
    /// </summary>
    public static bool IsPlaceholder(string? realityPubkey, string? realityShortId, string? server) =>
        Inspect(realityPubkey, realityShortId, server) != null;

    /// <summary>Entry-typed convenience overload.</summary>
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

    // ─── Layer A: VlessServersResolver scope guard ───────────────────────────

    /// <summary>
    /// F-A — scope-guard helpers used by <see cref="VlessServersResolver"/>
    /// to decide whether the user's <c>vless.active_server</c> is a
    /// legitimate manual pick (real server / pubkey / short_id) or a stas-
    /// class placeholder remnant that should be ignored in favour of the
    /// subscription pool. See the resolver class doc for the brat vs stas
    /// differentiation rationale.
    /// </summary>
    internal static class LayerA_ResolverScopeGuard
    {
        /// <summary>
        /// Does this entry look like a stas-class placeholder? Checks
        /// against the consolidated <see cref="KnownFingerprints"/> list
        /// (server IP, Reality public_key, Reality short_id). Returns
        /// false for null entries — callers treat "no entry" as "no
        /// placeholder", not "fail-closed".
        /// </summary>
        public static bool IsPlaceholderEntry(VlessServerEntry? entry)
        {
            if (entry is null) return false;
            return PlaceholderDefense.Inspect(entry) is not null;
        }
    }

    // ─── Layer B: SettingsMigrator strip ─────────────────────────────────────

    /// <summary>
    /// F-B — helpers used by <see cref="SettingsMigrator.PruneKnownPlaceholders"/>
    /// to truncate placeholder values for log output. The migration step
    /// itself (which mutates <see cref="AppSettings"/>) lives in
    /// SettingsMigrator because it needs intimate access to the settings
    /// schema; only the truncation helper is shared here so the field-
    /// matching → value-lookup convention stays consistent.
    /// </summary>
    internal static class LayerB_MigratorStrip
    {
        /// <summary>Truncate a placeholder value for log output (full value
        /// is reconstructable via <see cref="KnownFingerprints"/>; here we only
        /// need enough to disambiguate which fingerprint matched).</summary>
        public static string TruncateForLog(string? v)
        {
            if (string.IsNullOrEmpty(v)) return "(empty)";
            return v.Length <= 16 ? v : $"{v[..8]}…{v[^4..]}";
        }
    }

    // ─── Layer D: LeakProtection scope-aware validation ──────────────────────

    /// <summary>
    /// F-D — predicate used by <see cref="LeakProtection"/>'s scope-aware
    /// allow-list builder. When a generated-mode config has both a
    /// subscription and a legacy <c>vless.servers</c> list, LeakProtection
    /// unions both, but drops entries whose fingerprint matches a
    /// placeholder so the stas-class shadow-override leak class stays
    /// caught at validation time.
    /// </summary>
    internal static class LayerD_LeakValidation
    {
        /// <summary>
        /// Mirror of <see cref="LayerA_ResolverScopeGuard.IsPlaceholderEntry"/>
        /// kept on its own sub-class so a future divergence between the
        /// two layer policies (different escalation behaviour, looser /
        /// stricter match) is structural rather than a hidden alias.
        /// Currently identical to LayerA.
        /// </summary>
        public static bool IsPlaceholderEntry(VlessServerEntry? entry) =>
            LayerA_ResolverScopeGuard.IsPlaceholderEntry(entry);
    }

    // ─── Layer E: ConfigSanityCheck runtime safety net ───────────────────────

    /// <summary>
    /// F-E — sing-box JSON outbound inspector used by
    /// <see cref="ConfigSanityCheck"/> as the final pre-launch gate. Looks
    /// at the FIRST proxy-typed outbound in the generated JSON and refuses
    /// to start a tunnel pointed at a known placeholder credential.
    ///
    /// <para>Also reused by <see cref="CustomConfigInjector"/> so the
    /// custom-config path rejects placeholder JSON at Inject time using
    /// the same detection logic as the runtime safety net (single source
    /// of truth for "look at a sing-box outbound JObject and check it").</para>
    /// </summary>
    internal static class LayerE_RuntimeSanity
    {
        /// <summary>
        /// Locates the first proxy-typed outbound in a sing-box
        /// <c>outbounds</c> array (vless / hysteria2 / tuic / shadowsocks
        /// / trojan). Returns <c>null</c> when none is present. Shared
        /// between <see cref="ConfigSanityCheck.CheckBeforeStart(JObject)"/>
        /// and <see cref="CustomConfigInjector"/>'s placeholder gate so
        /// both layers pick the same outbound to inspect.
        /// </summary>
        public static JObject? FindFirstProxyOutbound(JArray outbounds)
        {
            foreach (var ob in outbounds.OfType<JObject>())
            {
                var type = ob["type"]?.Value<string>()?.ToLowerInvariant() ?? "";
                if (type is "vless" or "hysteria2" or "tuic" or "shadowsocks" or "trojan")
                    return ob;
            }
            return null;
        }

        /// <summary>
        /// Inspects a single sing-box proxy outbound JObject for placeholder
        /// fingerprints (Reality public_key, Reality short_id, server IP).
        /// Returns the matching field name (<c>"reality.public_key"</c>,
        /// <c>"reality.short_id"</c>, <c>"server"</c>) or <c>null</c> when
        /// the outbound is clean. Matches the field-name convention used by
        /// <see cref="PlaceholderDefense.Inspect(string?, string?, string?)"/>.
        /// </summary>
        public static string? InspectOutbound(JObject? proxy)
        {
            if (proxy == null) return null;

            var reality = proxy["tls"]?["reality"] as JObject;
            var pubkey = reality?["public_key"]?.Value<string>();
            var shortId = reality?["short_id"]?.Value<string>();
            var server = proxy["server"]?.Value<string>();

            return PlaceholderDefense.Inspect(pubkey, shortId, server);
        }
    }

    // ─── Layer 6: VlessDeepVerifier fail-fast ────────────────────────────────

    /// <summary>
    /// Phase 2G Wave 7c-1 (2026-05-18) — 6th defense layer added on top of
    /// the F-A..F-E v2.32.3 set. Before spawning a probe sing-box,
    /// <see cref="VlessDeepVerifier.VerifyAsync"/> refuses to verify
    /// entries whose fingerprint matches any placeholder. Without this
    /// layer the verifier could lie ("VERIFIED — host TCP alive") when
    /// the host happens to be reachable on TCP/443 but the Reality
    /// handshake never completes.
    /// </summary>
    internal static class Layer6_DeepVerify
    {
        /// <summary>
        /// Returns the placeholder field name if <paramref name="entry"/>
        /// matches a known fingerprint, otherwise null. Mirror of the
        /// public <see cref="PlaceholderDefense.Inspect(VlessServerEntry?)"/>
        /// — kept as a layer-specific helper so a future deep-verify-
        /// specific policy (e.g. log differently, fail differently) can
        /// diverge without touching every other layer.
        /// </summary>
        public static string? InspectForDeepVerify(VlessServerEntry? entry) =>
            PlaceholderDefense.Inspect(entry);
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
    /// <see cref="PlaceholderDefense.Inspect(VlessServerEntry?)"/>.</summary>
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
