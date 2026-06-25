using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

/// <summary>
/// v2.14.4 — user-provided source URL for Free Configs aggregation.
/// Private subscriptions that user wants to include alongside the 14 public sources.
/// </summary>
public class UserFreeSource
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "url")]
    public string Url { get; set; } = string.Empty;

    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    [YamlMember(Alias = "added_at")]
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
