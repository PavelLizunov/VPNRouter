using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using VPNRouter.App.Services;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class BusinessProvisioningTests
{
    private const string Updates = "https://example.invalid/business/updates.json";

    private static string Bundle(string? updateField = null, string? connection = null,
        string source = "https://example.invalid/private-subscription")
    {
        var result = JsonSerializer.Serialize(new
        {
            config = Convert.ToBase64String(Encoding.UTF8.GetBytes(connection ?? BusinessClientConfigurationTests.Three)),
            business = new
            {
                subscription_url = source,
                nodes_url = "https://example.invalid/trusted-nodes",
                support = new { endpoint = "https://example.invalid/trusted-support", token = "synthetic-identity" }
            }
        });
        return updateField == null ? result : result.Replace("\"business\":{", "\"business\":{" + updateField + ",", StringComparison.Ordinal);
    }

    private static string ConfiguredBundle() => Bundle("\"updates_url\":" + JsonSerializer.Serialize(Updates));

    [Fact]
    public void BridgeThenGenericRestarts_RetainTheFullTrustedBundleAndSourceBoundCache()
    {
        using var fixture = new Fixture();
        var trusted = ConfiguredBundle();
        var expected = BusinessClientConfiguration.Parse(trusted);
        var cached = JsonSerializer.Serialize(new
        {
            config = Convert.ToBase64String(Encoding.UTF8.GetBytes(BusinessClientConfigurationTests.Four)),
            business = new { updates_url = "https://other.invalid/untrusted-update", nodes_url = "https://other.invalid/untrusted-nodes" }
        });
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, BusinessClientConfigurationTests.Four, BusinessBootstrap.ReadRules());
        settings.App.BusinessSubscriptionSource = expected.SourceHash;
        settings.App.BusinessSubscriptionCache = cached;
        settings.App.RoutingMode = "full";
        settings.Vless.ActiveServer = "Finland VLESS";
        RealSettingsStore.Instance.Save(settings, fixture.SettingsPath);

        foreach (var embedded in new string?[] { trusted, null, null })
        {
            var provisioned = BusinessBootstrap.ReadConnection(embedded, fixture.DataDirectory);
            Assert.Equal(trusted, provisioned);
            var cfg = BusinessClientConfiguration.Parse(provisioned);
            Assert.Equal(expected.Subscription, cfg.Subscription);
            Assert.Equal(expected.SourceHash, cfg.SourceHash);
            Assert.Equal(expected.NodesEndpoint, cfg.NodesEndpoint);
            Assert.Equal(expected.SupportEndpoint, cfg.SupportEndpoint);
            Assert.Equal(expected.SupportToken, cfg.SupportToken);
            Assert.Equal(Updates, cfg.UpdatesEndpoint!.AbsoluteUri);
            var loaded = RealSettingsStore.Instance.Load(fixture.SettingsPath);
            BusinessBootstrap.Apply(loaded, RealSettingsStore.Instance, provisioned,
                fixture.BundleDirectory, fixture.GeoDirectory, fixture.SettingsPath);
            var persisted = RealSettingsStore.Instance.Load(fixture.SettingsPath);
            Assert.Equal("full", persisted.App.RoutingMode);
            Assert.Equal("Finland VLESS", persisted.Vless.ActiveServer);
            Assert.Equal(cached, persisted.App.BusinessSubscriptionCache);
            Assert.Equal(expected.SourceHash, persisted.App.BusinessSubscriptionSource);
            Assert.Equal(5, persisted.Vless.Servers.Count);
        }
    }

    [Fact]
    public void ExplicitValidEmbeddedBundle_ReplacesSavedProvisioningBeforeTheNextGenericStart()
    {
        using var fixture = new Fixture();
        var original = ConfiguredBundle();
        var replacement = Bundle("\"updates_url\":\"https://example.invalid/new-updates\"",
            BusinessClientConfigurationTests.Three.Replace("fixture-", "new-account-", StringComparison.Ordinal),
            "https://example.invalid/new-subscription");
        Assert.Equal(original, BusinessBootstrap.ReadConnection(original, fixture.DataDirectory));
        Assert.Equal(replacement, BusinessBootstrap.ReadConnection(replacement, fixture.DataDirectory));
        Assert.Equal(replacement, BusinessBootstrap.ReadConnection(null, fixture.DataDirectory));
        Assert.Equal(replacement, File.ReadAllText(fixture.ProvisioningPath));
    }

    public static IEnumerable<object[]> InvalidBundles()
    {
        yield return ["{"];
        yield return ["{\"config\":null}"];
        yield return [ConfiguredBundle()[..^1]];
        yield return [Bundle(connection: BusinessClientConfigurationTests.Three + "\nmalformed-provider-row")];
        yield return [Bundle(source: "http://example.invalid/unsafe")];
        yield return [ConfiguredBundle().Replace("\"business\":{", "\"business\":{},\"business\":{", StringComparison.Ordinal)];
        yield return [BusinessClientConfigurationTests.Three + new string(' ', BusinessProvisioningStore.MaximumBytes)];
    }

    [Theory]
    [MemberData(nameof(InvalidBundles))]
    public void InvalidEmbeddedBundle_CannotMutateTheSavedProvisioning(string invalid)
    {
        using var fixture = new Fixture();
        var trusted = ConfiguredBundle();
        BusinessBootstrap.ReadConnection(trusted, fixture.DataDirectory);
        var before = File.ReadAllBytes(fixture.ProvisioningPath);

        var failure = Assert.Throws<InvalidDataException>(() => BusinessBootstrap.ReadConnection(invalid, fixture.DataDirectory));

        Assert.Null(failure.InnerException);
        Assert.Equal(before, File.ReadAllBytes(fixture.ProvisioningPath));
        Assert.Equal(trusted, BusinessBootstrap.ReadConnection(null, fixture.DataDirectory));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(fixture.ProvisioningPath)!, "*.tmp"));
    }

    [Fact]
    public void InvalidEmbeddedConnection_IsRejectedBeforeAValidCachedPoolCanMutateSettings()
    {
        using var fixture = new Fixture();
        var bundle = ConfiguredBundle();
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, BusinessClientConfigurationTests.Four, BusinessBootstrap.ReadRules());
        settings.App.BusinessSubscriptionSource = BusinessClientConfiguration.Parse(bundle).SourceHash;
        settings.App.BusinessSubscriptionCache = BusinessClientConfigurationTests.Four;
        settings.App.RoutingMode = "full";
        var previousPool = settings.Vless;
        var invalid = Bundle(connection: BusinessClientConfigurationTests.Three + "\nmalformed-provider-row");

        Assert.Throws<InvalidDataException>(() => BusinessBootstrap.Apply(settings, RealSettingsStore.Instance,
            invalid, fixture.BundleDirectory, fixture.GeoDirectory, fixture.SettingsPath));

        Assert.Same(previousPool, settings.Vless);
        Assert.Equal("full", settings.App.RoutingMode);
        Assert.False(File.Exists(fixture.SettingsPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericStart_RejectsMalformedOrOversizedSavedFilesWithoutRewritingThem(bool oversized)
    {
        using var fixture = new Fixture();
        BusinessBootstrap.ReadConnection(ConfiguredBundle(), fixture.DataDirectory);
        var bytes = oversized ? new byte[BusinessProvisioningStore.MaximumBytes + 1] : Encoding.UTF8.GetBytes("{\"config\":");
        File.WriteAllBytes(fixture.ProvisioningPath, bytes);

        Assert.Throws<InvalidDataException>(() => BusinessBootstrap.ReadConnection(null, fixture.DataDirectory));

        Assert.Equal(bytes, File.ReadAllBytes(fixture.ProvisioningPath));
    }

    [Fact]
    public void GenericStart_RequiresSavedProvisioning_AndStrictUtf8RejectsInvalidBytes()
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidDataException>(() => BusinessBootstrap.ReadConnection(null, fixture.DataDirectory));
        using var stream = new MemoryStream(new byte[] { 0xff, 0xfe, 0xff });
        Assert.Throws<InvalidDataException>(() => BusinessProvisioningStore.ReadBounded(stream));
        using var bom = new MemoryStream(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ConfiguredBundle())).ToArray());
        Assert.Equal(ConfiguredBundle(), BusinessProvisioningStore.ReadBounded(bom));
    }

    [Theory]
    [InlineData("\"updates_url\":null")]
    [InlineData("\"updates_url\":false")]
    [InlineData("\"updates_url\":42")]
    [InlineData("\"updates_url\":{}")]
    [InlineData("\"updates_url\":[]")]
    [InlineData("\"updates_url\":\"\"")]
    [InlineData("\"updates_url\":\"relative/update\"")]
    [InlineData("\"updates_url\":\"http://example.invalid/update\"")]
    [InlineData("\"updates_url\":\"https://user:password@example.invalid/update\"")]
    [InlineData("\"updates_url\":\"https://example.invalid/update#fragment\"")]
    [InlineData("\"updates_url\":\"https://bad_host.invalid/update\"")]
    [InlineData("\"updates_url\":\"https://example.invalid/a\",\"updates_url\":\"https://example.invalid/b\"")]
    public void InvalidOptionalUpdateUrl_DisablesOnlyUpdatesAndRoundTripsWithVpnProvisioning(string field)
    {
        using var fixture = new Fixture();
        var bundle = Bundle(field);
        BusinessBootstrap.ReadConnection(bundle, fixture.DataDirectory);
        var saved = BusinessBootstrap.ReadConnection(null, fixture.DataDirectory);
        var cfg = BusinessClientConfiguration.Parse(saved);
        Assert.Null(cfg.UpdatesEndpoint);
        Assert.NotNull(cfg.Subscription);
        Assert.NotNull(cfg.SupportEndpoint);
        Assert.NotNull(cfg.NodesEndpoint);
        Assert.Equal(3, BusinessDefaults.ParseConnection(saved).Count);
    }

    [Fact]
    public void MissingAndOversizedOptionalUpdateUrl_DoNotDisableVpnProvisioning()
    {
        using var fixture = new Fixture();
        Assert.Null(BusinessClientConfiguration.Parse(Bundle()).UpdatesEndpoint);
        var oversized = Bundle("\"updates_url\":" + JsonSerializer.Serialize("https://example.invalid/" + new string('a', 4096)));
        Assert.Null(BusinessClientConfiguration.Parse(BusinessBootstrap.ReadConnection(oversized, fixture.DataDirectory)).UpdatesEndpoint);
        Assert.Equal(3, BusinessDefaults.ParseConnection(oversized).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedSavedFile_IsRejectedForReadingAndReplacement_IncludingDanglingLinks(bool dangling)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic-link fixture requires Unix or a Windows privilege grant.");
        using var fixture = new Fixture();
        var trusted = ConfiguredBundle();
        BusinessBootstrap.ReadConnection(trusted, fixture.DataDirectory);
        var target = Path.Combine(fixture.Root, "outside-provisioning.json");
        if (!dangling) File.WriteAllText(target, trusted);
        File.Delete(fixture.ProvisioningPath);
        File.CreateSymbolicLink(fixture.ProvisioningPath, target);

        Assert.Throws<InvalidDataException>(() => BusinessBootstrap.ReadConnection(null, fixture.DataDirectory));
        Assert.Throws<InvalidDataException>(() => BusinessBootstrap.ReadConnection(trusted, fixture.DataDirectory));

        if (dangling) Assert.False(File.Exists(target));
        else Assert.Equal(trusted, File.ReadAllText(target));
    }

    [Fact]
    public void LinkedProvisioningAncestor_IsRejectedWithoutWritingThroughIt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic-link fixture requires Unix or a Windows privilege grant.");
        using var fixture = new Fixture();
        var linkedData = Path.Combine(fixture.Root, "linked-data");
        Directory.CreateSymbolicLink(linkedData, fixture.DataDirectory);
        Assert.Throws<InvalidDataException>(() => BusinessBootstrap.ReadConnection(ConfiguredBundle(), linkedData));
        Assert.False(File.Exists(fixture.ProvisioningPath));
    }

    [Fact]
    public void SavedProvisioning_IsProtectedOutsideTheAppPayload()
    {
        using var fixture = new Fixture();
        BusinessBootstrap.ReadConnection(ConfiguredBundle(), fixture.DataDirectory);
        Assert.False(fixture.ProvisioningPath.StartsWith(fixture.BundleDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        if (OperatingSystem.IsWindows())
        {
            var acl = new FileInfo(fixture.ProvisioningPath).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);
            using var identity = WindowsIdentity.GetCurrent();
            var allowed = new[] { identity.User!.Value, "S-1-5-18", "S-1-5-32-544" };
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow)
                    Assert.Contains(((SecurityIdentifier)rule.IdentityReference).Value, allowed);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.ProvisioningPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(fixture.ProvisioningPath)!));
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "business-provisioning-" + Guid.NewGuid().ToString("N"));
        internal string DataDirectory => Path.Combine(Root, "data");
        internal string SettingsPath => Path.Combine(DataDirectory, "config.yaml");
        internal string ProvisioningPath => BusinessProvisioningStore.PathFor(DataDirectory);
        internal string BundleDirectory => Path.Combine(Root, "app");
        internal string GeoDirectory => Path.Combine(DataDirectory, "geo");

        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(BundleDirectory, "geo"));
            Directory.CreateDirectory(GeoDirectory);
            File.WriteAllBytes(Path.Combine(BundleDirectory, "geo", "geoip-ru.srs"), RuleSetSamples.IntactEnvelope());
            File.WriteAllBytes(Path.Combine(BundleDirectory, "geo", "geosite-ru.srs"), RuleSetSamples.UpstreamGeositeTldRu());
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
