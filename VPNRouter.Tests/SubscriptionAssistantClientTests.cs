using System.Net.Http;
using System.Text;
using System.Text.Json;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class SubscriptionAssistantClientTests
{
    private const string Device = "0123456789abcdef0123456789abcdef";
    private const string Token = "synthetic-subscription";
    private const string DeviceUrl = "https://ninitux.com/api/v1/app/config/" + Device;
    private const string Endpoint = "https://ninitux.com/api/v1/assistant/vpnrouter/plan";
    private const string Action = """{"schema":1,"result":{"operation":"route_only_app"}}""";

    [Theory]
    [InlineData(DeviceUrl, "device", Device)]
    [InlineData("https://ninitux.com/sub/" + Token + "?format=sing-box", "subscription", Token)]
    [InlineData("https://ninitux.com/api/v1/sub/" + Token, "subscription", Token)]
    public async Task Uses_fixed_endpoint_and_sends_identity_only_in_the_header(
        string url, string kind, string credential)
    {
        var http = new FakeHttpClient().Setup(Endpoint, Action);
        var client = new SubscriptionAssistantClient(http);

        var result = await client.PlanAsync(url, " Chrome через VPN, остальное напрямую ",
            targetAvailable: true, russian: true, TestContext.Current.CancellationToken);

        Assert.Equal(AssistantPlanStatus.RouteOnlyApp, result.Status);
        var request = Assert.Single(http.SentRequests);
        Assert.Equal(Endpoint, request.Uri.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, request.Method);
        var headers = request.Headers!;
        Assert.Equal(kind, headers["X-Vpnctl-Identity"]);
        Assert.Equal("Bearer " + credential, headers["Authorization"]);
        Assert.Equal("application/json", request.BodyContentType);
        Assert.Equal(TimeSpan.FromSeconds(20), request.Timeout);
        Assert.Equal(2048L, request.MaxResponseBytes);
        Assert.Equal(0, request.RetryCount);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(4, body.RootElement.EnumerateObject().Count());
        Assert.Equal(1, body.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal("Chrome через VPN, остальное напрямую", body.RootElement.GetProperty("goal").GetString());
        Assert.Equal("ru", body.RootElement.GetProperty("locale").GetString());
        Assert.True(body.RootElement.GetProperty("target_available").GetBoolean());
        Assert.DoesNotContain(credential, body.RootElement.GetRawText());
    }

    [Fact]
    public async Task A_maximum_length_Cyrillic_goal_fits_the_server_body_limit()
    {
        var http = new FakeHttpClient().Setup(Endpoint, Action);
        var client = new SubscriptionAssistantClient(http);
        var goal = new string('я', 384);
        Assert.Equal(768, Encoding.UTF8.GetByteCount(goal));

        var result = await client.PlanAsync(DeviceUrl, goal, true, true, TestContext.Current.CancellationToken);

        Assert.Equal(AssistantPlanStatus.RouteOnlyApp, result.Status);
        var body = Assert.Single(http.SentRequests).Body!;
        Assert.InRange(body.Length, 768, 2048);
        using var payload = JsonDocument.Parse(body);
        Assert.Equal(goal, payload.RootElement.GetProperty("goal").GetString());
    }

    [Theory]
    [InlineData("http://ninitux.com/sub/token")]
    [InlineData("https://ninitux.com.example/sub/token")]
    [InlineData("https://ninitux.com:8443/sub/token")]
    [InlineData("https://user@ninitux.com/sub/token")]
    [InlineData("https://ninitux.com/sub/token/suffix")]
    [InlineData("https://ninitux.com/api/v1/app/config/012345")]
    public async Task Unrecognized_subscription_never_leaves_the_client(string url)
    {
        var http = new FakeHttpClient();
        var client = new SubscriptionAssistantClient(http);

        var result = await client.PlanAsync(url, "Test", true, false, TestContext.Current.CancellationToken);

        Assert.Equal(AssistantPlanStatus.AccessDenied, result.Status);
        Assert.Empty(http.SentRequests);
    }

    [Theory]
    [InlineData(Action, false)]
    [InlineData("""{"schema":1,"result":{"operation":"route_only_app","target":"wrong"}}""", true)]
    [InlineData("""{"schema":1,"result":{"operation":"run_command"}}""", true)]
    [InlineData("""{"schema":2,"result":{"operation":"route_only_app"}}""", true)]
    [InlineData("""{"schema":"1","result":{"operation":"route_only_app"}}""", true)]
    [InlineData("{", true)]
    public async Task Invalid_reply_never_grants_an_action(string body, bool targetAvailable)
    {
        var http = new FakeHttpClient().Setup(Endpoint, body);
        var client = new SubscriptionAssistantClient(http);

        var result = await client.PlanAsync(DeviceUrl, "Test", targetAvailable, false, TestContext.Current.CancellationToken);

        Assert.Equal(AssistantPlanStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task Pasted_credential_is_rejected_locally()
    {
        var http = new FakeHttpClient();
        var client = new SubscriptionAssistantClient(http);

        var result = await client.PlanAsync(DeviceUrl, "Help with " + Device, true, false, TestContext.Current.CancellationToken);

        Assert.Equal(AssistantPlanStatus.InvalidRequest, result.Status);
        Assert.Empty(http.SentRequests);
    }

    [Theory]
    [InlineData("app_selection", (int)AssistantPlanStatus.AppSelectionNeeded)]
    [InlineData("unsupported_goal", (int)AssistantPlanStatus.UnsupportedGoal)]
    public async Task Clarification_reply_cannot_grant_an_action(string reason, int expected)
    {
        var http = new FakeHttpClient().Setup(Endpoint,
            $$$"""{"schema":1,"result":{"operation":"clarify","reason":"{{{reason}}}"}}""");
        var client = new SubscriptionAssistantClient(http);

        var result = await client.PlanAsync(DeviceUrl, "Test", false, false, TestContext.Current.CancellationToken);

        Assert.Equal((AssistantPlanStatus)expected, result.Status);
    }

    [Theory]
    [InlineData(400, (int)AssistantPlanStatus.InvalidRequest)]
    [InlineData(401, (int)AssistantPlanStatus.AccessDenied)]
    [InlineData(403, (int)AssistantPlanStatus.AccessDenied)]
    [InlineData(413, (int)AssistantPlanStatus.InvalidRequest)]
    [InlineData(429, (int)AssistantPlanStatus.RateLimited)]
    [InlineData(503, (int)AssistantPlanStatus.Unavailable)]
    public async Task Server_errors_never_grant_an_action(int code, int expected)
    {
        var response = new HttpResponse(code, new Dictionary<string, string> { ["Retry-After"] = "20" }, [], TimeSpan.Zero);
        var http = new FakeHttpClient().Setup(Endpoint, response);
        var client = new SubscriptionAssistantClient(http);

        var result = await client.PlanAsync(DeviceUrl, "Test", true, false, TestContext.Current.CancellationToken);

        Assert.Equal((AssistantPlanStatus)expected, result.Status);
        Assert.Equal(code == 429 ? TimeSpan.FromSeconds(20) : (TimeSpan?)null, result.RetryAfter);
    }

    [Fact]
    public async Task An_already_cancelled_request_never_reaches_the_transport()
    {
        var http = new FakeHttpClient();
        var client = new SubscriptionAssistantClient(http);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        abort.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.PlanAsync(DeviceUrl, "Test", true, false, abort.Token));

        Assert.Empty(http.SentRequests);
    }

    [Fact]
    public async Task A_response_from_another_address_never_grants_an_action()
    {
        var response = new HttpResponse(200, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(Action), TimeSpan.Zero)
        {
            FinalUri = new Uri("https://ninitux.com.example/plan"),
        };
        var client = new SubscriptionAssistantClient(new FakeHttpClient().Setup(Endpoint, response));

        var result = await client.PlanAsync(DeviceUrl, "Test", true, false, TestContext.Current.CancellationToken);

        Assert.Equal(AssistantPlanStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task A_transport_timeout_never_grants_an_action()
    {
        var client = new SubscriptionAssistantClient(new FakeHttpClient().ThrowOn(Endpoint, new TimeoutException()));

        var result = await client.PlanAsync(DeviceUrl, "Test", true, false, TestContext.Current.CancellationToken);

        Assert.Equal(AssistantPlanStatus.Unavailable, result.Status);
    }
}
