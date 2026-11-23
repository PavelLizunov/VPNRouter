using System.Text.Json;
using VPNRouter.App.Services;

namespace VPNRouter.Tests;

public sealed class AssistantApplicationTargetTests
{
    private static readonly IReadOnlyDictionary<string, TargetCase> Cases = ReadCases();

    public static IEnumerable<object[]> CaseNames() => Cases.Select(pair =>
        new object[] { pair.Key, pair.Value.Category });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void User_text_and_local_evidence_bind_the_proposed_application(string id, string category)
    {
        var entry = Cases[id];
        Assert.Equal(category, entry.Category);
        var comparison = entry.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string? FindProcess(string name) => entry.Available.FirstOrDefault(executable => executable.Equals(name, comparison));

        Assert.Equal(entry.Expected, AssistantApplicationTarget.ResolveRequested(entry.Goal, entry.Proposal,
            entry.Windows, FindProcess, entry.DefaultBrowser));

        if (category == "generic-default")
        {
            Assert.Equal(entry.Expected, AssistantApplicationTarget.ResolveRequested(entry.Goal, proposal: null,
                entry.Windows, FindProcess, entry.DefaultBrowser));
            Assert.True(AssistantApplicationTarget.IsDefaultBrowserRequest(entry.Goal));
        }
    }

    [Theory]
    [InlineData("Use VPN only for Acme Editor.", "ACMEWIN.exe", false, "AcmeWin.exe")]
    [InlineData("Только Acme Editor через VPN.", "Acme Editor", false, "AcmeWin.exe")]
    [InlineData("Acme IDE should use VPN.", "Acme IDE", false, "AcmeWin.exe")]
    [InlineData("Use VPN only for Acme Editor.", "other.exe", false, null)]
    [InlineData("Use VPN only for Acme Editor.", "AcmeWin.exe", true, null)]
    [InlineData("Use VPN only for AcmeEditorPro.", "AcmeWin.exe", false, null)]
    [InlineData("Use VPN only for my app.", "AcmeWin.exe", false, null)]
    public void Unknown_names_require_unique_running_application_evidence(
        string goal, string proposal, bool duplicate, string? expected)
    {
        List<AssistantRunningApplication> running = [new("AcmeWin.exe", "Acme Editor", "Acme IDE")];
        if (duplicate) running.Add(new("SecondAcme.exe", "Acme Editor", "Acme IDE"));

        Assert.Equal(expected, AssistantApplicationTarget.ResolveRequested(goal, proposal, windows: true,
            _ => null, readRunningApplications: () => running));
    }

    [Theory]
    [InlineData("Send traffic from my browser over VPN during the trip.", true)]
    [InlineData("Нужен туннель для браузера по умолчанию.", true)]
    [InlineData("Через ВПН только Firefox или Chrome.", true)]
    [InlineData("Please connect the VPN.", false)]
    [InlineData("Open the Applications page.", false)]
    public void App_scoped_requests_constrain_the_available_vpn_actions(string goal, bool expected)
    {
        Assert.Equal(expected, AssistantApplicationTarget.IsAppScopedRoute(goal, hasResolvedTarget: false));
    }

    [Theory]
    [InlineData("Microsoft Edge", true, "MSEdge.exe")]
    [InlineData("Edge Beta", true, null)]
    [InlineData("Firefox", true, null)]
    [InlineData("браузер по умолчанию", true, "firefox.exe")]
    [InlineData("browser", false, null)]
    [InlineData("браузер", false, null)]
    [InlineData("browser.exe", true, "browser.exe")]
    [InlineData("Candidate.exe", true, "Candidate.exe")]
    [InlineData("missing.exe", true, null)]
    [InlineData("Acme Editor", true, "acme.exe")]
    [InlineData("Acme Desktop", true, "acme.exe")]
    [InlineData("Acme", true, null)]
    [InlineData("Shared Product", true, null)]
    [InlineData("swiftclient", false, "swiftclient")]
    [InlineData("SwiftClient", false, null)]
    public void Exact_operand_requires_platform_evidence(string input, bool windows, string? expected)
    {
        string[] available = ["MSEdge.exe", "Candidate.exe", "swiftclient", "browser", "browser.exe"];
        string? Find(string name) => available.FirstOrDefault(x => x.Equals(name,
            windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        AssistantRunningApplication[] running =
        [
            new("acme.exe", "Acme Editor", "Acme Desktop"),
            new("one.exe", "Shared Product", null),
            new("two.exe", "Shared Product", null),
        ];
        Assert.Equal(expected, AssistantApplicationTarget.ResolveExact(input, windows, Find, () => "firefox.exe", () => running));
    }

    private static IReadOnlyDictionary<string, TargetCase> ReadCases()
    {
        using var fixtures = typeof(AssistantApplicationTargetTests).Assembly.GetManifestResourceStream("AssistantAppTargets.jsonl")
            ?? throw new InvalidOperationException("Missing application-target cases.");
        using var reader = new StreamReader(fixtures);
        var cases = new Dictionary<string, TargetCase>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            cases.Add(row.GetProperty("id").GetString()!, new TargetCase(
                row.GetProperty("category").GetString()!,
                row.GetProperty("goal").GetString()!,
                row.GetProperty("proposal").GetString()!,
                row.GetProperty("windows").GetBoolean(),
                row.GetProperty("available").EnumerateArray().Select(item => item.GetString()!).ToArray(),
                row.GetProperty("default").GetString(),
                row.GetProperty("expected").GetString()));
        }
        return cases;
    }

    private sealed record TargetCase(string Category, string Goal, string Proposal, bool Windows,
        string[] Available, string? DefaultBrowser, string? Expected);
}
