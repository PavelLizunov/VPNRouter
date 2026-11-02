using VPNRouter.App.Services;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

// Executed only by the private packaging job. No secret values go to test output.
public sealed class BusinessPilotValidationTests
{
    [Fact]
    public void EmbeddedConnection_FirstRunPersistsAndGeneratesBothModesWithoutImport()
    {
        var destination = Environment.GetEnvironmentVariable("VPNROUTER_BUSINESS_VALIDATE_DIR");
        var bundle = Environment.GetEnvironmentVariable("VPNROUTER_BUSINESS_PAYLOAD_DIR");
        Assert.SkipUnless(ProductEdition.IsBusiness && !string.IsNullOrEmpty(destination) && !string.IsNullOrEmpty(bundle),
            "Private packaging job only; individual credentials must not be present in public tests.");
        var previousData = AppPaths.DataDir;
        AppPaths.OverrideDataDir(Path.Combine(destination!, "data"));
        try
        {
            AppPaths.EnsureDirectories();
            Assert.False(File.Exists(AppPaths.ConfigYamlPath));
            var settings = RealSettingsStore.Instance.Load(AppPaths.ConfigYamlPath);
            BusinessBootstrap.Apply(settings, RealSettingsStore.Instance, bundle);
            settings = RealSettingsStore.Instance.Load(AppPaths.ConfigYamlPath);
            Assert.NotEmpty(settings.Vless.Servers);
            Assert.Equal(6, settings.App.RoutingAppsInclude.Count);
            Assert.True(GeoDataDownloader.AreGeoFilesAvailable());
            foreach (var mode in new[] { "split", "full" })
            {
                settings.App.RoutingMode = mode;
                var config = ConfigGenerator.Generate(new Profile(), [], settings);
                Assert.Equal(mode == "split" ? "direct" : "proxy", config.Route.Final);
                Assert.Equal("direct", config.Route.Rules.First(r => r.RuleSet != null).Outbound);
                Assert.Contains(config.Dns.Rules, r => r.RuleSet != null && r.Server == "local-dns");
                Assert.Contains(config.Route.Rules, r => r.DomainSuffix?.Contains("uiscom.ru") == true && r.Outbound == "direct");
                AppPaths.WritePrivateText(Path.Combine(destination!, mode + ".json"), ConfigGenerator.Serialize(config));
            }
        }
        finally { AppPaths.OverrideDataDir(previousData); }
    }
}
