using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using VPNRouter.App.ViewModels.Internals;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class BusinessServerSelectionOutboundTests
{
    [AvaloniaTheory]
    [InlineData("split", false, "direct")]
    [InlineData("full", false, "proxy")]
    [InlineData("split", true, "direct")]
    [InlineData("full", true, "proxy")]
    public async Task VerifiedPrimaryAndPortalUseExactProfileWhileKeepingSavedManualChoice(
        string routingMode, bool repair, string finalOutbound)
    {
        if (Environment.GetEnvironmentVariable("VPNROUTER_EXPECT_BUSINESS_POLICY_TESTS") == "1")
            Assert.True(ProductEdition.IsBusiness, "Business CI must exercise the compiled Business generator.");
        Assert.SkipUnless(ProductEdition.IsBusiness, "Requires the compiled Business generator.");

        const string host = "192.0.2.70";
        const int port = 2443;
        const string tlsName = "verified.example.invalid";
        const string manualName = "Saved manual";
        var winnerUuid = Guid.NewGuid().ToString();
        var siblingUuid = Guid.NewGuid().ToString();
        var selectionMode = repair ? BusinessServerSelectionMode.Manual : BusinessServerSelectionMode.Automatic;
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, $"""
            vless://{winnerUuid}@{host}:{port}?security=tls&sni={tlsName}#Verified
            vless://{siblingUuid}@{host}:443?security=tls&flow=xtls-rprx-vision#Flow%20sibling
            vless://{Guid.NewGuid()}@192.0.2.71:443?security=tls#Saved%20manual
            """, BusinessBootstrap.ReadRules());
        settings.App.RoutingMode = routingMode;
        settings.App.BusinessServerSelectionMode = selectionMode;
        settings.App.BypassRussianTraffic = false;
        settings.Vless.ActiveServer = manualName;
        var originalPool = settings.Vless.Servers.Select(SavedProfile).ToArray();

        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = new MainWindowViewModel(store, null, null, null, businessBootstrap: (_, _) => null)
        {
            ExternalTunnelOwned = static () => false,
        };
        var previousSelection = vm.SelectedServer;
        Assert.Equal(manualName, previousSelection?.Name);

        (string? Server, int Port, string? Uuid, string? TlsName) expected = (host, port, winnerUuid, tlsName);
        var verified = new ConcurrentQueue<(string?, int, string?, string?)>();
        vm.BusinessServerVerifier = (entry, _, _) =>
        {
            var attempted = (entry.Server, entry.Port, entry.Uuid, entry.Tls.ServerName);
            if (attempted != expected || !string.IsNullOrEmpty(entry.Flow))
                return Task.FromResult(DeepVerifyResult.Failed("fixture rejected", DeepVerifyFailurePhase.ProxiedHttp));

            verified.Enqueue(attempted);
            return Task.FromResult(new DeepVerifyResult(true, 25, null, null));
        };

        var submissions = new List<AppSettings>();
        vm.BusinessConnectStart = (submission, _, _) =>
        {
            submissions.Add(submission);
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.StartTaskCompleted));
        };

        var savesBeforeStart = store.SaveCount;
        if (repair)
            await vm.ConnectVerifiedBusinessServerAsync(TestContext.Current.CancellationToken);
        else
            await vm.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Equal(expected, Assert.Single(verified));
        var engineSettings = VpnEngine.CopySessionSettings(Assert.Single(submissions));
        using var json = JsonDocument.Parse(ConfigPipeline.Generate(new Profile(), [], engineSettings,
            isServerAlive: _ => true));
        Assert.Equal(finalOutbound, json.RootElement.GetProperty("route").GetProperty("final").GetString());
        var outbounds = json.RootElement.GetProperty("outbounds").EnumerateArray().ToArray();
        foreach (var tag in new[] { "proxy", ConfigGenerator.BusinessPortalOutbound })
        {
            var outbound = Assert.Single(outbounds, item => item.GetProperty("tag").GetString() == tag);
            Assert.Equal("vless", outbound.GetProperty("type").GetString());
            Assert.Equal(expected, (
                outbound.GetProperty("server").GetString(),
                outbound.GetProperty("server_port").GetInt32(),
                outbound.GetProperty("uuid").GetString(),
                outbound.GetProperty("tls").GetProperty("server_name").GetString()));
            Assert.False(outbound.TryGetProperty("flow", out _));
        }
        Assert.DoesNotContain(outbounds, item => item.TryGetProperty("uuid", out var uuid) &&
            uuid.GetString() == siblingUuid);

        Assert.Same(previousSelection, vm.SelectedServer);
        Assert.Equal(savesBeforeStart, store.SaveCount);
        var persisted = store.Load(AppPaths.ConfigYamlPath);
        Assert.Equal(manualName, persisted.Vless.ActiveServer);
        Assert.Equal(selectionMode, persisted.App.BusinessServerSelectionMode);
        Assert.Equal(originalPool, persisted.Vless.Servers.Select(SavedProfile).ToArray());
    }

    private static (string Name, string Server, int Port, string Uuid, string Flow, string TlsName,
        bool SingleOutbound) SavedProfile(VlessServerEntry entry) =>
        (entry.Name, entry.Server, entry.Port, entry.Uuid, entry.Flow, entry.Tls.ServerName, entry.UseAsSingleOutbound);
}
