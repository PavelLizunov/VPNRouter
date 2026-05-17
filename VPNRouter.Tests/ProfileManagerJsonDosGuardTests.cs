using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// v2.31.0-r1 Pillar 1 — Core stability fixes
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>v2.31.0-r1 (CO-4 audit fix): JSON deserialization of profiles is
/// MaxDepth-capped to neutralize DoS via deeply-nested arrays. Real ProfileCollection
/// is shallow (~3 levels) so 32 leaves head-room. Test that adversarial input
/// is rejected before triggering stack overflow / extreme allocation.</summary>
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

        // SafeJsonSettings caps MaxDepth — Newtonsoft throws JsonReaderException
        // when limit is exceeded. We assert it throws (any kind) — the point
        // is that we never let it run to stack-overflow / process crash.
        Assert.ThrowsAny<Newtonsoft.Json.JsonException>(() =>
            Newtonsoft.Json.JsonConvert.DeserializeObject<ProfileCollection>(
                json, ProfileManager.SafeJsonSettings));
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

        var result = Newtonsoft.Json.JsonConvert.DeserializeObject<ProfileCollection>(
            json, ProfileManager.SafeJsonSettings);

        Assert.NotNull(result);
        Assert.NotNull(result.Profiles);
        Assert.Single(result.Profiles);
        Assert.Equal("Test_Profile", result.Profiles[0].Name);
    }
}
