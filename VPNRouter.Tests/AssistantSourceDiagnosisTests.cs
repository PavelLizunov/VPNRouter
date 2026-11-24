using System.Text.Json;
using System.Text.Json.Nodes;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class AssistantSourceDiagnosisTests
{
    private const string Commit = "1234567890abcdef1234567890abcdef12345678";
    private const string Credential = "source-test-credential";
    private const string Url = "https://ninitux.com/sub/" + Credential;
    private static readonly AssistantSourceBuild Build = new("2.51.1-r1", Commit, "windows", "x64");
    private static readonly AssistantCrashEvidence Evidence = new(true, AssistantCrashFault.Io, AssistantCrashArea.Settings, AssistantCrashSignal.None);
    private static JsonObject ValidResult() => new()
    {
        ["operation"] = "source_diagnosis", ["hypothesis"] = "File read interrupted.",
        ["basis"] = "The settings loader reported an I/O exception.", ["confidence"] = "low",
        ["check"] = "review_settings",
        ["citations"] = new JsonArray(new JsonObject
        {
            ["commit"] = Commit, ["path"] = "VPNRouter.Core/Example.V2/SettingsLoader.cs", ["first"] = 1, ["last"] = 14,
        }),
    };

    [Fact]
    public async Task Explicit_source_request_only_sends_build_and_enumerated_crash_fields()
    {
        var http = Stub(ValidResult());
        var reply = await Request(http);
        var diagnosis = Assert.IsType<AssistantSourceDiagnosis>(reply.Diagnosis);
        Assert.Equal(Commit, Assert.Single(diagnosis.Citations).Commit);
        var request = Assert.Single(http.SentRequests);
        Assert.Equal("Bearer " + Credential, request.Headers!["Authorization"]);
        Assert.Equal("subscription", request.Headers["X-Vpnctl-Identity"]);
        using var json = JsonDocument.Parse(request.Body!);
        var root = json.RootElement;
        Assert.Equal(new[] { "arch", "commit", "crash", "locale", "platform", "schema", "version" },
            root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(Commit, root.GetProperty("commit").GetString());
        Assert.Equal("io", root.GetProperty("crash").GetProperty("fault").GetString());
        Assert.Equal(4, root.GetProperty("crash").EnumerateObject().Count());
        Assert.DoesNotContain(Credential, root.GetRawText());
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("outside")]
    [InlineData("traversal")]
    [InlineData("lines")]
    [InlineData("duplicate")]
    [InlineData("control")]
    [InlineData("credential")]
    [InlineData("unknownCheck")]
    [InlineData("longText")]
    public async Task Rejects_unbound_or_actionable_provider_replies(string defect)
    {
        var result = ValidResult();
        var citation = result["citations"]![0]!;
        switch (defect)
        {
            case "commit": citation["commit"] = new string('a', 40); break;
            case "outside": citation["path"] = "/etc/hosts"; break;
            case "traversal": citation["path"] = "VPNRouter.App/../private.cs"; break;
            case "lines": citation["last"] = 49; break;
            case "duplicate": result["citations"]!.AsArray().Add(citation.DeepClone()); break;
            case "control": result["control"] = "SetVpnConnected"; break;
            case "credential": result["hypothesis"] = Credential; break;
            case "unknownCheck": result["check"] = "open_url"; break;
            case "longText": result["basis"] = new string('я', 193); break;
        }

        var reply = await Request(Stub(result));
        Assert.Equal(SourceDiagnosisStatus.Unavailable, reply.Status);
        Assert.Null(reply.Diagnosis);
    }

    [Fact]
    public async Task Unknown_build_remains_distinct_from_an_old_gateway_or_exhausted_quota()
    {
        var unknown = await Request(Stub(new JsonObject { ["operation"] = "unknown_build" }));
        Assert.Equal(SourceDiagnosisStatus.UnknownBuild, unknown.Status);
        foreach (var (code, status) in new[] { (404, SourceDiagnosisStatus.Unsupported), (429, SourceDiagnosisStatus.RateLimited) })
            Assert.Equal(status, (await Request(new FakeHttpClient().Setup(SubscriptionAssistantClient.SourceEndpoint, "", code))).Status);
    }

    [Fact]
    public void Legacy_reports_and_other_commits_with_the_same_version_keep_their_identity()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(root.CreateSubdirectory("crashes").FullName, "crash-001.txt");
            File.WriteAllText(file, $"VPNRouter crash report\nVersion: {Build.Version}\nFatal: True\n\n──── Exception ────\nSource build: {Build.ReportIdentity}");
            Assert.Null(AssistantCrashEvidence.ReadLatestReport(root.FullName, TestContext.Current.CancellationToken)!.SourceIdentity);
            var previous = Build with { Commit = new string('a', 40) };
            File.WriteAllText(file, $"VPNRouter crash report\nVersion: {previous.Version}\nSource build: {previous.ReportIdentity}\nFatal: True\n");
            var report = AssistantCrashEvidence.ReadLatestReport(root.FullName, TestContext.Current.CancellationToken)!;
            Assert.Equal(previous.ReportIdentity, report.SourceIdentity);
            Assert.NotEqual(Build.ReportIdentity, report.SourceIdentity);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(file)!, "crash-002.txt"), "Partial header");
            Assert.Null(AssistantCrashEvidence.ReadLatestReport(root.FullName, TestContext.Current.CancellationToken));
            Assert.NotNull(AssistantCrashEvidence.ReadLatest(root.FullName));
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static FakeHttpClient Stub(JsonObject result) => new FakeHttpClient().Setup(
        SubscriptionAssistantClient.SourceEndpoint, new JsonObject { ["schema"] = 1, ["result"] = result }.ToJsonString());

    private static Task<SourceDiagnosisReply> Request(FakeHttpClient http) =>
        new SubscriptionAssistantClient(http).DiagnoseSourceAsync(Url, Build, Evidence, false, TestContext.Current.CancellationToken);
}
