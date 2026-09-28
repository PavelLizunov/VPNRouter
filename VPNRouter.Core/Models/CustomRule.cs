using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public class CustomRule
{
    [YamlMember(Alias = "action")]
    [JsonPropertyName("action")]
    public string Action { get; set; } = "direct";

    [YamlMember(Alias = "type")]
    [JsonPropertyName("type")]
    public string Type { get; set; } = "domain_suffix";

    [YamlMember(Alias = "value")]
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [YamlMember(Alias = "comment")]
    [JsonPropertyName("comment")]
    public string Comment { get; set; } = string.Empty;

    [YamlMember(Alias = "enabled")]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}
