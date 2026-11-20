using System.Reflection;
using Avalonia.Controls;
using VPNRouter.App.Controls;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using VPNRouter.App.ViewModels;
using VPNRouter.App.ViewModels.Internals;
using VPNRouter.App.Views.Pages;
using Strings = VPNRouter.Core.Localization.Strings;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class SimpleConnectStatusTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExpandedSimplePickerExposesSubscriptionChecksAndRefresh(bool cached)
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.UiMode = "simple";
        settings.App.Subscriptions =
        [
            new() { Enabled = true, Url = "https://feed.invalid/simple", Servers = cached ? [Entry("test", "test.invalid")] : [] }
        ];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = CreateVm(store);
        using var view = new OpenPage(vm, width: 380);
        vm.OpenConfigPickerCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var ping = view.Page.FindControl<Button>("SimplePing")!;
        var deepCheck = view.Page.FindControl<Button>("SimpleDeepCheck")!;
        var refresh = view.Page.FindControl<Button>("SimpleRefreshSubscriptions")!;
        Assert.Same(vm.TestAllSubscriptionServersCommand, ping.Command);
        Assert.Same(vm.DeepVerifyAllSubscriptionServersCommand, deepCheck.Command);
        Assert.Same(vm.RefreshAllSubscriptionsCommand, refresh.Command);
        Assert.Equal(cached, ping.IsEffectivelyVisible);
        Assert.Equal(cached, deepCheck.IsEffectivelyVisible);
        Assert.True(refresh.IsEffectivelyVisible);
        Assert.True(refresh.IsEffectivelyEnabled);

        vm.IsTestingServers = true;
        vm.IsDeepTestingServers = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Strings.ServerTestCancel, ping.Content);
        Assert.Equal(Strings.ServerDeepStop, deepCheck.Content);
        Assert.False(refresh.IsEffectivelyEnabled);
        vm.IsTestingServers = false;
        vm.IsDeepTestingServers = false;

        vm.IsConnecting = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(refresh.IsEffectivelyEnabled);
        vm.SmpErrorText = Strings.StartTimeoutPhaseB;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.SmpHeroIsBusy);
        Assert.False(refresh.IsEffectivelyEnabled);
        vm.IsConnecting = false;
        vm.SmpErrorText = string.Empty;
        Dispatcher.UIThread.RunJobs();
        Assert.True(refresh.IsEffectivelyEnabled);

        vm.IsApplying = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(refresh.IsEffectivelyEnabled);
        vm.IsApplying = false;
        vm.Subscriptions.Single().Enabled = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(ping.IsEffectivelyVisible);
        Assert.False(deepCheck.IsEffectivelyVisible);
        Assert.False(refresh.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task PhaseBTimeoutAppearsOnTheSimplePage()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.App.UiMode = "simple";
        settings.Vless.Servers = [Entry("fixture", "test.invalid")];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = CreateVm(store);
        using var view = new OpenPage(vm);
        var started = false;
        vm.SimpleTunnelStart = (_, _, _) =>
        {
            started = true;
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.PhaseBTimeout));
        };

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(started);
        Assert.Equal(Strings.StartTimeoutPhaseB, BoundText(view.Page, "ConnectionDetails"));
        Assert.False(vm.SmpHeroIsBusy);
        Assert.False(vm.IsConnecting);
        Assert.False(vm.IsConnected);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnconfirmedStopRemainsVisibleOnSimplePage(bool tunnelStillActive)
    {
        var settings = new AppSettings();
        settings.App.UiMode = "simple";
        settings.Vless.Servers = [Entry("fixture", "test.invalid")];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = CreateVm(store);
        using var view = new OpenPage(vm);

        vm.IsConnected = tunnelStillActive;
        vm.SmpErrorText = Strings.TunnelStopUnconfirmed;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Strings.TunnelStopUnconfirmed, BoundText(view.Page, "ConnectionDetails"));
        Assert.Equal(tunnelStillActive, vm.IsConnected);
    }

    [AvaloniaFact]
    public async Task PendingRoutingAndServerEditsStayOutOfTheConnectedStatus()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.RoutingMode = "split";
        settings.App.UiMode = "simple";
        settings.App.Subscriptions = [new()
        {
            Enabled = true, Url = "https://feed.invalid/status", Servers =
            [
                Entry("accepted", "accepted.invalid"), Entry("pending", "pending.invalid")
            ]
        }];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = CreateVm(store);
        var engine = Engine(vm);
        using var view = new OpenPage(vm);
        var page = view.Page;
        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.SimpleProbe = async (rows, _) =>
        {
            probing.TrySetResult();
            await resume.Task;
            return rows.Select(s => new ServerLiveness(s, true, 1)).ToList();
        };
        vm.SimpleTunnelStart = (submission, _, _) =>
        {
            engine.CommitTunnelInfo(Generate(submission));
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };

        var start = vm.SmpToggleConnectCommand.ExecuteAsync(null);
        try
        {
            await probing.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var splitInput = page.FindControl<RadioButton>("SplitInput")!;
            Assert.True(splitInput.IsChecked);
            splitInput.SetCurrentValue(RadioButton.IsCheckedProperty, false);
            vm.SelectedSubscriptionServer = vm.SubscriptionServers.Single(s => s.Name == "pending");
        }
        finally
        {
            resume.TrySetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        Assert.True(vm.HasPendingAppChanges);
        Assert.Contains(Strings.SmpCfgSplit, BoundText(page, "ActiveMode"));
        Assert.Contains(Strings.SmpCfgFull, BoundText(page, "PendingConfig"));
        Assert.Contains("pending", BoundText(page, "PendingConfig"));
        Assert.True(page.FindControl<TextBlock>("ActiveMode")!.IsEffectivelyVisible);
        Assert.True(page.FindControl<TextBlock>("PendingConfig")!.IsEffectivelyVisible);
        Assert.Contains("accepted.invalid", BoundText(page, "ConnectionDetails"));
        Assert.False(vm.IsSplitTunnel);
        Assert.Equal("pending", vm.SelectedSubscriptionServer?.Name);
        Assert.Contains("subscribe/split", vm.StatusText);
        Assert.Contains("accepted.invalid", vm.StatusText);
        Assert.DoesNotContain("pending", vm.StatusText);
        Assert.Contains("accepted.invalid", vm.SmpActiveServerLine);
        Assert.Contains("accepted.invalid", vm.SimpleActiveOutboundLine);
        Assert.Equal("accepted", vm.SmpConfigServer?.Name);
        Assert.False(vm.SelectedSubscriptionServer!.IsActive);
        Assert.True(vm.SubscriptionServers.Single(s => s.Name == "accepted").IsActive);
    }

    [AvaloniaTheory]
    [InlineData("generated", true)]
    [InlineData("subscribe", false)]
    public void AutoGroupChangesTheOpenCardAndRejectsOldTags(string mode, bool autoSelect)
    {
        var first = Entry("same", "one.invalid");
        var second = Entry("same", autoSelect ? "two.invalid" : "one.invalid", 8443);
        var settings = new AppSettings();
        settings.App.ConfigMode = mode;
        settings.Vless.AutoSelectBestServer = autoSelect;
        settings.Vless.ActiveServer = first.Name;
        if (mode == "subscribe") settings.App.Subscriptions = [new() { Servers = [first, second] }];
        else settings.Vless.Servers = [first, second];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = CreateVm(store);
        using var view = new OpenPage(vm);
        var page = view.Page;
        var submission = VpnEngine.CopySessionSettings(settings);
        var accepted = Generate(submission);
        submission.Vless.Servers.Clear();
        submission.App.Subscriptions.Clear();
        submission.App.RoutingMode = "full";
        Assert.Equal("split", accepted.RoutingMode);
        var engine = Engine(vm);
        engine.CommitTunnelInfo(accepted);
        vm.IsConnected = true;
        vm.AutoSelectBestServer = !autoSelect;
        vm.HasPendingAppChanges = true;
        Assert.True(accepted.UsesAutoGroup);
        Assert.Equal(mode == "subscribe" ? 2 : 0, accepted.SubscriptionServerCount);
        Assert.Null(accepted.FindMember("another-session"));

        vm.ObserveAutoMember(accepted, "vless-same");
        Assert.Equal(first.Port, vm.SmpConfigServer?.Port);
        Assert.True(vm.SmpConfigServer!.IsActive);
        vm.ObserveAutoMember(accepted, "vless-same-2");
        Assert.Equal(second.Port, vm.SmpConfigServer?.Port);
        Assert.True(vm.SmpConfigServer!.IsActive);
        Assert.Equal("same", BoundText(page, "ConfigName"));
        Assert.Contains(second.Server, BoundText(page, "ConnectionDetails"));

        vm.ObserveAutoMember(accepted, null);
        Assert.Null(vm.SmpConfigServer);
        Assert.DoesNotContain(second.Server, BoundText(page, "ConnectionDetails"));

        vm.ObserveAutoMember(accepted, "vless-same-2");
        Assert.True(vm.SmpConfigServer!.IsActive);
        // An Apply that narrows the same pool to one row keeps IsConnected true.
        var replacement = VpnEngine.CopySessionSettings(settings);
        if (mode == "subscribe") replacement.App.Subscriptions[0].Servers.RemoveAt(1);
        else replacement.Vless.Servers.RemoveAt(1);
        engine.CommitTunnelInfo(Generate(replacement));
        ReportStatus(engine, "Applied (restart, PID 42)");
        vm.ObserveAutoMember(accepted, "vless-same-2");
        var rows = mode == "subscribe" ? vm.SubscriptionServers : vm.Servers;
        Assert.Equal(first.Port, Assert.Single(rows, row => row.IsActive).Port);
        Assert.Contains(first.Server, BoundText(page, "ConnectionDetails"));

        vm.IsConnecting = true;
        ReportStatus(engine, "Stopped");
        vm.IsConnected = false;
        vm.IsConnecting = false;
        Assert.DoesNotContain(rows, row => row.IsActive);
    }

    [AvaloniaFact]
    public void SingleProxyKeepsItsProtocolWhenTwoRowsShareAnEndpoint()
    {
        var first = Entry("vless", "shared.invalid");
        var selected = Entry("hy2", "shared.invalid");
        selected.Protocol = "hysteria2";
        selected.Password = "fixture";
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.ActiveSubscriptionServer = selected.Name;
        settings.App.Subscriptions = [new() { Servers = [first, selected] }];
        settings.Vless.AutoSelectBestServer = true;
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = CreateVm(store);
        using var view = new OpenPage(vm);
        var page = view.Page;
        var tunnel = Generate(settings);
        Assert.False(tunnel.UsesAutoGroup);
        Assert.Equal("hy2", tunnel.Server.Name);
        Engine(vm).CommitTunnelInfo(tunnel);
        vm.IsConnected = true;
        Assert.Equal("hy2", BoundText(page, "ConfigName"));
        var disc = page.FindControl<ProtocolDisc>("ConfigProtocol")!;
        Assert.True(disc.IsEffectivelyVisible);
        Assert.Equal(ProtocolGroup.GamesVoice, disc.Group);
        Assert.Equal("hy2", Assert.Single(vm.SubscriptionServers, row => row.IsActive).Name);

        // A subsequently detected service owns a different process, even if a local snapshot exists.
        vm.SyncConnectedWithVpnRuntime(vpnRunning: true);
        Assert.Equal(Strings.SmpStatusConnectedNoDetails, vm.StatusText);
        Assert.Equal(Strings.SmpStatusConnectedNoDetails, BoundText(page, "ConnectionDetails"));
        Assert.Equal(Strings.SmpModeUnknown, BoundText(page, "ActiveMode"));
        Assert.Equal(Strings.SmpStatusConnectedNoDetails, BoundText(page, "ConfigName"));
        Assert.False(disc.IsEffectivelyVisible);
        Assert.Null(vm.SmpConfigServer);
        Assert.Empty(vm.SimpleActiveOutboundLine);
        Assert.DoesNotContain(vm.SubscriptionServers, row => row.IsActive);
    }

    private static MainWindowViewModel CreateVm(InMemorySettingsStore store) =>
        new MainWindowViewModel(store);

    private static VpnEngine Engine(MainWindowViewModel vm) =>
        Assert.IsType<VpnEngine>(typeof(MainWindowViewModel)
            .GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm));

    private static VlessServerEntry Entry(string name, string host, int port = 443) =>
        new() { Name = name, Server = host, Port = port, Security = "none", Uuid = "00000000-0000-4000-8000-000000000001" };

    private static AcceptedTunnelInfo Generate(AppSettings settings)
    {
        settings.App.BypassRussianTraffic = false;
        var sources = new Dictionary<string, VlessServerEntry>();
        var json = ConfigPipeline.Generate(new Profile { Name = "offline" }, [], settings, proxySources: sources);
        return new AcceptedTunnelInfo(settings, json, "", null, sources);
    }

    private sealed class OpenPage : IDisposable
    {
        public SimplePage Page { get; }
        private readonly Window _window;

        public OpenPage(MainWindowViewModel vm, int width = 520)
        {
            Page = new SimplePage { DataContext = vm };
            _window = new Window { Content = Page, Width = width, Height = 800 };
            _window.Show();
        }

        public void Dispose() => _window.Close();
    }

    private static string BoundText(SimplePage page, string name)
    {
        Dispatcher.UIThread.RunJobs();
        return page.FindControl<TextBlock>(name)!.Text!;
    }

    private static void ReportStatus(VpnEngine engine, string message)
    {
        typeof(VpnEngine).GetMethod("OnStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, [message]);
        Dispatcher.UIThread.RunJobs();
    }
}
