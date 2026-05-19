#nullable enable

// Phase 6 — Wave 31b (2026-05-19): CLI-side JsonSerializerContext for
// DTOs that VPNRouter.Core's AppJsonContext cannot reach (because Core
// cannot reference CLI types — the dependency direction is one-way).
// Brief: plans/phase6-json-cleanups-2026-05-18.md.
//
// Mirrors the sibling-context pattern Wave 28 established for Android
// (VPNRouter.Android/Json/AndroidJsonContext.cs). StateFile.Options
// chains this context FIRST in its TypeInfoResolver.Combine call so the
// CLI-specific shapes are resolved before the Core context is consulted.
// The reflective DefaultJsonTypeInfoResolver at the end keeps the door
// open for any one-off shape (none today, future-proof).

using System.Text.Json.Serialization;
using VPNRouter.CLI.Commands;

namespace VPNRouter.CLI.Helpers;

/// <summary>
/// Phase 6 — Wave 31b: CLI-side <see cref="JsonSerializerContext"/> for
/// DTOs that live in the CLI assembly and therefore cannot be registered
/// in <c>VPNRouter.Core.Json.AppJsonContext</c> (Core has no reference
/// to CLI).
///
/// <para>Currently registers <see cref="RunState"/> — the state.json
/// payload persisted by <c>StateFile.Write</c> and consulted by
/// <c>stop</c>/<c>status</c> commands from a fresh CLI process.</para>
///
/// <para>Generator options mirror
/// <see cref="VPNRouter.Core.Json.AppJsonContext"/> exactly so the
/// composed resolver chain (this context first, then the Core context,
/// then the reflective fallback) produces byte-equivalent output to
/// pre-Phase-6 blobs:
/// <list type="bullet">
///   <item><c>DefaultIgnoreCondition = WhenWritingNull</c> — same
///   defence-in-depth as the Core context. <see cref="RunState"/> has
///   no nullable optionals today; this is forward-compat.</item>
///   <item><c>PropertyNameCaseInsensitive = true</c> — tolerates the
///   pre-Phase-4 PascalCase state.json blobs Newtonsoft produced.</item>
/// </list>
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RunState))]
internal sealed partial class CliJsonContext : JsonSerializerContext
{
}
