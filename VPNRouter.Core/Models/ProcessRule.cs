using Newtonsoft.Json;

namespace VPNRouter.Core.Models;

public class ProcessRule
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("include_children")]
    public bool IncludeChildren { get; set; } = true;

    [JsonProperty("scan_patterns")]
    public string[] ScanPatterns { get; set; } = Array.Empty<string>();
}
