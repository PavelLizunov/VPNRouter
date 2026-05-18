// PlaceholderGuard — back-compat thin forwarder for PlaceholderDefense.
//
// History:
//   v2.32.3 (2026-05-17): centralized helper introduced after F-E ran
//   repeatedly on real user configs (Z:\kanareik-class incident,
//   Bug-AND-023 session). Pre-v2.32.3 each layer reached into
//   ConfigSanityCheck's hash-sets directly; v2.32.3 promoted the three
//   sets to PlaceholderGuard and added typed Inspect() entry-points.
//
//   v3.0 Phase 3D (2026-05-18): the 6-layer defense (F-A..F-E plus
//   Wave 7c-1's deep-verify gate) was consolidated into
//   `PlaceholderDefense` with internal sub-classes per layer. This file
//   stays as a one-line-per-member forwarder so the ~13 existing call
//   sites compile unchanged. New code should call `PlaceholderDefense`
//   directly. The forwarder will eventually be removed (probably v3.1
//   or whenever the last hold-out call site gets renamed).
//
// See VPNRouter.Core/Services/PlaceholderDefense.cs for the consolidated
// fingerprint table + layer sub-classes + design rationale.

#nullable enable

using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Back-compat thin forwarder for the consolidated
/// <see cref="PlaceholderDefense"/> API. Every member here is a one-line
/// pass-through; new code should call <see cref="PlaceholderDefense"/>
/// directly. Existing callers (parsers, subscription fetcher, custom-config
/// injector, Android storage, settings migrator, deep verifier) keep
/// compiling unchanged via this shim.
/// </summary>
public static class PlaceholderGuard
{
    /// <summary>Reality public-key fingerprints — forwards to <see cref="PlaceholderDefense.KnownPubkeys"/>.</summary>
    public static IReadOnlySet<string> KnownPubkeys => PlaceholderDefense.KnownPubkeys;

    /// <summary>Reality short_id fingerprints — forwards to <see cref="PlaceholderDefense.KnownShortIds"/>.</summary>
    public static IReadOnlySet<string> KnownShortIds => PlaceholderDefense.KnownShortIds;

    /// <summary>Server-IP fingerprints — forwards to <see cref="PlaceholderDefense.KnownServers"/>.</summary>
    public static IReadOnlySet<string> KnownServers => PlaceholderDefense.KnownServers;

    /// <summary>Entry-typed inspection — forwards to <see cref="PlaceholderDefense.Inspect(VlessServerEntry?)"/>.</summary>
    public static string? Inspect(VlessServerEntry? entry) => PlaceholderDefense.Inspect(entry);

    /// <summary>Tri-field inspection — forwards to <see cref="PlaceholderDefense.Inspect(string?, string?, string?)"/>.</summary>
    public static string? Inspect(string? realityPubkey, string? realityShortId, string? server) =>
        PlaceholderDefense.Inspect(realityPubkey, realityShortId, server);

    /// <summary>Boolean tri-field convenience — forwards to <see cref="PlaceholderDefense.IsPlaceholder(string?, string?, string?)"/>.</summary>
    public static bool IsPlaceholder(string? realityPubkey, string? realityShortId, string? server) =>
        PlaceholderDefense.IsPlaceholder(realityPubkey, realityShortId, server);

    /// <summary>Boolean entry-typed convenience — forwards to <see cref="PlaceholderDefense.IsPlaceholder(VlessServerEntry?)"/>.</summary>
    public static bool IsPlaceholder(VlessServerEntry? entry) => PlaceholderDefense.IsPlaceholder(entry);

    /// <summary>URI inspection — forwards to <see cref="PlaceholderDefense.InspectUri"/>.</summary>
    public static string? InspectUri(string? uri) => PlaceholderDefense.InspectUri(uri);
}
