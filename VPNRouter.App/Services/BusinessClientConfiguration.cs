using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

// Read only from trusted provisioning, never from a downloaded subscription response.
internal sealed class BusinessClientConfiguration
{
    internal Uri? Subscription { get; private init; }
    internal Uri? NodesEndpoint { get; private init; }
    internal Uri? UpdatesEndpoint { get; private init; }
    internal Uri? SupportEndpoint { get; private init; }
    internal string? SupportToken { get; private init; }
    internal string? SourceHash => Subscription == null ? null :
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Subscription.AbsoluteUri)));

    internal static BusinessClientConfiguration Empty { get; } = new();

    internal static BusinessClientConfiguration Parse(string bundle)
    {
        if (!bundle.TrimStart().StartsWith('{')) return Empty;
        using var doc = JsonDocument.Parse(bundle);
        if (!doc.RootElement.TryGetProperty("business", out var data)) return Empty;
        Uri? subscription = null, endpoint = null;
        string? token = null;
        if (data.TryGetProperty("subscription_url", out var source))
            subscription = Https(source.GetString());
        if (data.TryGetProperty("support", out var support))
        {
            endpoint = Https(support.GetProperty("endpoint").GetString());
            token = support.GetProperty("token").GetString();
            if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 ||
                token.Any(c => !(char.IsAsciiLetterOrDigit(c) || "._~+/-=".Contains(c))))
                throw new InvalidDataException("Invalid Business upload identity.");
        }
        return new() { Subscription = subscription, NodesEndpoint = OptionalEndpoint(data, "nodes_url"),
            UpdatesEndpoint = OptionalEndpoint(data, "updates_url"),
            SupportEndpoint = endpoint, SupportToken = token };
    }

    // Invalid optional service URLs disable only their feature, never VPN startup.
    // Subscription caches do not supply these trusted targets.
    private static Uri? OptionalEndpoint(JsonElement data, string name)
    {
        var properties = data.EnumerateObject().Where(p => p.NameEquals(name)).ToArray();
        if (properties.Length != 1 || properties[0].Value.ValueKind != JsonValueKind.String)
            return null;
        var value = properties[0].Value.GetString();
        if (value is not { Length: > 0 and <= 4096 } ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !BusinessNodeSnapshot.TryNormalizeHost(uri.IdnHost, out _))
            return null;
        return uri;
    }

    private static Uri Https(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Invalid Business HTTPS service.");
        return uri;
    }

    internal static string SelectStartupConnection(AppSettings settings, string bundle)
    {
        var source = Parse(bundle).SourceHash;
        if (source != null && source == settings.App.BusinessSubscriptionSource &&
            settings.App.BusinessSubscriptionCache is { } cached)
        {
            try { ValidateResponse(cached); return cached; }
            catch { /* A damaged or obsolete cache never removes the embedded fallback. */ }
        }
        return bundle;
    }

    internal static void ValidateResponse(string response)
    {
        if (Encoding.UTF8.GetByteCount(response) > 1024 * 1024)
            throw new InvalidDataException("Business response too large.");
        var text = response.Trim();
        if (text.StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(text);
            text = doc.RootElement.GetProperty("config").GetString()
                ?? throw new InvalidDataException("Business response missing.");
        }
        try { text = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(text)); }
        catch (FormatException) { }
        var lines = text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var parsed = BusinessDefaults.ParseConnection(response);
        // Reject a partly malformed/truncated list rather than deleting good nodes.
        if (lines.Length != parsed.Count || lines.Any(line => !ServerUriParser.IsSupportedScheme(line)))
            throw new InvalidDataException("Business response is incomplete.");
    }
}
