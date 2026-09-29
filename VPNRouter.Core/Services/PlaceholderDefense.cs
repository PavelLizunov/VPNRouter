#nullable enable

using System.Text.Json.Nodes;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public sealed record PlaceholderFingerprint
{
    public string? Pubkey { get; init; }

    public string? ShortId { get; init; }

    public string? Server { get; init; }

    public string Origin { get; init; } = string.Empty;
}

public static class PlaceholderDefense
{
    private static readonly IReadOnlyList<PlaceholderFingerprint> s_known =
        new List<PlaceholderFingerprint>
        {
            new PlaceholderFingerprint
            {
                Pubkey = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU",
                Origin = "PlaceholderVlessUri smoke-test (Android pre-r10 / DEFCT-005)",
            },
            new PlaceholderFingerprint
            {
                ShortId = "78ca7952",
                Origin = "PlaceholderVlessUri smoke-test (Android pre-r10 / DEFCT-005)",
            },
            new PlaceholderFingerprint
            {
                Server = "195.135.255.216",
                Origin = "Stas-evidence khunrath_ln endpoint",
            },
        };

    public static IReadOnlyList<PlaceholderFingerprint> KnownFingerprints => s_known;

    private static readonly IReadOnlySet<string> s_knownPubkeys =
        s_known
            .Select(f => f.Pubkey)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

    private static readonly IReadOnlySet<string> s_knownShortIds =
        s_known
            .Select(f => f.ShortId)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

    private static readonly IReadOnlySet<string> s_knownServers =
        s_known
            .Select(f => f.Server)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.Ordinal)!;

    public static IReadOnlySet<string> KnownPubkeys => s_knownPubkeys;

    public static IReadOnlySet<string> KnownShortIds => s_knownShortIds;

    public static IReadOnlySet<string> KnownServers => s_knownServers;

    public static string? Inspect(VlessServerEntry? entry)
    {
        if (entry is null) return null;

        var pubkey = entry.Reality?.PublicKey;
        if (!string.IsNullOrEmpty(pubkey) && s_knownPubkeys.Contains(pubkey))
            return "reality.public_key";

        var shortId = entry.Reality?.ShortId;
        if (!string.IsNullOrEmpty(shortId) && s_knownShortIds.Contains(shortId))
            return "reality.short_id";

        var server = entry.Server;
        if (!string.IsNullOrEmpty(server) && s_knownServers.Contains(server))
            return "server";

        return null;
    }

    public static string? Inspect(string? realityPubkey, string? realityShortId, string? server)
    {
        if (!string.IsNullOrEmpty(realityPubkey) && s_knownPubkeys.Contains(realityPubkey))
            return "reality.public_key";
        if (!string.IsNullOrEmpty(realityShortId) && s_knownShortIds.Contains(realityShortId))
            return "reality.short_id";
        if (!string.IsNullOrEmpty(server) && s_knownServers.Contains(server))
            return "server";
        return null;
    }

    public static bool IsPlaceholder(string? realityPubkey, string? realityShortId, string? server) =>
        Inspect(realityPubkey, realityShortId, server) != null;

    public static bool IsPlaceholder(VlessServerEntry? entry) => Inspect(entry) != null;

    public static string? InspectUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        try
        {
            var parsed = ServerUriParser.Parse(uri);
            return Inspect(parsed);
        }
        catch (PlaceholderConfigException ex)
        {
            return ex.OffendingField;
        }
        catch
        {
            return null;
        }
    }

    internal static class LayerA_ResolverScopeGuard
    {
        public static bool IsPlaceholderEntry(VlessServerEntry? entry)
        {
            if (entry is null) return false;
            return PlaceholderDefense.Inspect(entry) is not null;
        }
    }

    internal static class LayerE_RuntimeSanity
    {
        public static JsonObject? FindFirstProxyOutbound(JsonArray outbounds)
        {
            foreach (var node in outbounds)
            {
                if (node is not JsonObject ob) continue;
                var type = StjNodeHelpers.AsString(ob["type"])?.ToLowerInvariant() ?? "";
                if (type is "vless" or "hysteria2" or "tuic" or "shadowsocks" or "naive" or "trojan")
                    return ob;
            }
            return null;
        }

        public static string? InspectOutbound(JsonObject? proxy)
        {
            if (proxy == null) return null;

            var reality = proxy["tls"]?["reality"] as JsonObject;
            var pubkey = StjNodeHelpers.AsString(reality?["public_key"]);
            var shortId = StjNodeHelpers.AsString(reality?["short_id"]);
            var server = StjNodeHelpers.AsString(proxy["server"]);

            return PlaceholderDefense.Inspect(pubkey, shortId, server);
        }
    }
}

public sealed class PlaceholderConfigException : Exception
{
    public string OffendingField { get; }

    public string OffendingValue { get; }

    public PlaceholderConfigException(string offendingField, string offendingValue)
        : base($"Credential rejected: {offendingField} matches a known placeholder fingerprint ({TruncateForMessage(offendingValue)}). " +
               "Get a real vless:// URL from your VPN provider.")
    {
        OffendingField = offendingField;
        OffendingValue = offendingValue;
    }

    private static string TruncateForMessage(string s) =>
        string.IsNullOrEmpty(s) ? "(empty)" :
        s.Length <= 16 ? s :
        $"{s[..8]}…{s[^4..]}";
}
