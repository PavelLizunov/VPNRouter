using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using VPNRouter.Core.Json;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public class SubscriptionEntry
{
    internal static string RowKey(VlessServerEntry entry)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, AppJsonContext.Default.VlessServerEntry);
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    // Native imports issue fresh opaque IDs for the same detour on every refresh. Compare the
    // outbound and its resolved hop to recognize that route when its exact stored row was replaced.
    internal static string RouteKey(VlessServerEntry entry, IEnumerable<VlessServerEntry> siblings)
    {
        static JsonObject Outbound(VlessServerEntry server)
        {
            var json = JsonSerializer.SerializeToNode(server, AppJsonContext.Default.VlessServerEntry)!.AsObject();
            json.Remove(nameof(server.Name));
            json.Remove(nameof(server.OutboundId));
            json.Remove(nameof(server.DetourVia));
            return json;
        }

        var route = Outbound(entry);
        if (!string.IsNullOrEmpty(entry.DetourVia))
        {
            var upstream = siblings.Where(s => string.IsNullOrEmpty(s.DetourVia) &&
                string.Equals(s.OutboundId, entry.DetourVia, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            route[nameof(entry.DetourVia)] = upstream.Count == 1 ? Outbound(upstream[0]) : JsonValue.Create(entry.DetourVia);
        }
        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            using var writer = new Utf8JsonWriter(buffer);
            route.WriteTo(writer);
            writer.Flush();
            return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
        }
        finally { buffer.Clear(); }
    }

    internal VlessServerEntry? FindExactRow(string? name, string? key) => key == null ? null :
        Servers.FirstOrDefault(row => row.Name == name && RowKey(row) == key);

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

    [YamlMember(Alias = "user_info")]
    public string? UserInfo { get; set; }
}
