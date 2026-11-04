using System.ComponentModel;
using System.Net;
using System.Net.Http;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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

public sealed class BusinessLiveServicesTests
{
    [AvaloniaFact]
    public void Refresh_AddsFourthNode_PreservesVariantsModeChoice_AndPersistsPool()
    {
        using var vm = CreateVm(out var store);
        vm.SelectBusinessServer(vm.Servers[1]);
        vm.IsSplitTunnel = false;
        var source = BusinessClientConfiguration.Parse(BusinessClientConfigurationTests.Bundle()).SourceHash!;
        Assert.True(vm.ReplaceBusinessServers(BusinessClientConfigurationTests.Four, source));
        Assert.Equal(5, vm.Servers.Count);
        Assert.Equal(4, vm.GetBusinessServerChoices().Count);
        Assert.Equal("192.0.2.2", vm.SelectedServer!.Server);
        Assert.False(vm.IsSplitTunnel);
        var persisted = store.Load(AppPaths.ConfigYamlPath);
        var startup = BusinessClientConfiguration.SelectStartupConnection(persisted, BusinessClientConfigurationTests.Bundle());
        BusinessDefaults.Apply(persisted, startup, BusinessBootstrap.ReadRules());
        using var restarted = new MainWindowViewModel(store);
        Assert.Equal(4, restarted.GetBusinessServerChoices().Count);
        Assert.Equal("192.0.2.2", restarted.SelectedServer!.Server);
        Assert.False(restarted.IsSplitTunnel);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Refresh_RefusesMutationWhileConnectingOrConnected(bool connecting)
    {
        using var vm = CreateVm(out var store);
        var before = store.SaveCount;
        vm.IsConnecting = connecting; vm.IsConnected = !connecting;
        Assert.False(vm.ReplaceBusinessServers(BusinessClientConfigurationTests.Four, "source"));
        Assert.Equal(3, vm.Servers.Count);
        Assert.Equal(before, store.SaveCount);
        vm.IsConnected = vm.IsConnecting = false;
    }

    [AvaloniaFact]
    public void FailedSettingsWrite_RollsBackPoolAndCacheBeforeChangingVisibleRows()
    {
        var store = new FailingStore();
        BusinessDefaults.Apply(store.Settings, BusinessClientConfigurationTests.Three, BusinessBootstrap.ReadRules());
        using var vm = new MainWindowViewModel(store);
        var old = vm.SelectedServer;
        store.FailSave = true;
        Assert.Throws<IOException>(() => vm.ReplaceBusinessServers(BusinessClientConfigurationTests.Four, "source"));
        Assert.Same(old, vm.SelectedServer);
        Assert.Equal(3, vm.Servers.Count);
        Assert.Equal(3, store.Settings.Vless.Servers.Count);
        Assert.Null(store.Settings.App.BusinessSubscriptionCache);
        Assert.Null(store.Settings.App.BusinessSubscriptionSource);
    }

    [AvaloniaFact]
    public async Task Startup_RefreshesOnlyVisibleIdleClient_WithoutCollectingOrUploading()
    {
        using var vm = CreateVm(out _);
        var requests = 0; var changed = 0;
        var diagnostics = new FakeDiagnostics();
        using var client = Client((_, _) => { requests++; return Task.FromResult(Response(BusinessClientConfigurationTests.Four)); });
        using var services = new BusinessServicesViewModel(vm, Configuration(), new(client), () => changed++, diagnostics);
        Assert.Equal(0, requests);
        services.SetVisible(true);
        await Idle(services);
        Assert.Equal(1, requests); Assert.Equal(1, changed);
        Assert.Equal(4, vm.GetBusinessServerChoices().Count);
        Assert.Equal(0, diagnostics.Collections); Assert.Equal(0, diagnostics.Sends);
        vm.IsConnected = true;
        Assert.False(services.RefreshListCommand.CanExecute(null));
        await services.RefreshListAsync(); Assert.Equal(1, requests);
        vm.IsConnected = false;
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HiddenOrConnecting_CancelsRefresh_AndLateResultCannotReplaceWorkingList(bool hide)
    {
        using var vm = CreateVm(out var store);
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        using var client = Client((_, ct) => { observed = ct; return gate.Task; });
        var changes = 0;
        using var services = new BusinessServicesViewModel(vm, Configuration(), new(client), () => changes++, new FakeDiagnostics());
        services.SetVisible(true);
        Assert.True(services.IsRefreshing);
        var before = store.SaveCount;
        if (hide) services.SetVisible(false); else vm.IsConnecting = true;
        Assert.True(observed.IsCancellationRequested);
        gate.SetResult(Response(BusinessClientConfigurationTests.Four));
        await Idle(services);
        Assert.Equal(0, changes); Assert.Equal(3, vm.Servers.Count);
        Assert.Equal(before, store.SaveCount);
        Assert.Null(store.Load(AppPaths.ConfigYamlPath).App.BusinessSubscriptionCache);
        vm.IsConnecting = false;
    }

    [AvaloniaFact]
    public async Task FailedRefresh_RetainsPoolAndShowsGenericFailureWithoutPrivateDetails()
    {
        using var vm = CreateVm(out var store);
        var selected = vm.SelectedServer; var saves = store.SaveCount;
        using var client = Client((_, _) => Task.FromException<HttpResponseMessage>(new IOException("private URL and identity")));
        using var services = new BusinessServicesViewModel(vm, Configuration(), new(client), () => Assert.Fail("Must not apply failed response"), new FakeDiagnostics());
        services.SetVisible(true); await Idle(services);
        Assert.Same(selected, vm.SelectedServer); Assert.Equal(saves, store.SaveCount);
        Assert.Equal(Strings.BusinessSubscriptionFailed, services.RefreshStatus);
        Assert.DoesNotContain("private URL", services.RefreshStatus);
    }

    [AvaloniaFact]
    public async Task DiagnosticSend_FailureThenRetry_ReusesArchive_AndRequiresAcknowledgement()
    {
        using var vm = CreateVm(out _);
        using var client = Client((_, _) => throw new InvalidOperationException("No subscription request expected"));
        var fake = new FakeDiagnostics { FailSend = true };
        using var services = new BusinessServicesViewModel(vm, Configuration(), new(client), () => { }, fake);
        Assert.Equal(0, fake.Collections); Assert.Equal(0, fake.Sends);
        await services.RunDiagnosticsAsync(true);
        Assert.Equal(1, fake.Collections); Assert.Equal(1, fake.Sends);
        Assert.Equal(Strings.BusinessDiagnosticsSendFailed, services.DiagnosticStatus);
        Assert.True(services.HasReport);
        fake.FailSend = false;
        await services.RunDiagnosticsAsync(true);
        Assert.Equal(1, fake.Collections); Assert.Equal(2, fake.Sends);
        Assert.Equal(Strings.BusinessDiagnosticsDelivered, services.DiagnosticStatus);
        await services.RunDiagnosticsAsync(true);
        Assert.Equal(2, fake.Collections); Assert.Equal(3, fake.Sends);
    }

    [AvaloniaFact]
    public async Task DiagnosticCollection_DoesNotSend_AndCancellationDoesNotClaimDelivery()
    {
        using var vm = CreateVm(out _);
        using var client = Client((_, _) => throw new InvalidOperationException());
        var fake = new FakeDiagnostics();
        using var services = new BusinessServicesViewModel(vm, Configuration(), new(client), () => { }, fake);
        await services.RunDiagnosticsAsync(false);
        Assert.Equal(1, fake.Collections); Assert.Equal(0, fake.Sends);
        fake.WaitForCancellation = true;
        var send = services.RunDiagnosticsAsync(true);
        Assert.True(services.IsDiagnosticBusy); Assert.False(services.SendCommand.CanExecute(null));
        services.CancelDiagnosticCommand.Execute(null);
        await send.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Strings.BusinessDiagnosticsCancelled, services.DiagnosticStatus);
        Assert.True(services.HasReport);
    }

    [AvaloniaFact]
    public async Task DisposedController_CancelsAndIgnoresUncooperativeDiagnosticCompletion()
    {
        using var vm = CreateVm(out _);
        using var client = Client((_, _) => throw new InvalidOperationException());
        var fake = new FakeDiagnostics { CollectionGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var services = new BusinessServicesViewModel(vm, Configuration(), new(client), () => { }, fake);
        var collecting = services.RunDiagnosticsAsync(false);
        services.Dispose();
        fake.CollectionGate.SetResult("synthetic-report.zip");
        await collecting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(services.HasReport);
        Assert.Equal(0, fake.Sends);
        Assert.False(services.SendCommand.CanExecute(null));
    }

    [AvaloniaTheory]
    [InlineData(340)]
    [InlineData(420)]
    public async Task Window_ShowsCountryAndSendAboveFold_RefreshesAllChoices_AndKeepsBottomReachable(int width)
    {
        using var vm = CreateVm(out _);
        using var client = Client((_, _) => Task.FromResult(Response(BusinessClientConfigurationTests.Four)));
        BusinessServicesViewModel? services = null;
        var window = new BusinessWindow(static (_, _) => Task.FromResult(ServerProbeResult.Unknown), (model, changed) =>
            services = new(model, Configuration(), new(client), changed, new FakeDiagnostics())) { DataContext = vm, Width = width };
        window.Show();
        try
        {
            await Idle(services!);
            var selector = window.FindControl<ComboBox>("BusinessServerSelector")!;
            Assert.Equal(4, selector.Items.Count);
            var choice = Assert.IsType<BusinessServerChoiceViewModel>(selector.SelectedItem);
            Assert.Equal("IS", choice.CountryCode); Assert.NotNull(choice.CountryFlag);
            Assert.Equal("Исландия", choice.CountryName);
            var send = Assert.Single(window.GetVisualDescendants().OfType<Button>().Where(b => AutomationProperties.GetAutomationId(b) == "BusinessDiagnosticsSend"));
            Assert.True(send.IsEffectivelyEnabled);
            var top = send.TranslatePoint(default, window);
            Assert.True(top.HasValue && top.Value.Y >= 0 && top.Value.Y + send.Bounds.Height < window.Height);
            var scroll = Assert.IsType<ScrollViewer>(window.Content);
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 1);
            scroll.Offset = default;
            vm.StatusText = "VPNRouter Business";
            ScreenshotHelper.Capture(window, $"business-services-{width}");
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData("Iceland VLESS", "IS")]
    [InlineData("Germany-Office", "DE")]
    [InlineData("NL node", "NL")]
    [InlineData("Исландия VPN", "IS")]
    [InlineData("ISLAND-unknown", "")]
    [InlineData("Private custom name", "")]
    public void Country_UsesExplicitAdvertisedNamesAndBoundaries(string label, string expected) =>
        Assert.Equal(expected, BusinessServerCountry.Code(label));

    private static MainWindowViewModel CreateVm(out InMemorySettingsStore store)
    {
        store = new(); var settings = new AppSettings();
        BusinessDefaults.Apply(settings, BusinessClientConfigurationTests.Three, BusinessBootstrap.ReadRules());
        settings.App.Language = "ru"; store.Save(settings, AppPaths.ConfigYamlPath);
        return new(store);
    }
    private static BusinessClientConfiguration Configuration() => BusinessClientConfiguration.Parse(BusinessClientConfigurationTests.Bundle());
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new BusinessClientHttpTests.Handler(send));
    private static async Task Idle(BusinessServicesViewModel services)
    {
        if (!services.IsRefreshing) return;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs e) { if (!services.IsRefreshing) done.TrySetResult(); }
        services.PropertyChanged += Changed;
        try { if (!services.IsRefreshing) done.TrySetResult(); await done.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { services.PropertyChanged -= Changed; }
    }
    private sealed class FakeDiagnostics : IBusinessDiagnostics
    {
        internal int Collections, Sends;
        internal bool FailSend, WaitForCancellation;
        internal TaskCompletionSource<string>? CollectionGate;
        public Task<string> CollectAsync(CancellationToken ct) { Collections++; return CollectionGate?.Task ?? Task.FromResult("synthetic-report.zip"); }
        public async Task SendAsync(string path, CancellationToken ct)
        {
            Sends++; Assert.Equal("synthetic-report.zip", path);
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, ct);
            if (FailSend) throw new IOException("private response must not reach UI");
        }
    }
    private sealed class FailingStore : ISettingsStore
    {
        internal readonly AppSettings Settings = new(); internal bool FailSave;
        public AppSettings Load(string? path = null) => Settings;
        public void Save(AppSettings settings, string? path = null) { if (FailSave) throw new IOException("synthetic write failure"); }
        public string? ResetToDefaults(string? path = null) => null;
        public string? ConsumeRecoveryNotice() => null;
        public (int Count, string AtUtc) ConsumePlaceholderPruneNotice(AppSettings settings) => (0, "");
        public void StartWatching(string? path = null, Action<AppSettings>? onReload = null) { }
        public void StopWatching() { }
    }
}
