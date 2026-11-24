using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VPNRouter.Core.Services;

internal enum AssistantPlanStatus
{
    RouteOnlyApp,
    AppSelectionNeeded,
    UnsupportedGoal,
    AccessDenied,
    RateLimited,
    InvalidRequest,
    Unavailable
}

internal sealed record AssistantPlan(AssistantPlanStatus Status, TimeSpan? RetryAfter = null);

internal sealed partial class SubscriptionAssistantClient(IHttpClient http)
{
    private readonly IHttpClient _http = http;
    internal const string Endpoint = "https://ninitux.com/api/v1/assistant/vpnrouter/plan";
    private const int RequestLimit = 2048;
    private const int ResponseLimit = 2048;

    public static SubscriptionAssistantClient Shared { get; } = new(new PolicyHttpClient(
        new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })));

    public static bool IsEligible(string url) => ReadIdentity(url) != null;

    public async Task<AssistantPlan> PlanAsync(
        string subscriptionUrl, string goal, bool targetAvailable, bool russian, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var identity = ReadIdentity(subscriptionUrl);
        if (identity is not { } subscriber)
            return new(AssistantPlanStatus.AccessDenied);

        var trimmedGoal = goal.Trim();
        if (Encoding.UTF8.GetByteCount(trimmedGoal) is < 1 or > 768
            || goal.Any(IsDisallowedGoalCharacter)
            || (subscriber.Credential.Length >= 8
                && goal.Contains(subscriber.Credential, StringComparison.Ordinal)))
            return new(AssistantPlanStatus.InvalidRequest);

        var body = WriteRequest(trimmedGoal, targetAvailable, russian);
        if (body.Length > RequestLimit)
            return new(AssistantPlanStatus.InvalidRequest);

        var request = new HttpRequest(HttpMethod.Post, new Uri(Endpoint),
            new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {subscriber.Credential}",
                ["X-Vpnctl-Identity"] = subscriber.Kind,
            },
            body, "application/json", TimeSpan.FromSeconds(20))
        {
            MaxResponseBytes = ResponseLimit, ReadErrorBody = false,
        };

        try
        {
            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.FinalUri is { } finalUri && !finalUri.Equals(request.Uri))
                return new(AssistantPlanStatus.Unavailable);

            return response.StatusCode switch
            {
                200 => Decode(response.Body, targetAvailable),
                400 or 413 => new(AssistantPlanStatus.InvalidRequest),
                401 or 403 => new(AssistantPlanStatus.AccessDenied),
                429 => new(AssistantPlanStatus.RateLimited, ReadRetryAfter(response)),
                _ => new(AssistantPlanStatus.Unavailable),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException or InvalidDataException or JsonException)
        {
            return new(AssistantPlanStatus.Unavailable);
        }
    }

    private static bool IsDisallowedGoalCharacter(char value) =>
        (char.IsControl(value) && value is not ('\n' or '\t'))
        || value is '\u061c' or '\u200e' or '\u200f' or >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069';

    private static byte[] WriteRequest(string goal, bool targetAvailable, bool russian)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            // The JSON endpoint consumes UTF-8 directly. ASCII escaping can exceed its body limit.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        writer.WriteStartObject();
        writer.WriteNumber("schema", 1);
        writer.WriteString("locale", russian ? "ru" : "en");
        writer.WriteString("goal", goal);
        writer.WriteBoolean("target_available", targetAvailable);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static (string Kind, string Credential)? ReadIdentity(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.IdnHost != "ninitux.com" || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            return null;

        var path = uri.AbsolutePath;
        const string devicePrefix = "/api/v1/app/config/";
        if (path.StartsWith(devicePrefix, StringComparison.Ordinal))
        {
            var device = path[devicePrefix.Length..];
            return device.Length == 32 && device.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
                ? ("device", device) : null;
        }

        foreach (var prefix in new[] { "/api/v1/sub/", "/sub/" })
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var token = path[prefix.Length..];
            return token.Length is >= 1 and <= 256 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                ? ("subscription", token) : null;
        }

        return null;
    }

    private static AssistantPlan Decode(byte[] body, bool targetAvailable)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!HasProperties(root, 2)
            || !root.TryGetProperty("schema", out var schema)
            || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1
            || !root.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object)
            return new(AssistantPlanStatus.Unavailable);

        return ReadString(result, "operation") switch
        {
            "route_only_app" when targetAvailable && HasProperties(result, 1) =>
                new(AssistantPlanStatus.RouteOnlyApp),
            "clarify" when HasProperties(result, 2) => ReadString(result, "reason") switch
            {
                "app_selection" => new(AssistantPlanStatus.AppSelectionNeeded),
                "unsupported_goal" => new(AssistantPlanStatus.UnsupportedGoal),
                _ => new(AssistantPlanStatus.Unavailable),
            },
            _ => new(AssistantPlanStatus.Unavailable),
        };
    }

    private static bool HasProperties(JsonElement value, int count) =>
        value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == count;

    private static string? ReadString(JsonElement value, string key) =>
        value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static TimeSpan? ReadRetryAfter(HttpResponse response)
    {
        return response.Headers.TryGetValue("Retry-After", out var text)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds) : null;
    }
}
