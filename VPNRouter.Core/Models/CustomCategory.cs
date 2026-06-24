using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

/// <summary>A user-created Applications category.</summary>
public class CustomCategory
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "apps")]
    public List<string> Apps { get; set; } = new();

    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;
}
