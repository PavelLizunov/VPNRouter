#nullable enable

// Phase 5 — Wave 25 AOT-2 (2026-05-18): System.Text.Json source-gen for AOT.
// Brief: plans/phase5-config-example-aot-prep-2026-05-18.md
//
// Why: Phase 4 retired Newtonsoft.Json from VPNRouter.Core (4 csprojs dropped
// the package). System.Text.Json is AOT-friendly but ONLY when used with a
// JsonSerializerContext-based source generator. Without it, AOT compilation
// fails at runtime when the reflective DefaultJsonTypeInfoResolver tries to
// inspect types whose metadata has been trimmed.
//
// This context registers the highest-traffic DTOs in VPNRouter.Core so a
// future PublishAot build (Phase 6 candidate — Android NativeAOT for the
// 4x startup win) can resolve their JsonTypeInfo at compile time. The
// generator emits a `JsonTypeInfo<T>` per registered type into a sibling
// `AppJsonContext.g.cs` file; runtime serialization for registered types
// becomes a static lookup instead of reflective walks of property metadata.
//
// Composition over replacement: existing `JsonSerializerOptions` instances
// (ProfileManager.SafeJsonOptions, GitHubReleaseSource.GitHubReleaseJsonOptions,
// ConfigGenerator.SingBoxOptions, ConfigShareDocument.DocumentOptions,
// AndroidStorage.JsonOptions) wire `TypeInfoResolver` to
// `JsonTypeInfoResolver.Combine(AppJsonContext.Default,
// new DefaultJsonTypeInfoResolver())`. The context resolves registered types
// (fast, AOT-safe); the reflective fallback handles every other type we
// serialize anonymously (e.g. one-off `new { schema_version, ... }` shapes
// in RunState). Phase 6 retires the reflective fallback once a broader DTO
// audit lands every remaining type here.
//
// Type list registration order: alphabetical for deterministic build output.
// When adding a new type, keep the list sorted. The source generator emits
// resolver tables in declaration order; a stable order keeps generated-file
// diffs reviewable.

using System.Collections.Generic;
using System.Text.Json.Serialization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.Core.Json;

/// <summary>
/// Phase 5 — Wave 25 AOT-2: the single <see cref="JsonSerializerContext"/>
/// for VPNRouter.Core's hot-path DTOs.
///
/// <para>Source-generated at build time. The generator inspects every
/// <c>[JsonSerializable(typeof(T))]</c> attribute below and emits a
/// <c>JsonTypeInfo&lt;T&gt;</c> resolver for it (plus, recursively, for
/// every nested type T references). Result: registered types serialize
/// without reflection, satisfying the AOT trimmer's "no dynamic code"
/// guarantee.</para>
///
/// <para>Generator options applied via the
/// <c>[JsonSourceGenerationOptions]</c> attribute mirror the union of what
/// our hot-path options instances already enable individually:
/// <list type="bullet">
///   <item><c>DefaultIgnoreCondition = WhenWritingNull</c> — matches
///   <see cref="ConfigGenerator.SingBoxOptions"/> and
///   <see cref="ConfigShareDocument.DocumentOptions"/>. Per-property
///   <c>[JsonIgnore(Condition=WhenWritingNull)]</c> already pins the
///   contract on every nullable optional in the DTO tree; this is a
///   defence-in-depth backstop. WriteIndented stays at the runtime default
///   here — the per-options-instance side overrides via
///   <c>JsonSerializer.Serialize(..., options)</c> where the per-call
///   indentation switch is what actually drives output formatting.</item>
///   <item><c>PropertyNameCaseInsensitive = true</c> — matches every
///   existing options instance. Newtonsoft was case-insensitive by default;
///   STJ keeping that posture means hand-edited cache files / Android
///   SharedPreferences blobs deserialize unchanged.</item>
/// </list>
/// </para>
///
/// <para>Note on <see cref="VPNRouter.Android.AndroidStorage.ServerTestResultDto"/>:
/// the brief listed it as one of the 10 candidates. It lives in
/// <c>VPNRouter.Android</c> (assembly direction: Android references Core,
/// not the reverse), so Core cannot reference it without a circular
/// dependency. A sibling Android-side context can wire it later as a
/// Phase 6 follow-up; the reflective fallback handles it today.</para>
///
/// <para><see cref="ConfigShareDocument"/> takes the 10th slot in its place
/// — also high-traffic on Android (Bug-AND-023 QR scan flow) and desktop
/// (manual export/import), and properly Core-resident. Plus the
/// <see cref="GitHubRelease"/> array shape and the
/// <see cref="List{T}"/>-wrappers for the Android subscription/server
/// blobs round the registered surface to 13 entries — comfortably above
/// the "10+" verification gate.</para>
/// </summary>
// Phase 7 Wave 34 additions (2026-05-19): registered the last 4 DTOs that
// were still going through the reflective fallback at hot-path call sites:
//   CacheRecovery.SchemaProbe — schema-version probe on cache recovery
//   FreeConfigCache.CacheFile — free-config persistent cache wrapper
//   CustomRule / List<CustomRule> — custom-rules import/export (the
//     List<Dictionary<string,object>> sing-box-native export branch stays
//     on reflective until Wave 35 restructures it to a concrete record tree)
// Also requires CacheRecovery.SchemaProbe visibility flip private→internal
// (the source generator can't reference a private nested type from outside
// the enclosing class). InternalsVisibleTo lets the context see it without
// promoting the surface beyond Core's assembly.
// Phase 7 Wave 34: MaxDepth=32 added on the context so JsonTypeInfo<T>
// overloads inherit the JSON-DoS guard that ProfileManager.SafeJsonOptions
// pioneered in v2.31.0-r1 (CO-4). All registered types in this context
// stay well under 32-level nesting (deepest: ConfigShareDocument wrapping
// SingBoxConfig, ~6 levels). Tightening MaxDepth globally hardens every
// deserialize path against degenerate-nesting input — same posture, broader
// coverage. Any future type that needs deeper nesting would either need a
// sibling context or a per-call options override.
// Phase 7 Wave 34: WriteIndented=true at context level — every site
// using the JsonTypeInfo<T> overload inherits human-readable JSON:
//   * sing-box JSON (ConfigGenerator) — diagnostics
//   * config-share document — manual export inspection
//   * profile cache file — human-readable cache
//   * launch failure counter state, CLI state.json — small inspectable files
//
// Trade-off: FreeConfigCache.Save flipped from WriteIndented=false →
// true. The cache file is internal-only (no NekoBox/Hiddify interop),
// grows ~5-10% on disk (~30MB → ~32MB at 25k entries), and refreshes
// every ~6h via background cron — not a user-facing latency path.
// CacheRecoveryTests.FreeConfigCache_Save_StampsCurrentSchemaVersion
// updated to whitespace-tolerant regex per this behaviour change.
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    MaxDepth = 32,
    // Phase 7 Wave 34: AllowReadingFromString carried over from
    // ClashSingBoxApi's pre-Wave-34 SerializerOptions. sing-box can
    // emit numeric fields as JSON strings ("12345") in some responses
    // (download/uploadTotal counters in /connections); permissive
    // number-from-string parsing tolerates that. Applies to all
    // numeric properties on registered types but is a read-only widening
    // (no effect on serialized output).
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(CacheRecovery.SchemaProbe))]
[JsonSerializable(typeof(ClashSelectProxyDto))]
[JsonSerializable(typeof(ClashSetConfigDto))]
[JsonSerializable(typeof(ClashSingBoxApi.ConnectionsDto))]
[JsonSerializable(typeof(ClashSingBoxApi.ProxiesEnvelopeDto))]
[JsonSerializable(typeof(ClashSingBoxApi.VersionDto))]
[JsonSerializable(typeof(ConfigShareDocument))]
[JsonSerializable(typeof(CustomRule))]
[JsonSerializable(typeof(FreeConfigCache.CacheFile))]
[JsonSerializable(typeof(GitHubAsset))]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(GitHubRelease[]))]
[JsonSerializable(typeof(LaunchFailureCounter.State))]
[JsonSerializable(typeof(List<CustomRule>))]
[JsonSerializable(typeof(List<SubscriptionEntry>))]
[JsonSerializable(typeof(List<VlessServerEntry>))]
[JsonSerializable(typeof(ProcessRule))]
[JsonSerializable(typeof(Profile))]
[JsonSerializable(typeof(ProfileCacheFile))]
[JsonSerializable(typeof(ProfileCollection))]
[JsonSerializable(typeof(SingBoxConfig))]
[JsonSerializable(typeof(SubscriptionEntry))]
[JsonSerializable(typeof(VlessServerEntry))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}
