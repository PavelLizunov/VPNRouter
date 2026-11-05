using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using VPNRouter.App.ViewModels.Internals;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class BusinessProtocolValidationTests
{
    private static string Key(int bytes = 32) => Uri.EscapeDataString(Convert.ToBase64String(
        SHA512.HashData(Encoding.UTF8.GetBytes("synthetic-business-protocol-fixture"))[..bytes]));
    private static string Reality(string query) =>
        "vless://fixture-only@192.0.2.21:443?security=reality" + query + "#Fixture%20Reality";
    private static string BadReality => BusinessClientConfigurationTests.Three.Replace("security=tls", "security=reality");

    public static IEnumerable<object[]> InvalidResponses()
    {
        yield return [BadReality];
        yield return [Reality("&pbk=")];
        yield return [Reality("&pbk=bad-key")];
        yield return [Reality("&pbk=" + Key(31))];
        yield return [Reality("&pbk=" + Key(33))];
        yield return [Reality("&pbk=" + Key() + "&sid=a")];
        yield return [Reality("&pbk=" + Key() + "&sid=zz")];
        yield return [Reality("&pbk=" + Key() + "&sid=0123456789abcdef00")];
        yield return [BusinessClientConfigurationTests.Three.Replace("security=tls", "security=unknown")];
        yield return [BusinessClientConfigurationTests.Three + "\nhy2://%20@192.0.2.22:443#Empty%20password"];
        yield return [BusinessClientConfigurationTests.Three + "\nss://aes-128-gcm:@192.0.2.22:443#Empty%20password"];
        yield return [BusinessClientConfigurationTests.Three + "\nss://2022-blake3-aes-128-gcm:short@192.0.2.22:443#Invalid%20key"];
    }

    [Theory]
    [MemberData(nameof(InvalidResponses))]
    public void Response_RejectsInvalidProtocolFieldsBeforePoolAcceptance(string response) =>
        Assert.Throws<InvalidDataException>(() => BusinessClientConfiguration.ValidateResponse(response));

    [Theory]
    [InlineData("")]
    [InlineData("&sid=0123456789abcdef")]
    public void Response_AcceptsRealityWithAValid32BytePublicKey(string shortId)
    {
        var response = Reality("&pbk=" + Key() + shortId);
        BusinessClientConfiguration.ValidateResponse(response);
        Assert.Single(BusinessDefaults.ParseConnection(response));
    }

    [AvaloniaFact]
    public async Task MalformedRefresh_RetainsVisiblePoolSelectionAndDiskCache_AcrossRealBootstrapRestarts()
    {
        using var fixture = new Fixture();
        fixture.Seed(BusinessClientConfigurationTests.Four);
        using (var vm = new MainWindowViewModel(fixture.Store))
        {
            var beforeRows = vm.Servers.ToArray();
            var beforeSelected = vm.SelectedServer;
            var beforeDisk = File.ReadAllBytes(fixture.SettingsPath);
            var saves = fixture.Store.Saves;
            using var client = new HttpClient(new BusinessClientHttpTests.Handler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BadReality) })));
            using var services = new BusinessServicesViewModel(vm, fixture.Configuration, new(client),
                () => Assert.Fail("Invalid subscription must not replace the pool."));
            services.SetVisible(true);
            await Idle(services);

            Assert.Equal(Strings.BusinessSubscriptionFailed, services.RefreshStatus);
            Assert.Equal(saves, fixture.Store.Saves);
            Assert.Equal(beforeDisk, File.ReadAllBytes(fixture.SettingsPath));
            Assert.Equal(beforeRows, vm.Servers.ToArray());
            Assert.Same(beforeSelected, vm.SelectedServer);
            Assert.False(vm.IsSplitTunnel);
            // Even a caller bypassing the HTTP boundary cannot persist bad entries.
            Assert.Throws<InvalidDataException>(() => vm.ReplaceBusinessServers(BadReality, fixture.Configuration.SourceHash!));
            Assert.Equal(saves, fixture.Store.Saves);
        }

        for (var restart = 0; restart < 2; restart++)
        {
            var restored = fixture.Bootstrap();
            Assert.Equal(4, BusinessDefaults.GetServerChoices(restored.Vless.Servers).Count);
            Assert.Equal("Finland VLESS", restored.Vless.ActiveServer);
            Assert.Equal("full", restored.App.RoutingMode);
            Assert.Equal(BusinessClientConfigurationTests.Four, restored.App.BusinessSubscriptionCache);
        }
    }

    [Fact]
    public void ActualBootstrap_RejectsPreviouslyPersistedMalformedRealityCache_AndUsesEmbeddedFallback()
    {
        using var fixture = new Fixture();
        fixture.Seed(BadReality);
        var restored = fixture.Bootstrap();
        Assert.Equal(3, BusinessDefaults.GetServerChoices(restored.Vless.Servers).Count);
        Assert.Equal("Iceland VLESS", restored.Vless.ActiveServer);
        Assert.Equal("full", restored.App.RoutingMode);
        Assert.All(restored.Vless.Servers, server => Assert.Equal("tls", server.Security));
    }

    private static async Task Idle(BusinessServicesViewModel services)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs e) { if (!services.IsRefreshing) done.TrySetResult(); }
        services.PropertyChanged += Changed;
        try { if (!services.IsRefreshing) done.TrySetResult(); await done.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { services.PropertyChanged -= Changed; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "business-protocol-" + Guid.NewGuid().ToString("N"));
        private readonly string _bundle = BusinessClientConfigurationTests.Bundle();
        internal BusinessClientConfiguration Configuration { get; } = BusinessClientConfiguration.Parse(BusinessClientConfigurationTests.Bundle());
        internal string SettingsPath => Path.Combine(_root, "settings.yaml");
        internal BoundStore Store { get; }

        internal Fixture()
        {
            var geo = Path.Combine(_root, "bundle", "geo");
            Directory.CreateDirectory(geo);
            Directory.CreateDirectory(Path.Combine(_root, "geo"));
            File.WriteAllBytes(Path.Combine(geo, "geoip-ru.srs"), RuleSetSamples.IntactEnvelope());
            File.WriteAllBytes(Path.Combine(geo, "geosite-ru.srs"), RuleSetSamples.UpstreamGeositeTldRu());
            Store = new(SettingsPath);
        }

        internal void Seed(string cache)
        {
            var settings = new AppSettings();
            BusinessDefaults.Apply(settings, BusinessClientConfigurationTests.Four, BusinessBootstrap.ReadRules());
            settings.Vless.ActiveServer = "Finland VLESS";
            settings.App.RoutingMode = "full";
            settings.App.BusinessSubscriptionSource = Configuration.SourceHash;
            settings.App.BusinessSubscriptionCache = cache;
            Store.Save(settings);
        }

        internal AppSettings Bootstrap()
        {
            var loaded = Store.Load();
            BusinessBootstrap.Apply(loaded, Store, _bundle, Path.Combine(_root, "bundle"), Path.Combine(_root, "geo"), SettingsPath);
            return Store.Load();
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class BoundStore(string path) : ISettingsStore
    {
        internal int Saves { get; private set; }
        public AppSettings Load(string? ignored = null) => RealSettingsStore.Instance.Load(path);
        public void Save(AppSettings settings, string? ignored = null) { RealSettingsStore.Instance.Save(settings, path); Saves++; }
        public string? ResetToDefaults(string? ignored = null) => null;
        public string? ConsumeRecoveryNotice() => null;
        public (int Count, string AtUtc) ConsumePlaceholderPruneNotice(AppSettings settings) => (0, "");
        public void StartWatching(string? ignored = null, Action<AppSettings>? onReload = null) { }
        public void StopWatching() { }
    }
}
