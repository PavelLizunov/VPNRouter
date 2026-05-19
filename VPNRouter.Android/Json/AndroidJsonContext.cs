#nullable enable

// Phase 6 — Wave 28 6-AJ-1 (2026-05-18): sibling Android-side
// JsonSerializerContext for DTOs that VPNRouter.Core's
// AppJsonContext cannot reach.
// Brief: plans/phase6-android-jsoncontext-memory-2026-05-18.md
//
// Why: Wave 25 (commit d9b0788) wired a JsonSerializerContext for 13
// Core-resident DTOs but deferred two shapes that the AndroidStorage
// SharedPreferences round-trip serializes:
//
//   * AndroidStorage.ServerTestResultDto — Android-only test-history
//     side-table entry (status / latency / last-tested / error).
//     Lives in VPNRouter.Android, so VPNRouter.Core cannot reference
//     it without inverting the assembly dependency.
//
//   * CustomCategory + List<CustomCategory> — Core-resident type
//     (VPNRouter.Core.Models.CustomCategory) but Wave 25 declined to
//     register the List<T> wrapper in Core to avoid pulling YamlDotNet-
//     adjacent shapes into the AOT-pinned surface for ProfileManager /
//     ConfigGenerator / etc. — those options instances never hit
//     CustomCategory. The Android SharedPreferences blob does.
//
//   * List<string> — the per-app-packages persistent blob (a flat
//     list of Android package IDs). Primitive collection; STJ's
//     built-in support handles the inner string, but registering
//     the List<string> wrapper here pins the resolver to a static
//     JsonTypeInfo<List<string>> instead of a reflective walk at AOT
//     time.
//
// AndroidStorage.JsonOptions chains this context FIRST in the
// JsonTypeInfoResolver.Combine call so the Android-specific shapes
// are resolved before the Core context is consulted. The
// DefaultJsonTypeInfoResolver fallback at the end keeps the door
// open for any one-off anonymous shapes (none today, future-proof).
//
// Type list order: alphabetical for deterministic generator output —
// matches the convention in VPNRouter.Core/Json/AppJsonContext.cs so
// future-Phase audits can diff the two contexts uniformly.

using System.Collections.Generic;
using System.Text.Json.Serialization;
using VPNRouter.Core.Models;

namespace VPNRouter.Android.Json;

/// <summary>
/// Phase 6 — Wave 28 6-AJ-1: Android-side
/// <see cref="JsonSerializerContext"/> for DTOs that are either
/// Android-only (lives in <c>VPNRouter.Android</c>, unreachable from
/// Core) or intentionally excluded from
/// <see cref="VPNRouter.Core.Json.AppJsonContext"/> because no Core
/// options instance touches them.
///
/// <para>Generator options mirror
/// <see cref="VPNRouter.Core.Json.AppJsonContext"/> exactly so the
/// composed resolver chain (this context first, then the Core context,
/// then the reflective fallback) produces byte-equivalent output to
/// pre-Phase-6 blobs:
/// <list type="bullet">
///   <item><c>DefaultIgnoreCondition = WhenWritingNull</c> — same
///   defence-in-depth as the Core context; per-property
///   <c>[JsonIgnore(Condition=WhenWritingNull)]</c> already pins the
///   contract on every nullable optional in the registered DTOs.</item>
///   <item><c>PropertyNameCaseInsensitive = true</c> — same lookup
///   posture so legacy Newtonsoft-default SharedPreferences blobs
///   continue to deserialize unchanged after AOT activation.</item>
/// </list>
/// </para>
///
/// <para>Registered types (alphabetical):
/// <list type="bullet">
///   <item><see cref="CustomCategory"/> — user-defined Applications
///   category (Name + Apps[] + Enabled). Persisted via
///   <see cref="VPNRouter.Android.AndroidStorage.SetCustomCategories"/>.</item>
///   <item><see cref="Dictionary{TKey, TValue}"/> of
///   <c>string → ServerTestResultDto</c> — the per-server probe
///   history keyed by VlessServersResolver dedup shape
///   (<c>Server:Port:Uuid:Flow</c>). Persisted via
///   <see cref="VPNRouter.Android.AndroidStorage.SetServerTestResults"/>.</item>
///   <item><see cref="List{T}"/> of <see cref="CustomCategory"/> — the
///   collection wrapper for the SharedPreferences blob.</item>
///   <item><see cref="List{T}"/> of <see cref="string"/> — the
///   per-app-packages flat list (Android package IDs).</item>
///   <item><see cref="VPNRouter.Android.AndroidStorage.ServerTestResultDto"/>
///   — one entry in the test-results map; <c>status</c>, <c>latency_ms</c>,
///   <c>last_tested_at</c>, optional <c>error</c>.</item>
/// </list>
/// </para>
///
/// <para>Phase 7 candidate: when NativeAOT is enabled
/// (<c>&lt;PublishAot&gt;true&lt;/PublishAot&gt;</c>), the trim/AOT
/// audit may surface more reflective serialization paths that need
/// registration here.</para>
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CustomCategory))]
[JsonSerializable(typeof(Dictionary<string, AndroidStorage.ServerTestResultDto>))]
[JsonSerializable(typeof(List<CustomCategory>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(AndroidStorage.ServerTestResultDto))]
internal sealed partial class AndroidJsonContext : JsonSerializerContext
{
}
