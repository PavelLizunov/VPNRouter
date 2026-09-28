using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public class CustomDirectRule
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "domain_suffix";

    [YamlMember(Alias = "value")]
    public string Value { get; set; } = string.Empty;

    [YamlMember(Alias = "comment")]
    public string Comment { get; set; } = string.Empty;

    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;
}
