#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VPNRouter.Core.Services;

internal static class StjNodeHelpers
{
    public static string? AsString(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s;
            try { return jv.ToJsonString().Trim('"'); }
            catch { return null; }
        }
        return null;
    }

    public static int? AsInt(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<int>(out var i)) return i;
            if (jv.TryGetValue<long>(out var l)) return (int)l;
            if (jv.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed)) return parsed;
        }
        return null;
    }

    public static bool? AsBool(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
        return null;
    }

    public static JsonNode? SelectToken(JsonNode? root, string dottedPath)
    {
        if (root is null) return null;
        if (string.IsNullOrEmpty(dottedPath)) return root;

        JsonNode? current = root;
        foreach (var segment in dottedPath.Split('.'))
        {
            if (current is not JsonObject obj) return null;
            if (!obj.TryGetPropertyValue(segment, out var next)) return null;
            current = next;
        }
        return current;
    }
}
