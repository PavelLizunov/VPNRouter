using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

/// <summary>A single VLESS subscription source (URL + its servers).</summary>
public class SubscriptionEntry
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "url")]
    public string Url { get; set; } = string.Empty;

    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    [YamlMember(Alias = "last_refreshed_at")]
    public DateTimeOffset? LastRefreshedAt { get; set; }

    [YamlMember(Alias = "last_server_count")]
    public int LastServerCount { get; set; }

    [YamlMember(Alias = "servers")]
    public List<VlessServerEntry> Servers { get; set; } = new();

    // P2 (2026-06-21) — raw `Subscription-Userinfo` response header from the last
    // fetch (e.g. "upload=..; download=..; total=..; expire=.."). Parsed for display
    // via SubscriptionUserInfo.Parse. Null when the provider doesn't send it.
    [YamlMember(Alias = "user_info")]
    public string? UserInfo { get; set; }
}
