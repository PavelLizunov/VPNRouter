using System.Text;
using System.Text.Json;
using VPNRouter.App.Services;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.Diagnostics;

namespace VPNRouter.Tests;

public sealed class BusinessClientConfigurationTests
{
    internal const string Three = "vless://fixture-a@192.0.2.1:443?security=tls#Iceland%20VLESS\n" +
        "vless://fixture-b@192.0.2.2:443?security=tls#Germany%20VLESS\n" +
        "vless://fixture-c@192.0.2.3:443?security=tls#Netherlands%20VLESS";
    internal const string Four = Three + "\nvless://fixture-d@192.0.2.4:443?security=tls#Finland%20VLESS\n" +
        "vless://fixture-udp@192.0.2.4:2443?security=tls#Finland%20UDP";

    internal static string Bundle(string source = "https://example.invalid/private-subscription") =>
        JsonSerializer.Serialize(new { config = Convert.ToBase64String(Encoding.UTF8.GetBytes(Three)),
            business = new { subscription_url = source,
                support = new { endpoint = "https://example.invalid/api/reports", token = "synthetic-upload-identity" } } });

    [Fact]
    public void OldBundle_IsOfflineCompatible_AndNewBundleSeparatesUploadAndSubscription()
    {
        Assert.Null(BusinessClientConfiguration.Parse(Three).Subscription);
        var cfg = BusinessClientConfiguration.Parse(Bundle());
        Assert.Equal("/private-subscription", cfg.Subscription!.AbsolutePath);
        Assert.Equal("/api/reports", cfg.SupportEndpoint!.AbsolutePath);
        Assert.NotNull(cfg.SourceHash);
        Assert.Equal(Three, BusinessClientConfiguration.SelectStartupConnection(new AppSettings(), Three));
    }

    [Theory]
    [InlineData("http://example.invalid/sub")]
    [InlineData("https://user:password@example.invalid/sub")]
    [InlineData("https://example.invalid/sub#fragment")]
    public void PrivateMetadata_RejectsUnsafeServiceTargets(string source) =>
        Assert.Throws<InvalidDataException>(() => BusinessClientConfiguration.Parse(Bundle(source)));

    [Theory]
    [InlineData("")]
    [InlineData("<html>unavailable</html>")]
    [InlineData("{\"status\":\"device_not_registered\",\"config\":null}")]
    [InlineData(Three + "\nmalformed-partial-row")]
    [InlineData(Three + "\nvless://other@192.0.2.9:443?security=tls#iceland%20VLESS")]
    public void IncompleteOrAmbiguousResponses_AreRejected(string response) =>
        Assert.ThrowsAny<Exception>(() => BusinessClientConfiguration.ValidateResponse(response));

    [Fact]
    public void CompleteResponse_RetainsFourthHostAndItsBackingVariants()
    {
        var wrapped = JsonSerializer.Serialize(new { config = Convert.ToBase64String(Encoding.UTF8.GetBytes(Four)) });
        BusinessClientConfiguration.ValidateResponse(wrapped);
        var entries = BusinessDefaults.ParseConnection(wrapped);
        Assert.Equal(5, entries.Count);
        Assert.Equal(4, BusinessDefaults.GetServerChoices(entries).Count);
    }

    [Fact]
    public void RenamedRetainedHost_RemainsSelected_WhileRemovedHostFallsBack()
    {
        var before = BusinessDefaults.CreateServerPool(BusinessDefaults.ParseConnection(Three), null);
        before.ActiveServer = before.Servers[1].Name;
        var renamed = BusinessDefaults.ParseConnection(Four.Replace("Germany%20VLESS", "New%20country%20label"));
        var next = BusinessDefaults.CreateServerPool(renamed, before);
        Assert.Equal("New country label", next.ActiveServer);
        var removed = BusinessDefaults.CreateServerPool(renamed.Where(s => s.Server != "192.0.2.2").ToList(), next);
        Assert.Equal("Iceland VLESS", removed.ActiveServer);
    }

    [Fact]
    public void Restart_UsesOnlyValidCacheBoundToTheEmbeddedSource()
    {
        var bundle = Bundle();
        var settings = new AppSettings();
        settings.App.BusinessSubscriptionSource = BusinessClientConfiguration.Parse(bundle).SourceHash;
        settings.App.BusinessSubscriptionCache = Four;
        Assert.Equal(Four, BusinessClientConfiguration.SelectStartupConnection(settings, bundle));
        Assert.NotEqual(Four, BusinessClientConfiguration.SelectStartupConnection(settings, Bundle("https://example.invalid/other-source")));
        settings.App.BusinessSubscriptionCache = "invalid";
        Assert.Equal(bundle, BusinessClientConfiguration.SelectStartupConnection(settings, bundle));
    }

    [Fact]
    public void DownloadedMetadata_CannotReplaceTheEmbeddedServices_AndCacheIsRedacted()
    {
        var downloaded = JsonSerializer.Serialize(new { config = Convert.ToBase64String(Encoding.UTF8.GetBytes(Four)),
            business = new { subscription_url = "https://other.invalid/steal", support = new { endpoint = "https://other.invalid/upload", token = "not-trusted" } } });
        var settings = new AppSettings();
        settings.App.BusinessSubscriptionSource = BusinessClientConfiguration.Parse(Bundle()).SourceHash;
        settings.App.BusinessSubscriptionCache = downloaded;
        var selected = BusinessClientConfiguration.SelectStartupConnection(settings, Bundle());
        Assert.Equal(5, BusinessDefaults.ParseConnection(selected).Count);
        Assert.Equal("example.invalid", BusinessClientConfiguration.Parse(Bundle()).SupportEndpoint!.Host);
        var redacted = DiagnosticsRedactor.RedactConfigYaml("app:\n  business_subscription_cache: |\n    secret-cached-payload\n");
        Assert.DoesNotContain("secret-cached-payload", redacted);
    }

    [Fact]
    public void Cache_RoundTripsWithTheExistingAtomicSettingsStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "business-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "config.yaml");
            var settings = new AppSettings();
            BusinessDefaults.Apply(settings, Four, BusinessBootstrap.ReadRules());
            settings.App.BusinessSubscriptionSource = BusinessClientConfiguration.Parse(Bundle()).SourceHash;
            settings.App.BusinessSubscriptionCache = Four;
            RealSettingsStore.Instance.Save(settings, path);
            var loaded = RealSettingsStore.Instance.Load(path);
            Assert.Equal(Four, loaded.App.BusinessSubscriptionCache);
            Assert.Equal(Four, BusinessClientConfiguration.SelectStartupConnection(loaded, Bundle()));
        }
        finally { Directory.Delete(root, true); }
    }
}
