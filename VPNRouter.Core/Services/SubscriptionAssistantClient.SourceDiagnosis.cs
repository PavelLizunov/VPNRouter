using System.Text;
using System.Text.Json;

namespace VPNRouter.Core.Services;

internal sealed record AssistantSourceCitation(string Commit, string Path, int First, int Last);
internal sealed record AssistantSourceDiagnosis(string Hypothesis, string Basis, string Confidence, string Check,
    IReadOnlyList<AssistantSourceCitation> Citations);
internal enum SourceConnectionFailure { StartupTimeout, ReadinessTimeout, UnexpectedStop, TunBusy }
internal enum SourceDiagnosisStatus { Available, UnknownBuild, InsufficientEvidence, AccessDenied, RateLimited, Unsupported, Unavailable }
internal sealed record SourceDiagnosisReply(SourceDiagnosisStatus Status, AssistantSourceDiagnosis? Diagnosis = null);

internal sealed partial class SubscriptionAssistantClient
{
    internal const string SourceEndpoint = "https://ninitux.com/api/v1/assistant/vpnrouter/source-diagnosis";
    private const int SourceResponseLimit = 4096;

    internal async Task<SourceDiagnosisReply> DiagnoseSourceAsync(string subscriptionUrl, AssistantSourceBuild build,
        AssistantCrashEvidence? crash, bool russian, CancellationToken ct, SourceConnectionFailure? connection = null)
    {
        ct.ThrowIfCancellationRequested();
        if (ReadIdentity(subscriptionUrl) is not { } identity) return new(SourceDiagnosisStatus.AccessDenied);

        if ((crash == null) == (connection == null) || (connection is { } kind && !Enum.IsDefined(kind)))
            return new(SourceDiagnosisStatus.InsufficientEvidence);

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 1, Locale = russian ? "ru" : "en", build.Version, build.Commit, build.Platform, build.Arch, Crash = crash, Connection = connection,
        }, StepJson);
        var request = new HttpRequest(HttpMethod.Post, new Uri(SourceEndpoint), new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {identity.Credential}", ["X-Vpnctl-Identity"] = identity.Kind,
        }, body, "application/json", TimeSpan.FromSeconds(30)) { MaxResponseBytes = SourceResponseLimit, ReadErrorBody = false };
        try
        {
            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.FinalUri is { } finalUri && finalUri != request.Uri) return new(SourceDiagnosisStatus.Unavailable);
            return response.StatusCode switch
            {
                200 when response.Body.Length <= SourceResponseLimit => ReadSourceDiagnosis(response.Body, build, identity.Credential),
                401 or 403 => new(SourceDiagnosisStatus.AccessDenied),
                404 => new(SourceDiagnosisStatus.Unsupported),
                429 => new(SourceDiagnosisStatus.RateLimited),
                _ => new(SourceDiagnosisStatus.Unavailable),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(SourceDiagnosisStatus.Unavailable);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException or InvalidDataException or JsonException)
        {
            return new(SourceDiagnosisStatus.Unavailable);
        }
    }

    private static SourceDiagnosisReply ReadSourceDiagnosis(byte[] bytes, AssistantSourceBuild build, string credential)
    {
        var invalid = new SourceDiagnosisReply(SourceDiagnosisStatus.Unavailable);
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (!HasProperties(root, 2) || !root.TryGetProperty("schema", out var schema)
            || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 1
            || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return invalid;
        var operation = ReadString(result, "operation");
        if (HasProperties(result, 1))
            return operation switch
            {
                "unknown_build" => new(SourceDiagnosisStatus.UnknownBuild),
                "insufficient_evidence" => new(SourceDiagnosisStatus.InsufficientEvidence),
                _ => invalid,
            };
        if (operation != "source_diagnosis" || !HasProperties(result, 6)
            || ReadString(result, "hypothesis") is not { } hypothesis || !ValidText(hypothesis)
            || ReadString(result, "basis") is not { } basis || !ValidText(basis)
            || ReadString(result, "confidence") is not ("low" or "medium" or "high")
            || ReadString(result, "check") is not ("view_connection_status" or "review_settings" or "view_last_crash")
            || !result.TryGetProperty("citations", out var array) || array.ValueKind != JsonValueKind.Array
            || array.GetArrayLength() is < 1 or > 3) return invalid;

        var citations = new List<AssistantSourceCitation>();
        foreach (var item in array.EnumerateArray())
        {
            if (!HasProperties(item, 4) || ReadString(item, "commit") != build.Commit
                || ReadString(item, "path") is not { } path || !ValidSourcePath(path)
                || !ReadLine(item, "first", out int first) || !ReadLine(item, "last", out int last)
                || last < first || last - first >= 48) return invalid;
            var citation = new AssistantSourceCitation(build.Commit, path, first, last);
            if (citations.Contains(citation) || ContainsCredential(path)) return invalid;
            citations.Add(citation);
        }
        return new(SourceDiagnosisStatus.Available, new(hypothesis, basis, ReadString(result, "confidence")!,
            ReadString(result, "check")!, citations));

        bool ContainsCredential(string text) => credential.Length >= 8 && text.Contains(credential, StringComparison.Ordinal);
        bool ValidText(string text) => !string.IsNullOrWhiteSpace(text) && Encoding.UTF8.GetByteCount(text) <= 384
            && !text.Any(c => char.IsControl(c) || IsDisallowedGoalCharacter(c) || c is '\u2028' or '\u2029')
            && !ContainsCredential(text);
    }

    private static bool ReadLine(JsonElement item, string key, out int line)
    {
        line = 0;
        return item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out line) && line > 0;
    }

    private static bool ValidSourcePath(string path) =>
        path.Length <= 200
        && (path.StartsWith("VPNRouter.Core/", StringComparison.Ordinal) || path.StartsWith("VPNRouter.App/", StringComparison.Ordinal))
        && path.EndsWith(".cs", StringComparison.Ordinal)
        && path.Split('/').All(part => part is not ("" or "." or "..")
            && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
}
