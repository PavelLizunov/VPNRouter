using System.Text.Json.Serialization;

namespace VPNRouter.Core.Models;

public class ProcessRule
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("include_children")]
    public bool IncludeChildren { get; set; } = true;

    [JsonPropertyName("scan_patterns")]
    public string[] ScanPatterns { get; set; } = Array.Empty<string>();
}
