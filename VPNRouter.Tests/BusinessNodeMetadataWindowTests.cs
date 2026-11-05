using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using VPNRouter.App.ViewModels.Internals;
using VPNRouter.App.Views;
using VPNRouter.Core;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class BusinessNodeMetadataWindowTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private const string Connection = "vless://fixture-a@NODE.EXAMPLE.INVALID.:443?security=tls#Iceland%20node\n" +
        "vless://fixture-b@[2001:db8::2]:443?security=tls#Reserve\n" +
        "vless://fixture-c@xn--bcher-kva.example:443?security=tls#Germany%20node";

    [AvaloniaFact]
    public void LegacyWindowWithoutMetadataEndpoint_KeepsAdvertisedCountryAndHidesMetrics()
    {
        using var fixture = new Fixture(metadata: false);
        var choice = fixture.Choices[0];
        Assert.False(choice.HasMetadata);
        Assert.Equal("IS", choice.CountryCode);
        Assert.Equal("Iceland", choice.CountryName);
        Assert.NotNull(choice.CountryFlag);
        Assert.Equal(0, fixture.Fetch.Calls);
        Assert.DoesNotContain(fixture.Selector.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == Strings.BusinessNodeMetricsUnavailable && text.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Choice_UsesAuthoritativeCountryAndRealZero_WhileHealthRemainsIndependent()
    {
        var previousLanguage = Strings.Lang;
        Strings.Lang = "en";
        try
        {
            var choice = new BusinessServerChoiceViewModel(new(new VlessServerEntry
                { Name = "Iceland node", Server = "node.example.invalid", Port = 443 }));
            choice.Update(new(ServerProbeStatus.Ok, 12, null));
            var snapshot = Snapshot(Node("node.example.invalid", "CH", available: 1024, load: 0));
            choice.UpdateMetadata(snapshot, snapshot.Nodes.Values.Single());

            Assert.True(choice.HasMetadata);
            Assert.Equal("CH", choice.CountryCode);
            Assert.Equal("Switzerland", choice.CountryName);
            Assert.NotNull(choice.CountryFlag);
            Assert.Equal("Load 0.00 · RAM 0%", choice.MetricsText);
            Assert.Contains("not CPU percent", choice.MetricsHint);
            Assert.True(choice.IsGood);
            Assert.Equal("12 ms", choice.PingText);

            var stale = Snapshot(Node("node.example.invalid", null, available: 0, load: 0,
                observed: 600, age: 1400, freshness: "stale"));
            choice.UpdateMetadata(stale, stale.Nodes.Values.Single());
            Assert.Equal("", choice.CountryCode);
            Assert.Equal("Country unknown", choice.CountryName);
            Assert.Null(choice.CountryFlag);
            Assert.False(choice.HasCountry);
            Assert.Equal("Load 0.00 · RAM 100% (stale)", choice.MetricsText);
            Assert.Contains("Data is stale", choice.MetricsHint);
            Assert.True(choice.IsGood);

            var unsupported = Snapshot(Node("node.example.invalid", "CA"));
            choice.UpdateMetadata(unsupported, unsupported.Nodes.Values.Single());
            Assert.Equal("CA", choice.CountryCode);
            Assert.Equal("CA", choice.CountryName);
            Assert.True(choice.HasCountry);
            Assert.Null(choice.CountryFlag);

            var future = Snapshot(Node("node.example.invalid", "CH", observed: 2001, age: 0));
            choice.UpdateMetadata(future, future.Nodes.Values.Single());
            Assert.Equal("Load: no data", choice.MetricsText);
            Assert.Contains("Sample age is unknown", choice.MetricsHint);
            Assert.Equal("CH", choice.CountryCode);
            Assert.True(choice.IsGood);
            choice.UpdateMetadata(null, null);
            Assert.True(choice.HasMetadata);
            Assert.Equal("Load: no data", choice.MetricsText);
        }
        finally { Strings.Lang = previousLanguage; }
    }

    [AvaloniaFact]
    public async Task MetadataAndLanguageWhileConnected_PreserveConnectionStateSelectionServicesAndActiveDiagnostics()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Choices[0].HasMetadata);
        Assert.Equal("Load: no data", fixture.Choices[0].MetricsText);
        fixture.Vm.IsConnected = true;
        var selected = fixture.Vm.SelectedServer;
        var pool = fixture.Vm.Servers.ToArray();
        var services = fixture.Services;
        var diagnostic = services.RunDiagnosticsAsync(false);
        var before = fixture.Store.SaveCount;
        var round = fixture.Controller!.RefreshAsync();
        var snapshot = Snapshot(Node("node.example.invalid", "CH", available: 1024, load: 0));
        (await fixture.Fetch.NextAsync()).Result.SetResult(snapshot);
        await round.WaitAsync(Budget);
        Flush(fixture.Window);

        Assert.Equal(before, fixture.Store.SaveCount);
        var choice = Assert.IsType<BusinessServerChoiceViewModel>(fixture.Selector.SelectedItem);
        Assert.Equal("CH", choice.CountryCode);
        Assert.Equal("Load 0.00 · RAM 0%", choice.MetricsText);
        Assert.False(choice.IsGood);
        foreach (var index in new[] { 0, 1 })
        {
            fixture.Window.FindControl<ComboBox>("BusinessLanguageSelector")!.SelectedIndex = index;
            Flush(fixture.Window);
            Assert.Equal(index == 0 ? "Нагрузка 0,00 · RAM 0%" : "Load 0.00 · RAM 0%", choice.MetricsText);
            Assert.Equal(index == 0 ? "Швейцария" : "Switzerland", choice.CountryName);
            Assert.Same(choice, fixture.Selector.SelectedItem);
            Assert.Same(selected, fixture.Vm.SelectedServer);
            Assert.Equal(pool, fixture.Vm.Servers.ToArray());
            Assert.Same(services, fixture.Services);
            Assert.True(fixture.Vm.IsConnected);
            Assert.False(fixture.Vm.IsConnecting);
            Assert.False(fixture.Vm.IsSplitTunnel);
            Assert.True(services.IsDiagnosticBusy);
            Assert.False(fixture.Diagnostics.Token.IsCancellationRequested);
        }

        var afterLanguage = fixture.Store.SaveCount;
        var failed = fixture.Controller.RefreshAsync();
        (await fixture.Fetch.NextAsync()).Result.SetException(new IOException("synthetic service failure"));
        await failed.WaitAsync(Budget);
        Assert.Same(snapshot, fixture.Controller.LastSnapshot);
        Assert.Equal(afterLanguage, fixture.Store.SaveCount);
        Assert.Same(selected, fixture.Vm.SelectedServer);
        fixture.Diagnostics.Result.SetResult("synthetic-report.zip");
        await diagnostic.WaitAsync(Budget);
        Assert.True(services.HasReport);
        Assert.Equal(1, fixture.Diagnostics.Collections);
        Assert.Equal(0, fixture.Diagnostics.Sends);
    }

    [AvaloniaFact]
    public async Task PoolRebuild_ReappliesSameSnapshotByNormalizedHost_WithoutResettingStaleData()
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(Node("node.example.invalid", "CH", observed: 600, age: 1400, freshness: "stale"),
            Node("2001:0db8:0:0:0:0:0:2", "SE"), Node("bücher.example", "NL"));
        var initial = fixture.Controller!.RefreshAsync();
        (await fixture.Fetch.NextAsync()).Result.SetResult(snapshot);
        await initial.WaitAsync(Budget);
        var original = fixture.Choices;
        var received = snapshot.ReceivedTimestamp;
        var services = fixture.Services;
        var updated = Connection.Replace("NODE.EXAMPLE.INVALID.", "node.example.invalid")
            .Replace("Iceland%20node", "Renamed%20node");

        Assert.True(fixture.Vm.ReplaceBusinessServers(updated, new string('a', 64)));
        fixture.RebuildPool!();
        Flush(fixture.Window);
        // Rebuilding legitimately synchronizes/persists the selected row. Only
        // the following metadata request and callback must be write-free.
        var beforeMetadataFailure = fixture.Store.SaveCount;
        var rebuilt = fixture.Choices;
        Assert.NotSame(original[0], rebuilt[0]);
        Assert.Equal(new[] { "CH", "SE", "NL" }, rebuilt.Select(choice => choice.CountryCode));
        Assert.Contains("(stale)", rebuilt[0].MetricsText);
        Assert.Contains("Data is stale", rebuilt[0].MetricsHint);
        Assert.Same(snapshot, fixture.Controller.LastSnapshot);
        Assert.Equal(received, fixture.Controller.LastSnapshot!.ReceivedTimestamp);
        Assert.Same(services, fixture.Services);
        Assert.Equal("Renamed node", fixture.Vm.SelectedServer!.Name);
        Assert.False(fixture.Vm.IsSplitTunnel);

        var refresh = fixture.Controller.RefreshAsync();
        (await fixture.Fetch.NextAsync()).Result.SetException(new IOException("synthetic refresh failure"));
        await refresh.WaitAsync(Budget);
        Assert.Equal(beforeMetadataFailure, fixture.Store.SaveCount);
        Assert.Same(snapshot, fixture.Controller.LastSnapshot);
        Assert.Contains("(stale)", fixture.Choices[0].MetricsText);
    }

    [AvaloniaFact]
    public async Task HideShowWindow_CancelsOldRead_AndReusesOwnerWithoutPublishingLateMetadata()
    {
        using var fixture = new Fixture();
        var controller = fixture.Controller!;
        var oldRound = controller.RefreshAsync();
        var old = await fixture.Fetch.NextAsync();
        fixture.Window.Hide();
        Assert.True(old.Token.IsCancellationRequested);
        fixture.Window.Show();
        Flush(fixture.Window);
        Assert.Equal(1, fixture.Fetch.Calls);
        old.Result.SetResult(Snapshot(Node("node.example.invalid", "SE")));
        await oldRound.WaitAsync(Budget);
        var pending = await fixture.Fetch.NextAsync();
        Assert.Null(controller.LastSnapshot);
        Assert.Equal("IS", fixture.Choices[0].CountryCode);
        var newRound = controller.RefreshAsync();
        pending.Result.SetResult(Snapshot(Node("node.example.invalid", "CH")));
        await newRound.WaitAsync(Budget);
        Assert.Equal("CH", fixture.Choices[0].CountryCode);
        Assert.Equal(1, fixture.FactoryCalls);
        Assert.Equal(0, fixture.Disposals);
    }

    [AvaloniaFact]
    public async Task ContextSwapAndClose_CancelOldResults_ReuseWindowOwner_AndDisposeOnce()
    {
        using var fixture = new Fixture();
        var controller = fixture.Controller!;
        var original = Snapshot(Node("node.example.invalid", "CH"));
        var initial = controller.RefreshAsync();
        (await fixture.Fetch.NextAsync()).Result.SetResult(original);
        await initial.WaitAsync(Budget);
        var oldServices = fixture.Services;
        var oldRound = controller.RefreshAsync();
        var abandoned = await fixture.Fetch.NextAsync();

        fixture.Window.DataContext = null;
        Flush(fixture.Window);
        Assert.True(abandoned.Token.IsCancellationRequested);
        Assert.Null(fixture.Window.FindControl<Border>("BusinessActions")!.DataContext);
        Assert.False(oldServices.CollectCommand.CanExecute(null));
        using var replacement = CreateViewModel(new InMemorySettingsStore(), "en");
        fixture.Window.DataContext = replacement;
        Flush(fixture.Window);
        Assert.Equal(1, fixture.FactoryCalls);
        Assert.Same(original, controller.LastSnapshot);
        Assert.Equal("CH", fixture.Choices[0].CountryCode);

        abandoned.Result.SetResult(Snapshot(Node("node.example.invalid", "SE")));
        await oldRound.WaitAsync(Budget);
        var pending = await fixture.Fetch.NextAsync();
        Assert.Same(original, controller.LastSnapshot);
        Assert.Equal("CH", fixture.Choices[0].CountryCode);
        var active = controller.RefreshAsync();
        var current = Snapshot(Node("node.example.invalid", "NL"));
        pending.Result.SetResult(current);
        await active.WaitAsync(Budget);
        Assert.Equal("NL", fixture.Choices[0].CountryCode);

        var closedRound = controller.RefreshAsync();
        var closed = await fixture.Fetch.NextAsync();
        fixture.Window.Close();
        Assert.True(closed.Token.IsCancellationRequested);
        Assert.Equal(1, fixture.Disposals);
        closed.Result.SetResult(Snapshot(Node("node.example.invalid", "IS")));
        await closedRound.WaitAsync(Budget);
        Assert.Same(current, controller.LastSnapshot);
        Assert.Equal(1, fixture.Disposals);
        Assert.Equal(1, fixture.FactoryCalls);
    }

    [AvaloniaTheory]
    [InlineData(340, "en")]
    [InlineData(420, "en")]
    [InlineData(340, "ru")]
    [InlineData(420, "ru")]
    public async Task NarrowWindow_RendersCountryAndLoadWithoutHorizontalOverflow(int width, string language)
    {
        using var fixture = new Fixture(width: width, language: language);
        var round = fixture.Controller!.RefreshAsync();
        (await fixture.Fetch.NextAsync()).Result.SetResult(Snapshot(Node("node.example.invalid", "CH", available: 1024, load: 0)));
        await round.WaitAsync(Budget);
        Flush(fixture.Window);
        var selected = Assert.IsType<BusinessServerChoiceViewModel>(fixture.Selector.SelectedItem);
        Assert.Contains(fixture.Selector.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == selected.MetricsText && text.IsEffectivelyVisible);
        var scroll = Assert.IsType<ScrollViewer>(fixture.Window.Content);
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
        var send = Assert.Single(fixture.Window.GetVisualDescendants().OfType<Button>()
            .Where(button => AutomationProperties.GetAutomationId(button) == "BusinessDiagnosticsSend"));
        var position = send.TranslatePoint(default, scroll);
        Assert.True(position.HasValue);
        Assert.True(send.Bounds.Width > 0 && send.Bounds.Height > 0);
        Assert.True(position!.Value.X >= -1 && position.Value.X + send.Bounds.Width <= scroll.Viewport.Width + 1);
        Assert.True(position.Value.Y >= -1 && position.Value.Y + send.Bounds.Height <= scroll.Viewport.Height + 1);
        // Capture closes the window; all live-window assertions must precede it.
        ScreenshotHelper.Capture(fixture.Window, $"business-node-metadata-{language}-{width}");
    }

    private static object Node(string host, string? country, long? total = 1024, long? available = 512,
        uint? load = 125, long? observed = 1900, ulong? age = 100, string freshness = "fresh") => new
        { endpoint_host = host, country_iso = country, memory_total_mib = total, memory_available_mib = available,
            load_1min_x100 = load, observed_at = observed, age_seconds = age, freshness };

    private static BusinessNodeSnapshot Snapshot(params object[] rows) => BusinessNodeSnapshot.Parse(JsonSerializer.Serialize(new
        { schema = 1, generated_at = 2000, max_age_seconds = 1320, nodes = rows }));

    private static MainWindowViewModel CreateViewModel(InMemorySettingsStore store, string language)
    {
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, Connection, BusinessBootstrap.ReadRules());
        settings.App.Language = language;
        settings.App.RoutingMode = "full";
        store.Save(settings, AppPaths.ConfigYamlPath);
        return new(store);
    }

    private static void Flush(BusinessWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly InMemorySettingsStore Store = new();
        internal readonly FetchQueue Fetch = new();
        internal readonly FakeDiagnostics Diagnostics = new();
        internal MainWindowViewModel Vm { get; }
        internal BusinessWindow Window { get; }
        internal BusinessNodeMetadataController? Controller { get; private set; }
        internal BusinessServicesViewModel Services { get; private set; } = null!;
        internal Action? RebuildPool { get; private set; }
        internal int FactoryCalls, Disposals;
        internal ComboBox Selector => Window.FindControl<ComboBox>("BusinessServerSelector")!;
        internal BusinessServerChoiceViewModel[] Choices => Selector.Items.Cast<BusinessServerChoiceViewModel>().ToArray();
        private readonly string _previousLanguage = Strings.Lang;
        private readonly HttpClient _http = new(new NoNetwork());

        internal Fixture(bool metadata = true, int width = 420, string language = "en")
        {
            Vm = CreateViewModel(Store, language);
            var configuration = BusinessClientConfiguration.Parse(JsonSerializer.Serialize(new
            {
                config = Convert.ToBase64String(Encoding.UTF8.GetBytes(Connection)),
                business = new { support = new { endpoint = "https://example.invalid/reports", token = "synthetic-fixture" } }
            }));
            Window = new(static (_, _) => Task.FromResult(ServerProbeResult.Unknown),
                (vm, changed) =>
                {
                    RebuildPool = changed;
                    return Services = new(vm, configuration, new(_http), changed, Diagnostics);
                },
                metadata ? changed =>
                {
                    FactoryCalls++;
                    return Controller = new(new("https://example.invalid/node-status/private-fixture"),
                        Fetch.FetchAsync, changed, () => Disposals++);
                } : null)
                { DataContext = Vm, Width = width };
            Window.Show();
            Flush(Window);
        }

        public void Dispose()
        {
            Window.Close();
            Vm.Dispose();
            Fetch.Dispose();
            _http.Dispose();
            Strings.Lang = _previousLanguage;
        }
    }

    private sealed class FetchQueue : IDisposable
    {
        private readonly Channel<Pending> _entered = Channel.CreateUnbounded<Pending>();
        private readonly List<Pending> _all = [];
        internal int Calls { get; private set; }
        internal Task<BusinessNodeSnapshot> FetchAsync(Uri endpoint, CancellationToken token)
        {
            Calls++;
            var pending = new Pending(token);
            _all.Add(pending);
            _entered.Writer.TryWrite(pending);
            return pending.Result.Task;
        }
        internal async Task<Pending> NextAsync() => await _entered.Reader.ReadAsync().AsTask().WaitAsync(Budget);
        public void Dispose() { foreach (var pending in _all) pending.Result.TrySetCanceled(); }
    }

    private sealed class Pending(CancellationToken token)
    {
        internal CancellationToken Token { get; } = token;
        internal TaskCompletionSource<BusinessNodeSnapshot> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeDiagnostics : IBusinessDiagnostics
    {
        internal readonly TaskCompletionSource<string> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token;
        internal int Collections, Sends;
        public Task<string> CollectAsync(CancellationToken token) { Token = token; Collections++; return Result.Task.WaitAsync(token); }
        public Task SendAsync(string path, CancellationToken token) { Sends++; return Task.CompletedTask; }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No network request is allowed in this fixture.");
    }
}
