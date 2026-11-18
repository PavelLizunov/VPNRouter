using System.Text.Json;
using System.Text.Json.Nodes;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services.FreeConfigs;

public static class FreeConfigSelection
{
    public static int SelectOrAdd(List<VlessServerEntry> servers, VlessServerEntry verified)
    {
        if (!string.IsNullOrEmpty(verified.DetourVia))
            throw new InvalidOperationException(DeepVerifyConstants.DetourNote);

        var names = servers.ToLookup(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var profile = ConnectionSettings(verified);
        for (int i = 0; i < servers.Count; i++)
        {
            var saved = servers[i];
            if (saved.Port != verified.Port || !string.Equals(saved.Server, verified.Server, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(saved.Name) || names[saved.Name].Count() != 1)
                continue;
            if (JsonNode.DeepEquals(ConnectionSettings(saved), profile))
            {
                saved.UseAsSingleOutbound = true;
                return i;
            }
        }

        var baseName = string.IsNullOrWhiteSpace(verified.Name) ? verified.Server : verified.Name;
        var name = baseName;
        for (int suffix = 2; names.Contains(name); suffix++)
            name = $"{baseName} #{suffix}";
        verified.Name = name;
        verified.UseAsSingleOutbound = true;
        servers.Add(verified);
        return servers.Count - 1;
    }

    private static JsonObject ConnectionSettings(VlessServerEntry entry)
    {
        var settings = JsonSerializer.SerializeToNode(entry, AppJsonContext.Default.VlessServerEntry)!.AsObject();
        settings.Remove(nameof(entry.Name));
        settings.Remove(nameof(entry.UseAsSingleOutbound));
        return settings;
    }
}
