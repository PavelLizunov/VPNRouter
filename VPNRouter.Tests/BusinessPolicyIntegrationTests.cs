using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class BusinessPolicyIntegrationTests
{
    [Theory]
    [InlineData("split", false)]
    [InlineData("full", false)]
    [InlineData("split", true)]
    [InlineData("full", true)]
    public void Bootstrap_RevalidatesFullReplacementAcrossFileBackedRestarts(string mode, bool empty)
    {
        using var fixture = new Fixture();
        fixture.Accept(7, empty: empty);
        var saved = fixture.Settings.Load();
        saved.App.RoutingMode = mode;
        saved.Vless.ActiveServer = "Germany VLESS";
        fixture.Settings.Save(saved);

        for (var launch = 0; launch < 2; launch++)
        {
            var settings = fixture.Settings.Load();
            using var runtime = fixture.Bootstrap(settings, fixture.Settings);
            Assert.Equal(7, settings.BusinessPolicy!.Revision);
            Assert.Equal(mode, settings.App.RoutingMode);
            Assert.Equal("Germany VLESS", settings.Vless.ActiveServer);
            Assert.DoesNotContain(settings.App.CustomRules, r => r.Value == "dns-shop.ru");
            Assert.DoesNotContain(settings.App.CustomRules, r => r.Value == "youtube.com");
            Assert.Equal(!empty, settings.App.CustomRules.Any(r => r.Value == "example.org" && r.Action == "proxy"));
            Assert.Contains(settings.App.CustomRules, r => r.Value == "uiscom.ru" && r.Action == "direct");
            Assert.Contains(settings.App.CustomRules, r => r.Value == BusinessPolicyDomains.PortalDomain && r.Action == "proxy");
            Assert.Contains("Telegram.exe", settings.App.RoutingAppsInclude);
            Assert.DoesNotContain(fixture.Settings.Load().App.CustomRules, r => r.Value == "dns-shop.ru");
        }
    }

    [AvaloniaFact]
    public void OrdinarySave_CannotRestoreStaleEditorRules_OrMutateCapturedConnection()
    {
        using var fixture = new Fixture();
        fixture.Accept(7);
        using var vm = fixture.ViewModel();
        var captured = vm.CaptureBusinessConnectionSettings();
        Assert.Equal(7, captured.BusinessPolicy!.Revision);
        var saves = fixture.Settings.Saves;

        vm.CustomRulesText = CustomRulesParser.SerializeToText(BusinessBootstrap.ReadRules());
        vm.IsSplitTunnel = false;
        vm.SelectBusinessServer(vm.Servers.Last());

        Assert.True(fixture.Settings.Saves > saves);
        var persisted = fixture.Settings.Load();
        Assert.DoesNotContain(persisted.App.CustomRules, r => r.Value == "dns-shop.ru");
        Assert.Equal("proxy", Assert.Single(persisted.App.CustomRules, r => r.Value == "example.org").Action);
        Assert.Equal("custom_first", persisted.App.CustomRulesPriority);
        Assert.Equal("split", captured.App.RoutingMode);
        Assert.Equal("full", vm.CaptureBusinessConnectionSettings().App.RoutingMode);
        Assert.NotSame(captured.Vless.Servers[0], vm.CaptureBusinessConnectionSettings().Vless.Servers[0]);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceivedWhileStopped_IsPendingWithoutStartingVpn(bool applyNow)
    {
        using var fixture = new Fixture();
        fixture.Accept(7);
        BusinessPolicyCoordinator? coordinator = null;
        using var vm = fixture.ViewModel((settings, store) =>
        {
            using var initial = fixture.Bootstrap(settings, store);
            coordinator = new(fixture.Configuration, fixture.PolicyStore(),
                _ => Task.FromResult(fixture.Envelope(8, applyNow: applyNow, empty: true)));
            coordinator.Load();
            return coordinator;
        });
        var previousConnection = vm.CaptureBusinessConnectionSettings();

        await coordinator!.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(8, vm.AcceptedBusinessPolicyRevision);
        Assert.False(vm.IsConnecting);
        Assert.False(vm.IsConnected);
        Assert.False(vm.IsApplying);
        Assert.Null(vm.AppliedBusinessPolicy);
        Assert.Null(vm.HistoricalBusinessPolicy);
        Assert.Equal(Core.Localization.Strings.BusinessPolicyPending, vm.BusinessPolicyStatusText);
        var nextConnection = vm.CaptureBusinessConnectionSettings();
        Assert.Equal(8, nextConnection.BusinessPolicy!.Revision);
        Assert.DoesNotContain(nextConnection.App.CustomRules, r => r.Value == "example.org");
        Assert.Equal(7, previousConnection.BusinessPolicy!.Revision);
        Assert.Contains(previousConnection.App.CustomRules, r => r.Value == "example.org");
        vm.Dispose();
        Assert.True(coordinator.Completion.IsCompletedSuccessfully);
    }

    [AvaloniaFact]
    public async Task ImmediateRequestCapture_DoesNotAdoptLaterNextConnectionPublication()
    {
        using var fixture = new Fixture();
        fixture.PolicyStore().Accept(fixture.Envelope(7, applyNow: true));
        BusinessPolicyCoordinator? coordinator = null;
        using var vm = fixture.ViewModel((settings, store) =>
        {
            using var initial = fixture.Bootstrap(settings, store);
            coordinator = new(fixture.Configuration, fixture.PolicyStore(),
                _ => Task.FromResult(fixture.Envelope(8, applyNow: false, empty: true)));
            coordinator.Load();
            return coordinator;
        });
        // The immediate request has selected revision 7 when the next poll wins.
        var requested = vm.CaptureBusinessConnectionSettings().BusinessPolicy!;
        Assert.True(requested.ApplyNow);
        await coordinator!.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        var inFlight = vm.CaptureBusinessConnectionSettings(requested);
        var nextConnection = vm.CaptureBusinessConnectionSettings();
        Assert.Equal(7, inFlight.BusinessPolicy!.Revision);
        Assert.Contains(inFlight.App.CustomRules, r => r.Value == "example.org");
        Assert.Equal(8, nextConnection.BusinessPolicy!.Revision);
        Assert.False(nextConnection.BusinessPolicy.ApplyNow);
        Assert.DoesNotContain(nextConnection.App.CustomRules, r => r.Value == "example.org");
    }

    [AvaloniaFact]
    public void CorruptJournal_BlocksNewCaptureWithoutRestoringDeletedSeedSites()
    {
        using var fixture = new Fixture();
        fixture.Accept(7, empty: true);
        using (var runtime = fixture.Bootstrap(fixture.Settings.Load(), fixture.Settings)) { }
        var savedSettings = File.ReadAllBytes(fixture.SettingsPath);
        File.WriteAllBytes(fixture.PolicyStore().JournalPath, [1, 2, 3]);

        Assert.Throws<BusinessPolicyStartupException>(() => fixture.Bootstrap(fixture.Settings.Load(), fixture.Settings));
        Assert.Equal(savedSettings, File.ReadAllBytes(fixture.SettingsPath));
        using var vm = fixture.ViewModel();
        Assert.Throws<BusinessPolicyStartupException>(() => vm.CaptureBusinessConnectionSettings());
        Assert.Equal(Core.Localization.Strings.BusinessPolicyRecoveryRequired, vm.BusinessPolicyStatusText);
        Assert.DoesNotContain(fixture.Settings.Load().App.CustomRules, r => r.Value == "dns-shop.ru");
    }

    [AvaloniaTheory]
    [InlineData(340)]
    [InlineData(420)]
    public void PendingRules_ShowReadableStatusAtNarrowWidths(int width)
    {
        using var fixture = new Fixture();
        fixture.Accept(7);
        using var vm = fixture.ViewModel();
        var window = new BusinessWindow(static (_, _) => Task.FromResult(ServerProbeResult.Unknown))
            { DataContext = vm, Width = width };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Core.Localization.Strings.BusinessPolicyPending, window.Texts.PolicyStatus);
            Assert.True(window.Texts.HasPolicyStatus);
            var scroll = Assert.IsType<ScrollViewer>(window.Content);
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
            ScreenshotHelper.Capture(window, $"business-policy-pending-{width}");
        }
        finally { window.Close(); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "business-policy-integration-" + Guid.NewGuid().ToString("N"));
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string _bundle;
        internal string SettingsPath => Path.Combine(_root, "settings.yaml");
        internal BusinessPolicyConfiguration Configuration { get; }
        internal BoundStore Settings { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(_root, "bundle", "geo"));
            Directory.CreateDirectory(Path.Combine(_root, "geo"));
            File.WriteAllBytes(Path.Combine(_root, "bundle", "geo", "geoip-ru.srs"), RuleSetSamples.IntactEnvelope());
            File.WriteAllBytes(Path.Combine(_root, "bundle", "geo", "geosite-ru.srs"), RuleSetSamples.UpstreamGeositeTldRu());
            _bundle = "{\"config\":" + JsonSerializer.Serialize(Convert.ToBase64String(Encoding.UTF8.GetBytes(BusinessClientConfigurationTests.Three))) +
                ",\"business\":{\"policy\":" + BusinessPolicyConfigurationTests.PolicyJson(_key) + "}}";
            Configuration = BusinessClientConfiguration.Parse(_bundle).Policy!;
            Settings = new(SettingsPath);
            var initial = new AppSettings();
            BusinessDefaults.Apply(initial, _bundle, BusinessBootstrap.ReadRules());
            initial.App.Language = "ru";
            Settings.Save(initial);
        }

        internal BusinessPolicyStore PolicyStore() => new(Configuration, _root);
        internal void Accept(long revision, bool empty = false) => PolicyStore().Accept(Envelope(revision, empty: empty));
        internal byte[] Envelope(long revision, bool applyNow = false, bool empty = false)
        {
            var payload = JsonSerializer.Serialize(new
            {
                schema = 1, policy_id = "fixture-policy", key_id = "fixture-key", revision,
                published_at = 1794657600, apply = applyNow ? "now" : "next_connection",
                rules = empty ? [] : new[] { new { domain = "example.org", action = "proxy", comment = "Managed" } }
            });
            var bytes = Encoding.UTF8.GetBytes(payload);
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                payload = Convert.ToBase64String(bytes),
                signature = Convert.ToBase64String(_key.SignData(bytes, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            });
        }

        internal BusinessPolicyCoordinator? Bootstrap(AppSettings settings, ISettingsStore store) =>
            BusinessBootstrap.Apply(settings, store, _bundle, Path.Combine(_root, "bundle"), Path.Combine(_root, "geo"), SettingsPath);
        internal MainWindowViewModel ViewModel(Func<AppSettings, ISettingsStore, BusinessPolicyCoordinator?>? bootstrap = null) =>
            new(Settings, null, null, null, bootstrap ?? Bootstrap);
        public void Dispose() { _key.Dispose(); Directory.Delete(_root, recursive: true); }
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
