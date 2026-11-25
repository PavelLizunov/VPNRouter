using System.Net;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using VPNRouter.App.ViewModels.Internals;
using VPNRouter.Core;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class BusinessServerSelectionSubscriptionTests
{
    private const string NewServer = "Finland VLESS";
    private const string CachedServer = "Germany VLESS";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionWaitsForInFlightRefreshBeforeProbingNewPool(bool repair)
    {
        using var fixture = new Fixture(manual: repair);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var services = fixture.CreateServices(_ => response.Task, NewServer);
        Task connect;
        try
        {
            services.SetVisible(true);
            var refreshToken = await fixture.WaitForFirstRequestAsync();
            Assert.True(services.IsRefreshing);
            Assert.Equal(1, fixture.Requests);

            connect = repair
                ? services.OpenRepairCommand.ExecuteAsync(null)
                : fixture.Vm.ToggleConnectionCommand.ExecuteAsync(null);
            Assert.True(fixture.Vm.IsConnecting);
            Assert.False(connect.IsCompleted);
            Assert.False(refreshToken.IsCancellationRequested);
            Assert.Empty(fixture.Probed);
            Assert.Empty(fixture.Submitted);
            await fixture.Vm.ToggleConnectionCommand.ExecuteAsync(null).WaitAsync(TestTimeout);
            Assert.Equal(1, fixture.Requests);
        }
        finally { response.TrySetResult(Response(BusinessClientConfigurationTests.Four)); }

        await connect.WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Requests);
        Assert.Equal(NewServer, Assert.Single(fixture.Submitted).Vless.ActiveServer);
        var newEntry = Assert.Single(fixture.Probed, entry => entry.Name == NewServer);
        Assert.Equal(("192.0.2.4", 443, "fixture-d"), (newEntry.Server, newEntry.Port, newEntry.Uuid));
        Assert.Equal(BusinessClientConfigurationTests.Four, fixture.Saved.App.BusinessSubscriptionCache);
        Assert.Equal(CachedServer, fixture.Saved.Vless.ActiveServer);
        Assert.Equal(repair ? BusinessServerSelectionMode.Manual : BusinessServerSelectionMode.Automatic,
            fixture.Saved.App.BusinessServerSelectionMode);
    }

    [AvaloniaTheory]
    [InlineData("offline", false)]
    [InlineData("offline", true)]
    [InlineData("http error", false)]
    [InlineData("invalid body", false)]
    public async Task FailedRefreshBeforeConnectUsesPreviouslyAcceptedPool(string failure, bool joinInFlight)
    {
        using var fixture = new Fixture();
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var services = fixture.CreateServices(_ => failure switch
        {
            "offline" when joinInFlight => pending.Task,
            "offline" => Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic offline")),
            "http error" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            _ => Task.FromResult(Response(BusinessClientConfigurationTests.Three + "\ninvalid row")),
        }, CachedServer);
        if (joinInFlight)
        {
            services.SetVisible(true);
            await fixture.WaitForFirstRequestAsync();
        }

        var connect = fixture.Vm.ToggleConnectionCommand.ExecuteAsync(null);
        if (joinInFlight) pending.SetException(new HttpRequestException("synthetic offline"));
        await connect.WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Requests);
        Assert.Equal(0, fixture.PoolChanges);
        Assert.Equal(CachedServer, Assert.Single(fixture.Submitted).Vless.ActiveServer);
        Assert.Contains(fixture.Probed, entry => entry.Name == CachedServer);
        Assert.Equal(BusinessClientConfigurationTests.Three, fixture.Saved.App.BusinessSubscriptionCache);
        Assert.Equal(Strings.BusinessSubscriptionFailed, services.RefreshStatus);
    }

    [AvaloniaFact]
    public async Task ConnectRetriesFailedStartupRefreshOnce()
    {
        using var fixture = new Fixture();
        using var services = fixture.CreateServices(_ => fixture.Requests == 1
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic offline"))
            : Task.FromResult(Response(BusinessClientConfigurationTests.Four)), NewServer);

        services.SetVisible(true);
        await WaitForRefreshAsync(services);
        Assert.Equal(Strings.BusinessSubscriptionFailed, services.RefreshStatus);

        await fixture.Vm.ToggleConnectionCommand.ExecuteAsync(null).WaitAsync(TestTimeout);
        Assert.Equal(2, fixture.Requests);
        Assert.Equal(1, fixture.PoolChanges);
        Assert.Equal(NewServer, Assert.Single(fixture.Submitted).Vless.ActiveServer);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectRefreshesOnlyWhenLastSuccessIsStale(bool stale)
    {
        using var fixture = new Fixture();
        var clock = new ManualClock();
        var winner = stale ? NewServer : CachedServer;
        using var services = fixture.CreateServices(_ => Task.FromResult(Response(fixture.Requests == 1
            ? BusinessClientConfigurationTests.Three : BusinessClientConfigurationTests.Four)), winner,
            timeProvider: clock);
        services.SetVisible(true);
        await WaitForRefreshAsync(services);
        clock.Elapsed = stale ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(59);

        await fixture.Vm.ToggleConnectionCommand.ExecuteAsync(null).WaitAsync(TestTimeout);
        Assert.Equal(stale ? 2 : 1, fixture.Requests);
        Assert.Equal(winner, Assert.Single(fixture.Submitted).Vless.ActiveServer);
    }

    [AvaloniaFact]
    public async Task RepairCancellationStopsItsOwnConnectWhileVisibleRefreshFinishes()
    {
        using var fixture = new Fixture(manual: true);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var services = fixture.CreateServices(_ => response.Task, NewServer);
        services.SetVisible(true);
        try
        {
            await fixture.WaitForFirstRequestAsync();
            var repair = services.OpenRepairCommand.ExecuteAsync(null);
            Assert.True(fixture.Vm.IsConnecting);
            services.CloseRepairCommand.Execute(null);
            await repair.WaitAsync(TestTimeout);
            Assert.False(fixture.Vm.IsConnecting);
            Assert.True(services.IsRefreshing);
            Assert.Empty(fixture.Probed);
            Assert.Empty(fixture.Submitted);
        }
        finally { response.TrySetResult(Response(BusinessClientConfigurationTests.Four)); }

        await WaitForRefreshAsync(services);
        Assert.Equal(1, fixture.Requests);
        Assert.Equal(1, fixture.PoolChanges);
        Assert.Empty(fixture.Submitted);
    }

    [AvaloniaFact]
    public async Task ExternalTunnelAppearingDuringRefreshBlocksPoolReplacementAndStart()
    {
        using var fixture = new Fixture();
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var services = fixture.CreateServices(_ => response.Task, NewServer);
        var connect = fixture.Vm.ToggleConnectionCommand.ExecuteAsync(null);
        try
        {
            await fixture.WaitForFirstRequestAsync();
            fixture.Vm.ExternalTunnelOwned = static () => true;
            fixture.Vm.AdoptExternalTunnelForTest = static () => false;
        }
        finally { response.TrySetResult(Response(BusinessClientConfigurationTests.Four)); }

        await connect.WaitAsync(TestTimeout);
        Assert.Equal(1, fixture.Requests);
        Assert.Equal(0, fixture.PoolChanges);
        Assert.Empty(fixture.Probed);
        Assert.Empty(fixture.Submitted);
        Assert.Equal(BusinessClientConfigurationTests.Three, fixture.Saved.App.BusinessSubscriptionCache);
    }

    [AvaloniaFact]
    public async Task LocalProfileStartsImmediatelyWithoutSubscriptionUrl()
    {
        using var fixture = new Fixture();
        using var services = fixture.CreateServices(_ => throw new InvalidOperationException("Unexpected HTTP request."),
            CachedServer, BusinessClientConfiguration.Empty);
        services.SetVisible(true);

        await fixture.Vm.ToggleConnectionCommand.ExecuteAsync(null).WaitAsync(TestTimeout);
        Assert.Equal(0, fixture.Requests);
        Assert.Equal(CachedServer, Assert.Single(fixture.Submitted).Vless.ActiveServer);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void RemovedDnsttRetainsManualChoiceButAllowsAutomaticPoolRotation(bool manual)
    {
        using var fixture = new Fixture(manual, savedDnstt: true);
        var previous = fixture.Vm.SelectedServer;
        if (manual)
        {
            Assert.Throws<InvalidDataException>(() => fixture.Vm.ReplaceBusinessServers(
                BusinessClientConfigurationTests.Four, fixture.SourceHash));
            Assert.Same(previous, fixture.Vm.SelectedServer);
            Assert.Equal(BusinessClientConfigurationTests.Three, fixture.Saved.App.BusinessSubscriptionCache);
        }
        else
        {
            Assert.True(fixture.Vm.ReplaceBusinessServers(BusinessClientConfigurationTests.Four, fixture.SourceHash));
            Assert.Equal("Iceland VLESS", fixture.Vm.SelectedServer?.Name);
            Assert.Equal("Iceland VLESS", fixture.Saved.Vless.ActiveServer);
            Assert.Equal(BusinessClientConfigurationTests.Four, fixture.Saved.App.BusinessSubscriptionCache);
        }
    }

    private static HttpResponseMessage Response(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static async Task WaitForRefreshAsync(BusinessServicesViewModel services)
    {
        if (!services.IsRefreshing) return;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (!services.IsRefreshing) done.TrySetResult();
        }

        services.PropertyChanged += Changed;
        try { await done.Task.WaitAsync(TestTimeout); }
        finally { services.PropertyChanged -= Changed; }
    }

    private sealed class ManualClock : TimeProvider
    {
        internal TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly InMemorySettingsStore _store = new();
        private readonly BusinessClientConfiguration _configuration =
            BusinessClientConfiguration.Parse(BusinessClientConfigurationTests.Bundle());
        private readonly TaskCompletionSource<CancellationToken> _firstRequest =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private HttpClient? _client;
        private int _requests;

        internal MainWindowViewModel Vm { get; }
        internal AppSettings Saved => _store.Load(AppPaths.ConfigYamlPath);
        internal string SourceHash => _configuration.SourceHash!;
        internal int Requests => Volatile.Read(ref _requests);
        internal int PoolChanges { get; private set; }
        internal List<VlessServerEntry> Probed { get; } = [];
        internal List<AppSettings> Submitted { get; } = [];

        internal Fixture(bool manual = false, bool savedDnstt = false)
        {
            var settings = new AppSettings();
            BusinessDefaults.Apply(settings, BusinessClientConfigurationTests.Three, BusinessBootstrap.ReadRules());
            settings.App.BusinessServerSelectionMode = manual
                ? BusinessServerSelectionMode.Manual : BusinessServerSelectionMode.Automatic;
            settings.App.BusinessSubscriptionSource = SourceHash;
            settings.App.BusinessSubscriptionCache = BusinessClientConfigurationTests.Three;
            settings.Vless.ActiveServer = CachedServer;
            if (savedDnstt)
            {
                settings.Vless.Servers.Add(new VlessServerEntry
                {
                    Name = "Previous DNSTT",
                    Protocol = "dnstt",
                    Server = "192.0.2.50",
                    Dnstt = new DnsttProfile { Domain = "tunnel.example.invalid" },
                });
                settings.Vless.ActiveServer = "Previous DNSTT";
            }
            _store.Save(settings, AppPaths.ConfigYamlPath);
            Vm = new MainWindowViewModel(_store, null, null, null, businessBootstrap: (_, _) => null)
            {
                ExternalTunnelOwned = static () => false,
                BusinessConnectStart = (settings, _, _) =>
                {
                    Submitted.Add(settings);
                    return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.StartTaskCompleted));
                },
            };
        }

        internal BusinessServicesViewModel CreateServices(
            Func<CancellationToken, Task<HttpResponseMessage>> fetch, string winner,
            BusinessClientConfiguration? configuration = null, TimeProvider? timeProvider = null)
        {
            var config = configuration ?? _configuration;
            _client = new HttpClient(new BusinessClientHttpTests.Handler((request, ct) =>
            {
                Interlocked.Increment(ref _requests);
                Assert.Equal(config.Subscription, request.RequestUri);
                _firstRequest.TrySetResult(ct);
                return fetch(ct);
            }));
            Vm.BusinessServerVerifier = (entry, _, _) =>
            {
                Probed.Add(entry);
                return Task.FromResult(entry.Name == winner
                    ? new DeepVerifyResult(true, 25, null, null)
                    : DeepVerifyResult.Failed("fixture rejected", DeepVerifyFailurePhase.ProxiedHttp));
            };
            var http = new BusinessClientHttp(_client, subscriptionRuntime: static () => (false, null));
            return new BusinessServicesViewModel(Vm, config, http, () => PoolChanges++, timeProvider: timeProvider);
        }

        internal Task<CancellationToken> WaitForFirstRequestAsync() => _firstRequest.Task.WaitAsync(TestTimeout);

        public void Dispose()
        {
            _client?.Dispose();
            Vm.Dispose();
        }
    }
}
