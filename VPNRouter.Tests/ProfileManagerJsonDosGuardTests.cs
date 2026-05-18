using System.Text.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// v2.31.0-r1 Pillar 1 — Core stability fixes
// Phase 3B (2026-05-18): migrated assertions from Newtonsoft.Json.JsonException to
// System.Text.Json.JsonException. ProfileManager.SafeJsonOptions caps MaxDepth=32
// just like the pre-migration SafeJsonSettings did — same fail-closed semantics.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>v2.31.0-r1 (CO-4 audit fix): JSON deserialization of profiles is
/// MaxDepth-capped to neutralize DoS via deeply-nested arrays. Real ProfileCollection
/// is shallow (~3 levels) so 32 leaves head-room. Test that adversarial input
/// is rejected before triggering stack overflow / extreme allocation.
///
/// <para>Phase 3B (2026-05-18) — STJ migration. <see cref="JsonSerializer.Deserialize"/>
/// throws <see cref="JsonException"/> when <see cref="JsonSerializerOptions.MaxDepth"/>
/// is exceeded, matching Newtonsoft's JsonReaderException behaviour. The guard is
/// the contract; the exception type is implementation detail.</para>
/// </summary>
public class ProfileManagerJsonDosGuardTests
{
    [Fact]
    public void DeeplyNestedArray_ThrowsBeforeStackOverflow()
    {
        // Build a JSON string with 100 levels of nesting (well past our cap of 32).
        var depth = 100;
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"profiles\":[");
        for (int i = 0; i < depth; i++) sb.Append("[");
        for (int i = 0; i < depth; i++) sb.Append("]");
        sb.Append("]}");
        var json = sb.ToString();

        // SafeJsonOptions caps MaxDepth — STJ throws JsonException when the
        // limit is exceeded. We assert it throws (any kind) — the point
        // is that we never let it run to stack-overflow / process crash.
        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<ProfileCollection>(
                json, ProfileManager.SafeJsonOptions));
    }

    [Fact]
    public void NormalProfileJson_DeserializesUnderLimit()
    {
        // A realistic profile JSON has at most ~4 levels of nesting:
        // root → profiles[] → profile object → processes[] → process object.
        // Should round-trip cleanly under MaxDepth=32.
        var json = """
        {
          "profiles": [
            {
              "name": "Test_Profile",
              "processes": [
                { "name": "test.exe", "include_children": false }
              ]
            }
          ]
        }
        """;

        var result = JsonSerializer.Deserialize<ProfileCollection>(
            json, ProfileManager.SafeJsonOptions);

        Assert.NotNull(result);
        Assert.NotNull(result.Profiles);
        Assert.Single(result.Profiles);
        Assert.Equal("Test_Profile", result.Profiles[0].Name);
    }
}
