using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class AppAutomationDriverTests : IDisposable
{
    public AppAutomationDriverTests()
    {
        AppAutomationDriver.Stop();
        AppAutomationDriver.AutomationPort = null;
        AppAutomationDriver.AutomationToken = null;
    }

    public void Dispose()
    {
        AppAutomationDriver.Stop();
        AppAutomationDriver.AutomationPort = null;
        AppAutomationDriver.AutomationToken = null;
    }

    [Fact]
    public void ParseArgs_ValidPortAndToken_SetsProperties()
    {
        var args = new[] { "--foo", "bar", "--automation-port", "8089", "--automation-token", "tok-12345" };

        AppAutomationDriver.ParseArgs(args);

        Assert.Equal(8089, AppAutomationDriver.AutomationPort);
        Assert.Equal("tok-12345", AppAutomationDriver.AutomationToken);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("70000")]
    [InlineData("not-a-number")]
    public void ParseArgs_InvalidPort_DoesNotSetPort(string invalidPort)
    {
        var args = new[] { "--automation-port", invalidPort };

        AppAutomationDriver.ParseArgs(args);

        Assert.Null(AppAutomationDriver.AutomationPort);
    }

    [Fact]
    public void ParseArgs_EnvironmentVariableFallback_SetsPortAndToken()
    {
        var originalPort = Environment.GetEnvironmentVariable("VPNROUTER_AUTOMATION_PORT");
        var originalToken = Environment.GetEnvironmentVariable("VPNROUTER_AUTOMATION_TOKEN");

        try
        {
            Environment.SetEnvironmentVariable("VPNROUTER_AUTOMATION_PORT", "9912");
            Environment.SetEnvironmentVariable("VPNROUTER_AUTOMATION_TOKEN", "env-secret");

            AppAutomationDriver.ParseArgs(Array.Empty<string>());

            Assert.Equal(9912, AppAutomationDriver.AutomationPort);
            Assert.Equal("env-secret", AppAutomationDriver.AutomationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VPNROUTER_AUTOMATION_PORT", originalPort);
            Environment.SetEnvironmentVariable("VPNROUTER_AUTOMATION_TOKEN", originalToken);
        }
    }

    [AvaloniaFact]
    public async Task Endpoints_Metrics_Action_And_Tree_WorkEndToEnd()
    {
        var port = NetPortUtil.FindFreePort();
        const string secret = "test-auth-token-123";

        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        var window = new Window { DataContext = vm, Width = 500, Height = 600 };
        window.Show();

        try
        {
            AppAutomationDriver.Start(window, vm, port, secret);
            Assert.True(AppAutomationDriver.IsRunning);

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var baseUri = $"http://127.0.0.1:{port}";

            // 1. Unauthenticated request should return 401
            var unauthResp = await client.GetAsync($"{baseUri}/metrics");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);

            // 2. Authenticated GET /metrics
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            var metricsResp = await client.GetAsync($"{baseUri}/metrics");
            Assert.Equal(HttpStatusCode.OK, metricsResp.StatusCode);

            var metricsJson = await metricsResp.Content.ReadAsStringAsync();
            using var metricsDoc = JsonDocument.Parse(metricsJson);
            var root = metricsDoc.RootElement;

            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.True(root.GetProperty("system").GetProperty("working_set_mb").GetDouble() > 0);
            Assert.True(root.GetProperty("system").GetProperty("gc_allocated_mb").GetDouble() > 0);
            Assert.False(root.GetProperty("ui").GetProperty("is_connected").GetBoolean());

            // 3. POST /ui/action: switch tab
            var actionPayload = new { action = "switch_tab", value = "2" };
            var actionContent = new StringContent(JsonSerializer.Serialize(actionPayload), Encoding.UTF8, "application/json");
            var actionResp = await client.PostAsync($"{baseUri}/ui/action", actionContent);
            Assert.Equal(HttpStatusCode.OK, actionResp.StatusCode);

            Assert.Equal(2, vm.SelectedTabIndex);

            // 4. GET /ui/tree
            var treeResp = await client.GetAsync($"{baseUri}/ui/tree");
            Assert.Equal(HttpStatusCode.OK, treeResp.StatusCode);

            var treeJson = await treeResp.Content.ReadAsStringAsync();
            using var treeDoc = JsonDocument.Parse(treeJson);
            Assert.True(treeDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.NotNull(treeDoc.RootElement.GetProperty("root"));

            // 5. POST /ui/action: malformed JSON returns 400
            var badContent = new StringContent("not-valid-json", Encoding.UTF8, "application/json");
            var badResp = await client.PostAsync($"{baseUri}/ui/action", badContent);
            Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);

            // 6. GET /ui/screenshot: returns 200 OK + image/png
            var screenResp = await client.GetAsync($"{baseUri}/ui/screenshot");
            Assert.Equal(HttpStatusCode.OK, screenResp.StatusCode);
            Assert.Equal("image/png", screenResp.Content.Headers.ContentType?.MediaType);
            var pngBytes = await screenResp.Content.ReadAsByteArrayAsync();
            Assert.NotEmpty(pngBytes);
        }
        finally
        {
            AppAutomationDriver.Stop();
            window.Close();
        }
    }
}
