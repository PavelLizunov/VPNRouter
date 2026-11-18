using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public class ServiceSettingsReceiptTests
{
    [Theory]
    [InlineData("127.0.0.1:9090", "127.0.0.1:49301")]
    [InlineData("[::1]:9090", "[::1]:49301")]
    public void ControlConnectionMatchesBothEndpointsAndTheReplacementProcess(string service, string client)
    {
        var serverEndpoint = System.Net.IPEndPoint.Parse(service);
        var clientEndpoint = System.Net.IPEndPoint.Parse(client);
        var table = $"  TCP    {client}    {service}    ESTABLISHED    100\r\n" +
                    $"  TCP    {service}    {client}    ESTABLISHED    42\r\n";
        Assert.True(ServiceTunnelReadiness.OwnsConnection(table, 42, serverEndpoint, clientEndpoint));
        Assert.False(ServiceTunnelReadiness.OwnsConnection(table, 100, serverEndpoint, clientEndpoint));
        Assert.False(ServiceTunnelReadiness.OwnsConnection(table, 42, clientEndpoint, serverEndpoint));
    }

    [Fact]
    public async Task Characterization_WindowsConfirmsAReadyControlConnectionOwnedByItsProcess()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The service verifies TCP ownership on Windows.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(15));
        using var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        var config = System.Text.Json.JsonSerializer.Serialize(new
        {
            experimental = new { clash_api = new { external_controller = server.LocalEndpoint.ToString(), secret = "fixture" } }
        });
        using var handle = new FakeProcessHandle(Environment.ProcessId);
        var verify = ServiceTunnelReadiness.WaitAsync(handle, config, lifetime.Token);
        using var connection = await server.AcceptTcpClientAsync(lifetime.Token);
        using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        Assert.Equal("GET /version HTTP/1.1", await reader.ReadLineAsync(lifetime.Token));
        var headers = new List<string>();
        while (await reader.ReadLineAsync(lifetime.Token) is { Length: > 0 } line) headers.Add(line);
        Assert.Contains("Authorization: Bearer fixture", headers);
        var body = """{"version":"fixture"}""";
        var bytes = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n{body}");
        await stream.WriteAsync(bytes, lifetime.Token);
        Assert.True(await verify);
    }

    [Fact]
    public void OnlyAFreshResultForTheSavedSettingsFromTheSameManagerConfirmsApply()
    {
        var settings = new AppSettings().EnsureSane();
        var previousRevision = SettingsLoader.GetSubmittedRevision(settings);
        var oldId = Guid.NewGuid().ToString("N");
        settings.App.BlockAds = !settings.App.BlockAds;
        var adsRevision = SettingsLoader.GetSubmittedRevision(settings);
        Assert.NotEqual(previousRevision, adsRevision);
        settings.App.BypassRussianTraffic = !settings.App.BypassRussianTraffic;
        var ruRevision = SettingsLoader.GetSubmittedRevision(settings);
        Assert.NotEqual(adsRevision, ruRevision);
        settings.App.ConfigMode = "custom";
        settings.App.ActiveCustomConfig = "test-import";
        var revision = SettingsLoader.GetSubmittedRevision(settings);
        Assert.NotEqual(ruRevision, revision);
        string[] result = [Guid.NewGuid().ToString("N"), "42", revision, "applied"];

        Assert.True(ServiceSettingsReceipt.Match(result, oldId, 42, revision));
        Assert.Null(ServiceSettingsReceipt.Match(result, result[0], 42, revision));
        Assert.Null(ServiceSettingsReceipt.Match(result, oldId, 43, revision));
        Assert.Null(ServiceSettingsReceipt.Match(result, oldId, 42, previousRevision));
        result[3] = "rejected";
        Assert.False(ServiceSettingsReceipt.Match(result, oldId, 42, revision));
    }

    [Fact]
    public void ServiceRestartRetriesTheSameRejectedFileWithCompleteRuleSets()
    {
        const string yaml = "app:\n  block_ads: true";
        var revision = SettingsLoader.GetRevision(yaml);
        string[] receipt = [Guid.NewGuid().ToString("N"), "42", revision, "rejected"];
        Assert.Equal(yaml, ServiceSettingsReceipt.ReadRejectedCandidate(receipt, () => yaml));
        Assert.Null(ServiceSettingsReceipt.ReadRejectedCandidate(receipt, () => "app: {}"));
        receipt[3] = "applied";
        Assert.Null(ServiceSettingsReceipt.ReadRejectedCandidate(receipt, () => yaml));
        var cold = new StartupContext(new AppSettings(), StartupMode.ColdStart);
        Assert.False(cold.ValidateRequiredRuleSets);
        Assert.True((cold with { RequireCompleteRuleSets = true }).ValidateRequiredRuleSets);
    }

    [Fact]
    public void SubmittedRevisionMatchesTheSavedFileReadByTheWatcher()
    {
        var temp = Directory.CreateTempSubdirectory("vpnrouter-service-settings-");
        try
        {
            var path = Path.Combine(temp.FullName, "config.yaml");
            var settings = new AppSettings().EnsureSane();
            settings.App.BlockAds = true;
            SettingsLoader.Save(settings, path);
            var persisted = File.ReadAllText(path);
            Assert.Equal(SettingsLoader.GetSubmittedRevision(settings), SettingsLoader.GetRevision(persisted));
            var candidate = Assert.IsType<AppSettings>(SettingsLoader.ParseLiveCandidate(persisted));
            Assert.True(candidate.App.BlockAds);
        }
        finally { temp.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("app: [")]
    [InlineData("app:\n  config_mode: invalid")]
    public void LiveReloadRejectsInvalidFilesWithoutRecoveringToDefaults(string yaml)
    {
        Assert.Null(SettingsLoader.ParseLiveCandidate(yaml));
    }
}
