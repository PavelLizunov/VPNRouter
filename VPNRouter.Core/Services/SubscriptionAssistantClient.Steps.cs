using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VPNRouter.Core.Services;

internal sealed record AssistantControl(int Id, string Name, string Kind, bool? Active);
internal sealed record AssistantObservation(bool Connected, bool Busy, bool Simple,
    IReadOnlyList<AssistantControl> Controls, string? Previous);
internal enum AssistantStepStatus { Act, Finish, Clarify, AccessDenied, RateLimited, InvalidRequest, Unavailable }
internal sealed record AssistantStep(AssistantStepStatus Status, AssistantControl? Control = null,
    string? Text = null, bool? Toggle = null);

internal sealed partial class SubscriptionAssistantClient
{
    private const string StepEndpoint = "https://ninitux.com/api/v1/assistant/vpnrouter/step";
    private static readonly JsonSerializerOptions StepJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal async Task<AssistantStep> StepAsync(string subscriptionUrl, string goal, bool russian,
        AssistantObservation observation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ReadIdentity(subscriptionUrl) is not { } identity)
            return new(AssistantStepStatus.AccessDenied);

        goal = goal.Trim();
        bool ContainsCredential(string text) => identity.Credential.Length >= 8
            && text.Contains(identity.Credential, StringComparison.Ordinal);
        if (Encoding.UTF8.GetByteCount(goal) is < 1 or > 768
            || goal.Any(IsDisallowedGoalCharacter) || ContainsCredential(goal))
            return new(AssistantStepStatus.InvalidRequest);

        var controls = observation.Controls.Where(control => !ContainsCredential(control.Name)).Take(32).ToList();
        var previous = observation.Previous is { } name && !ContainsCredential(name) ? name : null;
        byte[] body;
        while (true)
        {
            body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Schema = 2,
                Locale = russian ? "ru" : "en",
                Goal = goal,
                observation.Connected,
                observation.Busy,
                observation.Simple,
                Controls = controls,
                Previous = previous,
            }, StepJson);
            if (body.Length <= RequestLimit) break;
            if (controls.Count == 0) return new(AssistantStepStatus.InvalidRequest);
            controls.RemoveAt(controls.Count - 1);
        }

        var request = new HttpRequest(HttpMethod.Post, new Uri(StepEndpoint), new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {identity.Credential}",
            ["X-Vpnctl-Identity"] = identity.Kind,
        }, body, "application/json", TimeSpan.FromSeconds(20)) { MaxResponseBytes = ResponseLimit };

        try
        {
            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.FinalUri is { } finalUri && !finalUri.Equals(request.Uri))
                return new(AssistantStepStatus.Unavailable);

            return response.StatusCode switch
            {
                200 => DecodeStep(response.Body, controls, ContainsCredential),
                400 or 413 => new(AssistantStepStatus.InvalidRequest),
                401 or 403 => new(AssistantStepStatus.AccessDenied),
                429 => new(AssistantStepStatus.RateLimited),
                _ => new(AssistantStepStatus.Unavailable),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException or InvalidDataException or JsonException)
        {
            return new(AssistantStepStatus.Unavailable);
        }
    }

    private static AssistantStep DecodeStep(byte[] body, IReadOnlyList<AssistantControl> controls,
        Func<string, bool> containsCredential)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!HasProperties(root, 2)
            || !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out int version) || version != 2
            || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            return new(AssistantStepStatus.Unavailable);

        var operation = ReadString(result, "operation");
        if (HasProperties(result, 1))
            return operation switch
            {
                "finish" => new(AssistantStepStatus.Finish),
                "clarify" => new(AssistantStepStatus.Clarify),
                _ => new(AssistantStepStatus.Unavailable),
            };

        if (operation != "act" || !result.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int number)
            || controls.FirstOrDefault(control => control.Id == number) is not { } target)
            return new(AssistantStepStatus.Unavailable);

        if (target.Kind is "press" or "select" or "expand" && HasProperties(result, 2))
            return new(AssistantStepStatus.Act, target);

        if (!HasProperties(result, 3) || !result.TryGetProperty("value", out var value))
            return new(AssistantStepStatus.Unavailable);
        if (target.Kind == "toggle" && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return new(AssistantStepStatus.Act, target, Toggle: value.GetBoolean());
        if (target.Kind == "text" && value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            && Encoding.UTF8.GetByteCount(text) <= 384 && !text.Any(c => char.IsControl(c) || IsDisallowedGoalCharacter(c))
            && !containsCredential(text))
            return new(AssistantStepStatus.Act, target, Text: text);
        return new(AssistantStepStatus.Unavailable);
    }
}
