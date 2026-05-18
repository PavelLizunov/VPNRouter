using System.Text.Json.Serialization;

namespace VPNRouter.Core.Models;

// Phase 3 — 3B (2026-05-18): migrated from Newtonsoft.Json [JsonProperty]
// to System.Text.Json [JsonPropertyName]. Same field names → byte-identical
// wire format. See plans/phase3-3B-newtonsoft-to-stj-2026-05-18.md.

public class ProcessRule
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("include_children")]
    public bool IncludeChildren { get; set; } = true;

    [JsonPropertyName("scan_patterns")]
    public string[] ScanPatterns { get; set; } = Array.Empty<string>();
}
