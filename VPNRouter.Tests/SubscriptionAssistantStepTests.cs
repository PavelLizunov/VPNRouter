using System.Text.Json;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class SubscriptionAssistantStepTests
{
    private const string Endpoint = "https://ninitux.com/api/v1/assistant/vpnrouter/step";
    private const string Subscription = "https://ninitux.com/sub/synthetic-assistant";
    private static readonly AssistantControl Connect = new(2, "ConnectVpn", "press", null);
    private static readonly AssistantControl Route = new(1, "RouteOnlyApplicationByWindowsExecutableName", "text", null);

    [Theory]
    [InlineData("Enable vpn", """{"operation":"act","id":2}""", 2, null)]
    [InlineData("сделай только Edge браузер через VPN", """{"operation":"act","id":1,"value":"msedge.exe"}""", 1, "msedge.exe")]
    public async Task TheSamePlannerCanSelectDifferentApplicationControls(
        string goal, string decision, int expectedId, string? executable)
    {
        var http = new FakeHttpClient().Setup(Endpoint, $"{{\"schema\":2,\"result\":{decision}}}");
        var client = new SubscriptionAssistantClient(http);
        var step = await client.StepAsync(Subscription, goal, true,
            new(false, false, true, [Route, Connect], null), TestContext.Current.CancellationToken);

        Assert.Equal(AssistantStepStatus.Act, step.Status);
        Assert.Equal(expectedId, step.Control!.Id);
        Assert.Equal(executable, step.Text);
        using var sent = JsonDocument.Parse(Assert.Single(http.SentRequests).Body!);
        Assert.Equal(goal, sent.RootElement.GetProperty("goal").GetString());
        Assert.Equal(2, sent.RootElement.GetProperty("controls").GetArrayLength());
        Assert.DoesNotContain("synthetic-assistant", sent.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("""{"operation":"act","id":99}""")]
    [InlineData("""{"operation":"act","id":2,"value":true}""")]
    [InlineData("""{"operation":"act","id":1,"value":null}""")]
    [InlineData("""{"operation":"act","id":1,"value":"synthetic-assistant"}""")]
    [InlineData("""{"operation":"act","id":1,"value":"msedge.exe","extra":true}""")]
    public async Task ThePlannerCannotUseUnadvertisedOrInvalidActions(string decision)
    {
        var http = new FakeHttpClient().Setup(Endpoint, $"{{\"schema\":2,\"result\":{decision}}}");
        var client = new SubscriptionAssistantClient(http);
        var step = await client.StepAsync(Subscription, "Test", false,
            new(false, false, true, [Route, Connect], null), TestContext.Current.CancellationToken);

        Assert.Equal(AssistantStepStatus.Unavailable, step.Status);
    }
}
