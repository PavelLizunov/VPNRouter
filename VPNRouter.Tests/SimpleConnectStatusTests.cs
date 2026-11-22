using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using VPNRouter.App.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        var options = view.Page.FindControl<ToggleButton>("SimpleConnectionOptions")!;
        var link = view.Page.FindControl<TextBox>("SimpleConfigLink")!;
        Assert.False(options.IsChecked);
        Assert.False(link.IsAttachedToVisualTree() && link.IsEffectivelyVisible);
        options.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(link.IsAttachedToVisualTree() && link.IsEffectivelyVisible);

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
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedStopStaysLocalizedUntilTheTunnelStops(bool quit)
    {
        var settings = new AppSettings();
        settings.App.UiMode = "simple";
        settings.Vless.Servers = [Entry("fixture", "test.invalid")];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = CreateVm(store);
        using var view = new OpenPage(vm);

        var engine = Engine(vm);
        using var manager = new SingBoxManager(new SingBoxSettings(), http: new FakeHttpClient(), runner: new FakeProcessRunner());
        var managerField = typeof(VpnEngine).GetField("_singBox", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var identity = new FakeProcessHandle { ExitQueryUnavailable = true };
        typeof(SingBoxManager).GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, identity);
        typeof(SingBoxManager).GetField("_exactStopUnconfirmed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, true);
        var originalLanguage = Strings.Lang;
        managerField.SetValue(engine, manager);
        try
        {
            vm.IsConnected = true;
            if (quit)
                Assert.False(vm.TryPrepareQuit());
            else
                await vm.SmpToggleConnectCommand.ExecuteAsync(null);
            var firstWarning = BoundText(view.Page, "ConnectionDetails");
            Assert.Equal(Strings.TunnelStopUnconfirmed, firstWarning);
            Assert.True(vm.SimpleCtaIsConnected);
            Assert.True(vm.SmpHeroIsWarn);
            Assert.False(vm.SmpHeroIsOn);
            Assert.True(vm.SimpleStatusIsWarn);
            Assert.False(vm.SimpleStatusIsOn);
            Assert.False(vm.SimpleStatusIsOff);
            Assert.Equal(firstWarning, vm.VpnBadgeTooltip);
            ReportStatus(engine, "Tunnel stop was not confirmed. Retry disconnect.");
            Assert.Equal(firstWarning, vm.StatusText);

            vm.ToggleLanguageCommand.Execute(null);
            Assert.Equal(Strings.TunnelStopUnconfirmed, BoundText(view.Page, "ConnectionDetails"));
            Assert.NotEqual(firstWarning, BoundText(view.Page, "ConnectionDetails"));
            Assert.Equal(Strings.TunnelStopUnconfirmed, vm.VpnBadgeTooltip);
            Assert.Equal(Strings.TunnelStopUnconfirmed, vm.StatusText);

            identity.ExitQueryUnavailable = false;
            identity.SignalExit(0);
            Assert.True(engine.HasActiveTunnel, "The manager still retains ownership after the process exits");
            vm.SyncConnectedWithVpnRuntime(vpnRunning: false);
            Assert.True(vm.SimpleCtaIsConnected);
            Assert.False(vm.SimpleCtaIsDisconnected);
            Assert.Equal(Strings.TunnelStopUnconfirmed, BoundText(view.Page, "ConnectionDetails"));

            managerField.SetValue(engine, null);
            if (quit)
                ReportStatus(engine, "Stopped");
            else
                vm.SyncConnectedWithVpnRuntime(vpnRunning: false);
            Assert.False(vm.IsConnected);
            Assert.False(vm.SmpHeroIsWarn);
            Assert.True(vm.SimpleStatusIsOff);
            Assert.Equal(Strings.NotConnected, vm.StatusText);
            Assert.NotEqual(Strings.TunnelStopUnconfirmed, BoundText(view.Page, "ConnectionDetails"));
        }
        finally
        {
            managerField.SetValue(engine, null);
            identity.ExitQueryUnavailable = false;
            identity.SignalExit(0);
            Strings.Lang = originalLanguage;
        }
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
    [InlineData(360, true)]
    [InlineData(520, false)]
    public void SimpleServerSearchPreservesSelectionUntilExactRowActivation(int width, bool autoSelect)
    {
        var previousLanguage = VPNRouter.App.Localization.Strings.Lang;
        try { CheckSimpleServerSearch(width, autoSelect); }
        finally { VPNRouter.App.Localization.Strings.Lang = previousLanguage; }
    }

    private static void CheckSimpleServerSearch(int width, bool autoSelect)
    {
        var initial = Entry("Sweden", "first.invalid");
        var firstMatch = Entry("Amsterdam", "ams-one.invalid");
        firstMatch.Transport.Type = "ws";
        var chosen = Entry("Amsterdam", "ams-two.invalid", 8443);
        var settings = new AppSettings();
        settings.App.Language = width == 360 ? "ru" : "en";
        settings.App.ConfigMode = "subscribe";
        settings.App.ActiveSubscriptionServer = initial.Name;
        settings.Vless.AutoSelectBestServer = autoSelect;
        settings.App.Subscriptions =
        [
            new()
            {
                Enabled = true, Url = "https://feed.invalid/picker",
                Servers = [initial, firstMatch, chosen, .. Enumerable.Range(1, 13)
                    .Select(n => Entry($"Server {n}", $"node-{n}.invalid"))]
            }
        ];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = CreateVm(store);
        using var view = new OpenPage(vm, width);
        var search = view.Page.FindControl<TextBox>("SimpleServerSearch")!;
        var list = view.Page.FindControl<ServerPickerListBox>("SimpleServerResults")!;
        var count = view.Page.FindControl<TextBlock>("SimpleServerCount")!;
        var link = view.Page.FindControl<TextBox>("SimpleConfigLink")!;
        var previous = vm.SelectedSubscriptionServer;
        var before = store.SaveCount;
        var input = vm.SmpInput;

        Assert.False(search.IsEffectivelyVisible);
        if (OperatingSystem.IsWindows()) view.Capture($"simple-home-{width}");
        vm.OpenConfigPickerCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(search.IsEffectivelyVisible);
        Assert.False(link.IsAttachedToVisualTree() && link.IsEffectivelyVisible);
        var groups = view.Page.FindControl<ItemsControl>("SimpleServerGroups")!;
        var groupedRows = groups.Items.Cast<SimpleServerGroup>().ToArray();
        Assert.Equal(new[] { ProtocolGroup.Daily, ProtocolGroup.WebFallback }, groupedRows.Select(group => group.Category));
        Assert.Equal(16, groupedRows.Sum(group => group.Servers.Length));
        Assert.Same(previous, groupedRows[0].Servers[0]);
        Assert.False(list.IsEffectivelyVisible);
        Assert.All(groupedRows, group => Assert.False(group.IsExpanded));

        FindGroupButton(groups, ProtocolGroup.Daily, disclosure: true).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(groupedRows[0].IsExpanded);
        Assert.Same(previous, vm.SelectedSubscriptionServer);
        Assert.Equal(before, store.SaveCount);
        if (OperatingSystem.IsWindows()) view.Capture($"simple-picker-{width}");

        FindGroupButton(groups, ProtocolGroup.WebFallback, disclosure: true).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(groupedRows[0].IsExpanded);
        Assert.True(groupedRows[1].IsExpanded);

        search.Text = "no matches";
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(list.Items);
        Assert.Equal(Strings.SmpNoMatchingServers, count.Text);
        Assert.Same(previous, vm.SelectedSubscriptionServer);
        vm.OpenConfigPickerCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(string.IsNullOrEmpty(search.Text));
        Assert.Same(previous, vm.SelectedSubscriptionServer);

        vm.OpenConfigPickerCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        search.Text = "  aMsTeRdAm  ";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, list.Items.Count);
        Assert.InRange(list.Bounds.Height, 1, 172);
        var firstItem = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
        firstItem.Focus();
        PressPickerKey(firstItem, Key.Down);
        Assert.Same(vm.SubscriptionServers[2], list.SelectedItem);
        Assert.Same(previous, vm.SelectedSubscriptionServer);
        Assert.Equal(input, vm.SmpInput);
        Assert.Equal(autoSelect, vm.AutoSelectBestServer);
        Assert.Equal(before, store.SaveCount);
        var item = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(1));
        PressPickerKey(item, Key.Enter);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(vm.SubscriptionServers[2], vm.SelectedSubscriptionServer);
        Assert.False(vm.AutoSelectBestServer);
        Assert.False(search.IsEffectivelyVisible);
        Assert.Equal(before + 1, store.SaveCount);
        var saved = store.Load(AppPaths.ConfigYamlPath);
        Assert.Equal(SubscriptionEntry.RowKey(chosen), saved.App.ActiveSubscriptionRow);
        Assert.Equal(saved.App.ActiveSubscriptionRow, saved.App.SubscriptionPreference?.RowKey);
        Assert.Equal(chosen.Server, Generate(VpnEngine.CopySessionSettings(saved)).Server.Address);

        vm.OpenConfigPickerCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        FindGroupButton(groups, ProtocolGroup.WebFallback, disclosure: false).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var automatic = store.Load(AppPaths.ConfigYamlPath);
        Assert.Equal(ProtocolGroup.WebFallback, automatic.App.SubscriptionPreference?.Category);
        Assert.False(vm.AutoSelectBestServer);
        Assert.False(search.IsEffectivelyVisible);
        Assert.Equal(firstMatch.Server, Generate(VpnEngine.CopySessionSettings(automatic)).Server.Address);
    }

    [AvaloniaFact]
    public async Task SimpleConnectPicksTheFastestVerifiedServerWithinTheSelectedCategory()
    {
        var slow = Entry("slow", "slow.invalid");
        var fast = Entry("fast", "fast.invalid");
        var other = Entry("other", "other.invalid");
        other.Transport.Type = "ws";
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.UiMode = "simple";
        settings.App.Subscriptions =
        [
            new() { Url = "https://feed.invalid/auto", Servers = [slow, fast, other] }
        ];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = CreateVm(store);

        vm.ConfirmSimpleCategory(ProtocolGroup.Daily);
        string[]? probed = null;
        vm.SimpleProbe = (rows, _) =>
        {
            probed = rows.Select(row => row.Name).ToArray();
            return Task.FromResult(rows.Select(row =>
                new ServerLiveness(row, Alive: true, LatencyMs: row.Name == fast.Name ? 20 : 100)).ToList());
        };
        AcceptedTunnelInfo? started = null;
        vm.SimpleTunnelStart = (snapshot, _, _) =>
        {
            started = Generate(snapshot);
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Equal(new[] { slow.Name, fast.Name }, probed);
        Assert.Equal(fast.Server, Assert.IsType<AcceptedTunnelInfo>(started).Server.Address);
        Assert.Equal(ProtocolGroup.Daily, store.Load(AppPaths.ConfigYamlPath).App.SubscriptionPreference?.Category);
    }

    [AvaloniaFact]
    public async Task MissingPinWaitsForANewChoiceAndAnExplicitFeedChangeClearsIt()
    {
        var pinned = Entry("pinned", "pinned.invalid");
        var alternative = Entry("alternative", "alternative.invalid");
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.Subscriptions =
        [
            new() { Url = "https://feed.invalid/first", Servers = [pinned] },
            new() { Url = "https://feed.invalid/second", Servers = [alternative] },
        ];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = CreateVm(store);
        vm.ConfirmSimpleServer(vm.SubscriptionServers[0]);
        vm.Subscriptions[0].Enabled = false;

        AcceptedTunnelInfo? started = null;
        vm.SimpleProbe = (rows, _) =>
            Task.FromResult(rows.Select(row => new ServerLiveness(row, Alive: true, LatencyMs: 1)).ToList());
        vm.SimpleTunnelStart = (snapshot, _, _) =>
        {
            started = Generate(snapshot);
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Null(started);
        Assert.Equal(SubscriptionEntry.RowKey(pinned), vm.SimpleServerPreference?.RowKey);
        Assert.Equal(Strings.SmpSelectionUnavailable, vm.SmpErrorText);

        vm.SmpInput = settings.App.Subscriptions[1].Url;
        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Null(vm.SimpleServerPreference);
        Assert.Equal(alternative.Server, Assert.IsType<AcceptedTunnelInfo>(started).Server.Address);
    }

    private static Button FindGroupButton(ItemsControl groups, ProtocolGroup category, bool disclosure) =>
        Assert.Single(groups.GetVisualDescendants().OfType<Button>(), button =>
            button.Classes.Contains("category") && button.Classes.Contains("disclosure") == disclosure &&
            button.DataContext is SimpleServerGroup group && group.Category == category);

    [AvaloniaFact]
    public void SimplePickerLabelsFollowLiveLanguageChanges()
    {
        var previousLanguage = VPNRouter.App.Localization.Strings.Lang;
        try
        {
            var settings = new AppSettings();
            settings.App.Language = "en";
            var store = new InMemorySettingsStore();
            store.Save(settings, AppPaths.ConfigYamlPath);
            using var vm = CreateVm(store);
            using var view = new OpenPage(vm);
            var search = view.Page.FindControl<TextBox>("SimpleServerSearch")!;
            var options = view.Page.FindControl<ToggleButton>("SimpleConnectionOptions")!;
            var appRules = view.Page.FindControl<Button>("SimpleAppRules")!;
            Assert.Equal("Find a server", search.PlaceholderText);

            vm.SetLanguageRussianCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Найти сервер", search.PlaceholderText);
            Assert.Equal("Найти сервер", AutomationProperties.GetName(search));
            Assert.Equal("Ссылка и проверка серверов", options.Content);
            Assert.Equal("Настроить приложения", appRules.Content);
        }
        finally { VPNRouter.App.Localization.Strings.Lang = previousLanguage; }
    }

    [AvaloniaTheory]
    [InlineData("generated", false)]
    [InlineData("subscribe", true)]
    public void ManualServerPickerDisablesTheCrossHostAutoGroup(string mode, bool selectAgain)
    {
        var first = Entry("first", "first.invalid");
        var chosen = Entry("second", "second.invalid");
        var subscription = mode == "subscribe";
        var settings = new AppSettings();
        settings.App.ConfigMode = mode;
        settings.App.Language = "en";
        settings.Vless.AutoSelectBestServer = true;
        settings.Vless.ActiveServer = first.Name;
        if (subscription) settings.App.Subscriptions = [new() { Servers = [first, chosen] }];
        else settings.Vless.Servers = [first, chosen];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = CreateVm(store);

        var row = (subscription ? vm.SubscriptionServers : vm.Servers)[1];
        if (selectAgain) vm.SelectedSubscriptionServer = row;
        Assert.True(vm.AutoSelectBestServer);
        Assert.True(Generate(VpnEngine.CopySessionSettings(store.Load(AppPaths.ConfigYamlPath))).UsesAutoGroup);

        UserControl page = subscription ? new SubscribePage() : new ServersPage();
        page.DataContext = vm;
        var window = new Window { Content = page, Width = 520, Height = 800 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var picker = page.FindControl<ServerPickerListBox>(subscription ? "SubList" : "ServerList")!;
            Assert.Equal(selectAgain, ReferenceEquals(row, picker.SelectedItem));

            if (selectAgain)
            {
                // Model a service-managed connection with this row already active.
                vm.IsConnected = true;
                row.IsActive = true;
            }
            else
            {
                var firstItem = Assert.IsType<ListBoxItem>(picker.ContainerFromIndex(0));
                firstItem.Focus();
                var beforeNavigation = store.SaveCount;
                PressPickerKey(firstItem, Key.Down);
                Assert.Same(row, picker.SelectedItem);
                Assert.True(vm.AutoSelectBestServer);
                Assert.Equal(beforeNavigation, store.SaveCount);
                vm.SelectedServer = vm.Servers[0];
                Assert.NotSame(row, picker.SelectedItem);
            }

            var beforeConfirmation = store.SaveCount;
            var item = Assert.IsType<ListBoxItem>(picker.ContainerFromIndex(1));
            PressPickerKey(item, Key.Enter);
            Assert.Same(row, picker.SelectedItem);
            Assert.False(vm.AutoSelectBestServer);
            Assert.Equal(beforeConfirmation + 1, store.SaveCount);
            if (selectAgain)
            {
                Assert.True(vm.HasPendingAppChanges);
                Assert.Contains("Click Apply", vm.StatusText);
            }

            var saved = store.Load(AppPaths.ConfigYamlPath);
            Assert.False(saved.Vless.AutoSelectBestServer);
            var connection = Generate(VpnEngine.CopySessionSettings(saved));
            Assert.False(connection.UsesAutoGroup);
            Assert.Equal(chosen.Name, connection.Server.Name);
        }
        finally { window.Close(); }
    }

    private static void PressPickerKey(ListBoxItem item, Key key) => item.RaiseEvent(new KeyEventArgs
    {
        Source = item,
        RoutedEvent = InputElement.KeyDownEvent,
        Key = key
    });

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

        public void Capture(string name)
        {
            using var bitmap = _window.CaptureRenderedFrame();
            Assert.NotNull(bitmap);
            bitmap.Save(Path.Combine(ScreenshotHelper.ScreenshotsDir, $"{name}.png"), PngBitmapEncoderOptions.Default);
        }
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
