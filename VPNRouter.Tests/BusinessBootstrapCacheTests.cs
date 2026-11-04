using VPNRouter.App.Services;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class BusinessBootstrapCacheTests
{
    [Fact]
    public void ActualBootstrap_UsesSourceBoundCacheAcrossRepeatedFileBackedRestarts()
    {
        using var fixture = new BootstrapFixture();
        var bundle = BusinessClientConfigurationTests.Bundle();
        fixture.Seed(BusinessClientConfiguration.Parse(bundle).SourceHash!, BusinessClientConfigurationTests.Four);

        for (var launch = 0; launch < 2; launch++)
        {
            var settings = RealSettingsStore.Instance.Load(fixture.SettingsPath);
            BusinessBootstrap.Apply(settings, RealSettingsStore.Instance, bundle,
                fixture.BundleDirectory, fixture.GeoDirectory, fixture.SettingsPath);

            var persisted = RealSettingsStore.Instance.Load(fixture.SettingsPath);
            Assert.Equal(5, persisted.Vless.Servers.Count);
            Assert.Equal(4, BusinessDefaults.GetServerChoices(persisted.Vless.Servers).Count);
            Assert.Equal("Finland VLESS", persisted.Vless.ActiveServer);
            Assert.Equal("full", persisted.App.RoutingMode);
            Assert.Equal(BusinessClientConfigurationTests.Four, persisted.App.BusinessSubscriptionCache);
            Assert.Equal(BusinessClientConfiguration.Parse(bundle).SourceHash, persisted.App.BusinessSubscriptionSource);
        }

        Assert.Equal(RuleSetSamples.IntactEnvelope(), File.ReadAllBytes(Path.Combine(fixture.GeoDirectory, "geoip-ru.srs")));
        Assert.Equal(RuleSetSamples.UpstreamGeositeTldRu(), File.ReadAllBytes(Path.Combine(fixture.GeoDirectory, "geosite-ru.srs")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActualBootstrap_UsesEmbeddedFallbackForMismatchedOrCorruptCache(bool differentSource)
    {
        using var fixture = new BootstrapFixture();
        var bundle = BusinessClientConfigurationTests.Bundle();
        var source = BusinessClientConfiguration.Parse(differentSource
            ? BusinessClientConfigurationTests.Bundle("https://example.invalid/different-source") : bundle).SourceHash!;
        fixture.Seed(source, differentSource ? BusinessClientConfigurationTests.Four : "damaged-cache");

        var settings = RealSettingsStore.Instance.Load(fixture.SettingsPath);
        BusinessBootstrap.Apply(settings, RealSettingsStore.Instance, bundle,
            fixture.BundleDirectory, fixture.GeoDirectory, fixture.SettingsPath);

        var persisted = RealSettingsStore.Instance.Load(fixture.SettingsPath);
        Assert.Equal(3, persisted.Vless.Servers.Count);
        Assert.Equal(3, BusinessDefaults.GetServerChoices(persisted.Vless.Servers).Count);
        Assert.Equal("Iceland VLESS", persisted.Vless.ActiveServer);
        Assert.Equal("full", persisted.App.RoutingMode);
        Assert.DoesNotContain(persisted.Vless.Servers, server => server.Server == "192.0.2.4");
    }

    private sealed class BootstrapFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "business-bootstrap-cache-" + Guid.NewGuid().ToString("N"));
        internal string BundleDirectory { get; }
        internal string GeoDirectory { get; }
        internal string SettingsPath { get; }

        internal BootstrapFixture()
        {
            BundleDirectory = Path.Combine(_root, "bundle");
            GeoDirectory = Path.Combine(_root, "copied-geo");
            SettingsPath = Path.Combine(_root, "settings.yaml");
            var source = Path.Combine(BundleDirectory, "geo");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(GeoDirectory);
            File.WriteAllBytes(Path.Combine(source, "geoip-ru.srs"), RuleSetSamples.IntactEnvelope());
            File.WriteAllBytes(Path.Combine(source, "geosite-ru.srs"), RuleSetSamples.UpstreamGeositeTldRu());
        }

        internal void Seed(string sourceHash, string response)
        {
            var settings = new AppSettings();
            BusinessDefaults.Apply(settings, BusinessClientConfigurationTests.Four, BusinessBootstrap.ReadRules());
            settings.Vless.ActiveServer = "Finland VLESS";
            settings.App.RoutingMode = "full";
            settings.App.BusinessSubscriptionSource = sourceHash;
            settings.App.BusinessSubscriptionCache = response;
            RealSettingsStore.Instance.Save(settings, SettingsPath);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
