using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public class ProfileSource
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = string.Empty; // github | local

    [YamlMember(Alias = "url")]
    public string? Url { get; set; }

    [YamlMember(Alias = "path")]
    public string? Path { get; set; }

    [YamlMember(Alias = "update_interval")]
    public int UpdateInterval { get; set; } = 3600;
}
