using System.Text.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class ProfileManagerJsonDosGuardTests
{
    [Fact]
    public void DeeplyNestedArray_ThrowsBeforeStackOverflow()
    {
        var depth = 100;
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"profiles\":[");
        for (int i = 0; i < depth; i++) sb.Append("[");
        for (int i = 0; i < depth; i++) sb.Append("]");
        sb.Append("]}");
        var json = sb.ToString();

        Assert.ThrowsAny<JsonException>(() =>
            JsonSerializer.Deserialize<ProfileCollection>(
                json, ProfileManager.SafeJsonOptions));
    }

    [Fact]
    public void NormalProfileJson_DeserializesUnderLimit()
    {
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
