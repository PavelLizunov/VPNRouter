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
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ConfigShareDocument))]
[JsonSerializable(typeof(GitHubAsset))]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(GitHubRelease[]))]
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
