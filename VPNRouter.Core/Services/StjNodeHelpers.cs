#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VPNRouter.Core.Services;

/// <summary>
/// Phase 4 (2026-05-18) — null-safe accessors over <see cref="JsonNode"/>
/// trees. Mirrors Newtonsoft's <c>JToken.Value&lt;T&gt;()</c> semantics which
/// returned <c>null</c> instead of throwing when the underlying value was
/// of the wrong kind. STJ's <see cref="JsonNode.GetValue{T}"/> throws on
/// kind mismatch — that's the right behaviour for a strict reader, but
/// the legacy Newtonsoft.Linq code we're replacing was permissive (a
/// query like <c>jo["server"]?.Value&lt;string&gt;()</c> would return null
/// when the value was missing, a number, or an object).
///
/// <para>These helpers preserve that permissive behaviour so the
/// pre-Phase-4 control flow (null-coalesce-or-default) keeps working
/// across the migration. Throwing-on-malformed remains an explicit
/// choice the caller makes by going through <see cref="JsonNode.GetValue{T}"/>
/// directly.</para>
/// </summary>
internal static class StjNodeHelpers
{
    /// <summary>
    /// Returns the node's value as a string. Permissive: returns null if
    /// the node is null, not a JsonValue, or wraps a non-string kind
    /// (e.g. a number, bool, or array). Mirrors Newtonsoft's
    /// <c>JToken.Value&lt;string?&gt;()</c>.
    /// </summary>
    public static string? AsString(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s;
            // Newtonsoft converted scalars to string via ToString.
            // Match that fallback so e.g. {"port":"443"} probes still work.
            try { return jv.ToJsonString().Trim('"'); }
            catch { return null; }
        }
        return null;
    }

    /// <summary>
    /// Returns the node's value as a nullable int. Permissive: returns
    /// null if the node is null, not a JsonValue, or wraps a non-numeric
    /// kind. Strings that look like numbers are parsed (mirrors
    /// Newtonsoft's int coercion).
    /// </summary>
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

    /// <summary>
    /// Returns the node's value as a nullable bool. Permissive: returns
    /// null if the node is null, not a JsonValue, or wraps a non-bool
    /// kind. Mirrors Newtonsoft's <c>JToken.Value&lt;bool?&gt;()</c>.
    /// </summary>
    public static bool? AsBool(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
        return null;
    }

    /// <summary>
    /// Walks a dot-separated path through nested JsonObjects. Returns
    /// <c>null</c> at the first missing step. Mirrors Newtonsoft's
    /// <c>SelectToken("dns.servers")</c> for the limited dotted-path
    /// case (no array indexing, no JSONPath operators — we never used
    /// those in the migrated call sites).
    /// </summary>
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
