using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
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
    public void SimpleDiagnosticsAreSeparateFromTheLinkAndServerPicker(bool cached)
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
        var options = view.Page.FindControl<ToggleButton>("SimpleCheckServers")!;
        var linkButton = FindOnPage<ToggleButton>(view.Page, "SimpleLinkButton")!;
        var link = FindOnPage<TextBox>(view.Page, "SimpleConfigLink")!;
        Assert.True(options.IsEffectivelyVisible);
        Assert.True(linkButton.IsEffectivelyVisible);
        Assert.False(options.IsChecked);
        options.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(Shown(link));

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
    public void FirstRunShowsTheLinkAndOneConnectAction()
    {
        var settings = new AppSettings();
        settings.App.Language = "ru";
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = CreateVm(store);
        using var view = new OpenPage(vm, 320, ThemeVariant.Light);
        Dispatcher.UIThread.RunJobs();
        var link = view.Page.FindControl<TextBox>("SimpleInitialLink")!;
        var connect = Assert.Single(view.Page.GetVisualDescendants().OfType<Button>(),
            control => control.IsEffectivelyVisible && ReferenceEquals(control.Command, vm.SmpToggleConnectCommand));
        Assert.True(link.IsEffectivelyVisible);
        Assert.False(connect.IsEffectivelyEnabled);
        Assert.False(Shown(FindOnPage<ToggleButton>(view.Page, "SimpleLinkButton")!));
        Assert.False(Shown(FindOnPage<ToggleButton>(view.Page, "SimpleServerSearchButton")!));
        if (OperatingSystem.IsWindows()) view.Capture("simple-home-first-link-320");

        var before = store.SaveCount;
        link.Text = "https://feed.invalid/first-run";
        Dispatcher.UIThread.RunJobs();
        Assert.True(connect.IsEffectivelyEnabled);
        Assert.False(vm.SmpHasConfig);
        Assert.Equal(before, store.SaveCount);
    }

    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(450)]
    public void SearchTransfersOnlyAnUncommittedDraftAndEscapeRestoresTheSavedLink(int width)
    {
        var language = VPNRouter.App.Localization.Strings.Lang;
        try
        {
            const string savedLink = "https://feed.invalid/current";
            var settings = new AppSettings();
            settings.App.Language = "ru";
            settings.App.ConfigMode = "subscribe";
            settings.App.Subscriptions =
            [
                new() { Url = savedLink, Servers = [Entry("Sweden", "one.invalid"), Entry(new string('Д', 160), "two.invalid")] }
            ];
            var store = new InMemorySettingsStore();
            store.Save(settings, AppPaths.ConfigYamlPath);
            using var vm = CreateVm(store);
            using var view = new OpenPage(vm, width, width == 320 ? ThemeVariant.Light : ThemeVariant.Dark);
            var page = view.Page;
            var linkButton = FindOnPage<ToggleButton>(page, "SimpleLinkButton")!;
            var searchButton = FindOnPage<ToggleButton>(page, "SimpleServerSearchButton")!;
            var search = FindOnPage<TextBox>(page, "SimpleServerSearch")!;
            var link = FindOnPage<TextBox>(page, "SimpleConfigLink")!;
            var transfer = FindOnPage<Button>(page, "SimpleTransferLink")!;
            var groups = FindOnPage<ItemsControl>(page, "SimpleServerGroups")!;
            var selected = vm.SelectedSubscriptionServer;
            var before = store.SaveCount;

            Assert.True(linkButton.IsEffectivelyVisible);
            Assert.False(Shown(searchButton));
            vm.OpenConfigPickerCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(linkButton.IsEffectivelyVisible);
            Assert.True(searchButton.IsEffectivelyVisible);
            Assert.False(Shown(search));

            Toggle(page, "SimpleServerSearchButton", true);
            Assert.True(search.IsFocused);
            search.Text = "Швеция";
            Dispatcher.UIThread.RunJobs();
            var result = Assert.Single(FindOnPage<ServerPickerListBox>(page, "SimpleServerResults")!.Items);
            Assert.Same(vm.SubscriptionServers[0], result);
            PressPickerKey(search, Key.Escape);
            Dispatcher.UIThread.RunJobs();
            Assert.True(searchButton.IsFocused);
            Assert.False(Shown(search));
            Assert.True(string.IsNullOrEmpty(search.Text));

            Toggle(page, "SimpleServerSearchButton", true);
            var draft = "https://feed.invalid/" + new string('a', 320);
            search.Text = draft;
            Dispatcher.UIThread.RunJobs();
            PressPickerKey(search, Key.Enter);
            Assert.True(transfer.IsEffectivelyVisible);
            Assert.False(Shown(link));
            Assert.False(vm.IsConnecting);
            Assert.Equal(before, store.SaveCount);
            if (OperatingSystem.IsWindows()) view.Capture($"simple-picker-search-{width}");

            transfer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(link.IsFocused);
            Assert.Equal(draft, link.Text);
            Assert.False(Shown(search));
            Assert.True(string.IsNullOrEmpty(search.Text));
            Assert.True(link.Bounds.Width < width);
            Assert.True(groups.IsEffectivelyVisible);

            link.CaretIndex = 12;
            vm.OpenConfigPickerCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(link.IsEffectivelyVisible);
            Assert.Equal(12, link.CaretIndex);
            Assert.Equal(draft, link.Text);
            Assert.Equal(savedLink, vm.SmpInput);
            Assert.Same(selected, vm.SelectedSubscriptionServer);
            Assert.Equal(before, store.SaveCount);
            if (OperatingSystem.IsWindows()) view.Capture($"simple-picker-editor-{width}");

            link.Text = string.Empty;
            Dispatcher.UIThread.RunJobs();
            PressPickerKey(link, Key.Enter);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Strings.SmpLinkInputInvalid, BoundText(page, "SimpleLinkError"));
            PressPickerKey(link, Key.Escape);
            Dispatcher.UIThread.RunJobs();
            Assert.False(Shown(link));
            Assert.True(linkButton.IsFocused);
            Assert.Equal(savedLink, link.Text);
            Assert.Equal(before, store.SaveCount);

            Toggle(page, "SimpleLinkButton", true);
            var save = FindOnPage<Button>(page, "SimpleSaveLink")!;
            Assert.True(save.Focus());
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(linkButton.IsFocused);
            Assert.False(Shown(link));
            Assert.Equal(before, store.SaveCount);
        }
        finally { VPNRouter.App.Localization.Strings.Lang = language; }
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
        var search = FindOnPage<TextBox>(view.Page, "SimpleServerSearch")!;
        var list = FindOnPage<ServerPickerListBox>(view.Page, "SimpleServerResults")!;
        var count = FindOnPage<TextBlock>(view.Page, "SimpleServerCount")!;
        var link = FindOnPage<TextBox>(view.Page, "SimpleConfigLink")!;
        var previous = vm.SelectedSubscriptionServer;
        var before = store.SaveCount;
        var input = vm.SmpInput;

        Assert.False(Shown(search));
        if (OperatingSystem.IsWindows()) view.Capture($"simple-home-{width}");
        vm.OpenConfigPickerCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(Shown(search));
        Assert.False(Shown(link));
        var groups = FindOnPage<ItemsControl>(view.Page, "SimpleServerGroups")!;
        var groupedRows = groups.Items.Cast<SimpleServerGroup>().ToArray();
        Assert.Equal(new[] { ProtocolGroup.Daily, ProtocolGroup.WebFallback }, groupedRows.Select(group => group.Category));
        Assert.Equal(16, groupedRows.Sum(group => group.Servers.Length));
        Assert.Same(previous, groupedRows[0].Servers[0]);
        Assert.False(Shown(list));
        Assert.All(groupedRows, group => Assert.False(group.IsExpanded));

        FindGroupButton(groups, ProtocolGroup.Daily, disclosure: true).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(groupedRows[0].IsExpanded);
        Assert.Equal("SE", previous!.CountryCode);
        Assert.NotNull(previous.CountryFlag);
        Assert.False(vm.SubscriptionServers[1].HasCountryFlag);
        Assert.Same(previous, vm.SelectedSubscriptionServer);
        Assert.Equal(before, store.SaveCount);
        if (OperatingSystem.IsWindows()) view.Capture($"simple-picker-{width}");

        FindGroupButton(groups, ProtocolGroup.WebFallback, disclosure: true).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(groupedRows[0].IsExpanded);
        Assert.True(groupedRows[1].IsExpanded);

        Toggle(view.Page, "SimpleServerSearchButton", true);
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
        Toggle(view.Page, "SimpleServerSearchButton", true);
        search.Text = "  aMsTeRdAm  ";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, list.Items.Count);
        Assert.True(list.Bounds.Height > 0);
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
        Assert.Same(vm.SelectedSubscriptionServer, vm.SmpConfigServer);
        Assert.Equal(ProtocolGroup.Daily, view.Page.FindControl<ProtocolDisc>("ConfigProtocol")!.Group);
        Assert.False(vm.AutoSelectBestServer);
        Assert.False(Shown(search));
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
        Assert.Equal(ProtocolGroup.WebFallback, automatic.App.SubscriptionPreference?.GetCategory());
        Assert.False(vm.AutoSelectBestServer);
        Assert.False(Shown(search));
        Assert.Equal(firstMatch.Server, Generate(VpnEngine.CopySessionSettings(automatic)).Server.Address);

        vm.OpenConfigPickerCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        FindGroupButton(groups, ProtocolGroup.WebFallback, disclosure: true).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var groupList = Assert.Single(groups.GetVisualDescendants().OfType<ServerPickerListBox>(),
            picker => picker.IsEffectivelyVisible);
        var groupRow = Assert.IsType<ListBoxItem>(groupList.ContainerFromIndex(0));
        groupRow.Focus();
        PressPickerKey(groupRow, Key.Enter);
        Dispatcher.UIThread.RunJobs();

        Assert.False(Shown(search));
        Assert.Equal(SubscriptionEntry.RowKey(firstMatch), vm.SimpleServerPreference?.RowKey);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectPicksWithinTheCategoryAcrossAModeSwitch(bool startInAdvanced)
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
        if (startInAdvanced) vm.ToggleUiModeCommand.Execute(null);
        string[]? probed = null;
        List<ServerLiveness>? results = null;
        var probe = new TaskCompletionSource<List<ServerLiveness>>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.SimpleProbe = (rows, _) =>
        {
            probed = rows.Select(row => row.Name).ToArray();
            results = rows.Select(row =>
                new ServerLiveness(row, Alive: true, LatencyMs: row.Name == fast.Name ? 20 : 100)).ToList();
            return probe.Task;
        };
        var starts = 0;
        AcceptedTunnelInfo? started = null;
        vm.SimpleTunnelStart = (snapshot, _, _) =>
        {
            starts++;
            started = Generate(snapshot);
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };

        var connect = startInAdvanced ? vm.ToggleConnectionCommand : vm.SmpToggleConnectCommand;
        var connecting = connect.ExecuteAsync(null);
        Assert.True(vm.IsConnecting);
        var preference = vm.SimpleServerPreference;
        vm.ToggleUiModeCommand.Execute(null);
        Assert.Equal(preference, vm.SimpleServerPreference);
        Assert.Equal(0, starts);
        probe.SetResult(Assert.IsType<List<ServerLiveness>>(results));
        await connecting;
        Assert.Equal(1, starts);

        Assert.Equal(new[] { slow.Name, fast.Name }, probed);
        Assert.Equal(fast.Server, Assert.IsType<AcceptedTunnelInfo>(started).Server.Address);
        Assert.Equal(ProtocolGroup.Daily, store.Load(AppPaths.ConfigYamlPath).App.SubscriptionPreference?.GetCategory());
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
        using var view = new OpenPage(vm, 360);
        vm.ConfirmSimpleServer(vm.SubscriptionServers[0]);
        vm.Subscriptions[0].Enabled = false;

        Assert.DoesNotContain(vm.SubscriptionServers, row => row.Name == pinned.Name);
        Assert.Null(vm.SmpConfigServer);
        Assert.Equal(Strings.SmpSelectionUnavailableTitle, BoundText(view.Page, "ConfigName"));
        Assert.False(view.Page.FindControl<ProtocolDisc>("ConfigProtocol")!.IsEffectivelyVisible);

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

    [AvaloniaFact]
    public async Task RefreshRemovingThePinnedRowDoesNotChooseItsSibling()
    {
        var pinned = Entry("pinned", "same.invalid");
        var sibling = Entry("sibling", "same.invalid", 8443);
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.Subscriptions = [new() { Url = "https://feed.invalid/pin", Servers = [pinned, sibling] }];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = CreateVm(store);
        vm.ConfirmSimpleServer(vm.SubscriptionServers[0]);

        vm.Subscriptions[0].UnderlyingEntry.Servers = [sibling];
        typeof(MainWindowViewModel).GetMethod("RebuildSubscriptionPool", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, null);
        Assert.Null(vm.SelectedSubscriptionServer);
        Assert.Equal(settings.App.Subscriptions[0].Url, vm.SmpInput);
        var starts = 0;
        vm.SimpleTunnelStart = (_, _, _) =>
        {
            starts++;
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };
        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Equal(0, starts);
        Assert.Equal(Strings.SmpSelectionUnavailable, vm.SmpErrorText);
        Assert.Equal(SubscriptionEntry.RowKey(pinned), store.Load(AppPaths.ConfigYamlPath).App.SubscriptionPreference?.RowKey);
    }

    [AvaloniaFact]
    public void UnsupportedGroupHasAnExplanationAndCannotBePinned()
    {
        var previous = SingBoxFeatures.OverrideXhttp;
        SingBoxFeatures.OverrideXhttp = false;
        try
        {
            var server = Entry("xhttp", "xhttp.invalid");
            server.Transport.Type = "xhttp";
            var settings = new AppSettings();
            settings.App.ConfigMode = "subscribe";
            settings.App.Subscriptions = [new() { Url = "https://feed.invalid/picker", Servers = [server] }];
            var store = new InMemorySettingsStore();
            store.Save(settings, AppPaths.ConfigYamlPath);
            using var vm = CreateVm(store);
            using var view = new OpenPage(vm, 360);
            vm.OpenConfigPickerCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var groups = FindOnPage<ItemsControl>(view.Page, "SimpleServerGroups")!;
            var group = Assert.IsType<SimpleServerGroup>(Assert.Single(groups.Items));
            Assert.False(group.CanAutoSelect);
            Assert.Equal(Strings.SmpServerUnsupported, group.SelectionHint);
            Assert.False(FindGroupButton(groups, ProtocolGroup.StealthWeb, disclosure: false).IsEffectivelyEnabled);
            Assert.True(FindGroupButton(groups, ProtocolGroup.StealthWeb, disclosure: true).IsEffectivelyEnabled);
            var before = store.SaveCount;
            vm.ConfirmSimpleServer(Assert.Single(group.Servers));
            Assert.Null(vm.SimpleServerPreference);
            Assert.Equal(Strings.SmpServerUnsupported, vm.SmpErrorText);
            Assert.Equal(before, store.SaveCount);
        }
        finally { SingBoxFeatures.OverrideXhttp = previous; }
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
            var search = FindOnPage<TextBox>(view.Page, "SimpleServerSearch")!;
            var check = view.Page.FindControl<ToggleButton>("SimpleCheckServers")!;
            var link = FindOnPage<ToggleButton>(view.Page, "SimpleLinkButton")!;
            var appRules = view.Page.FindControl<Button>("SimpleAppRules")!;
            Assert.Equal("Name, country or city", search.PlaceholderText);

            vm.SetLanguageRussianCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Название, страна или город", search.PlaceholderText);
            Assert.Equal("Поиск по списку", AutomationProperties.GetName(search));
            Assert.Equal("Проверить серверы", AutomationProperties.GetName(check));
            Assert.Equal("Ссылка подписки", AutomationProperties.GetName(link));
            Assert.Equal("Настроить приложения", appRules.Content);
        }
        finally { VPNRouter.App.Localization.Strings.Lang = previousLanguage; }
    }

    [AvaloniaFact]
    public void ManualServerPickerDisablesTheCrossHostAutoGroup()
    {
        var first = Entry("first", "first.invalid");
        var chosen = Entry("second", "second.invalid");
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.Vless.AutoSelectBestServer = true;
        settings.Vless.ActiveServer = first.Name;
        settings.Vless.Servers = [first, chosen];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = CreateVm(store);
        Assert.True(Generate(VpnEngine.CopySessionSettings(settings)).UsesAutoGroup);

        var page = new ServersPage { DataContext = vm };
        var window = new Window { Content = page, Width = 520, Height = 800 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var picker = page.FindControl<ServerPickerListBox>("ServerList")!;
            var firstItem = Assert.IsType<ListBoxItem>(picker.ContainerFromIndex(0));
            firstItem.Focus();
            var before = store.SaveCount;
            PressPickerKey(firstItem, Key.Down);
            Assert.Same(vm.Servers[1], picker.SelectedItem);
            Assert.True(vm.AutoSelectBestServer);
            Assert.Equal(before, store.SaveCount);

            var item = Assert.IsType<ListBoxItem>(picker.ContainerFromIndex(1));
            PressPickerKey(item, Key.Enter);
            Assert.False(vm.AutoSelectBestServer);
            Assert.Equal(before + 1, store.SaveCount);
            var saved = store.Load(AppPaths.ConfigYamlPath);
            Assert.False(saved.Vless.AutoSelectBestServer);
            var connection = Generate(VpnEngine.CopySessionSettings(saved));
            Assert.False(connection.UsesAutoGroup);
            Assert.Equal(chosen.Name, connection.Server.Name);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(360, false)]
    [InlineData(450, true)]
    public void SubscriptionPickersSharePolicyAndKeepTheRunningSession(int width, bool connected)
    {
        var language = VPNRouter.App.Localization.Strings.Lang;
        try
        {
            var daily = Entry("Швеция · Стокгольм", "daily.invalid");
            var dailySecond = Entry("Финляндия · Хельсинки", "second.invalid");
            var web = Entry("Нидерланды · Амстердам", "web.invalid");
            web.Transport.Type = "ws";
            var settings = new AppSettings();
            settings.App.Language = "ru";
            settings.App.ConfigMode = "subscribe";
            settings.App.Subscriptions =
                [new() { Url = "https://feed.invalid/categories", Servers = [daily, dailySecond, web] }];
            settings.Vless.AutoSelectBestServer = true;
            var store = new InMemorySettingsStore();
            store.Save(settings, AppPaths.ConfigYamlPath);
            using var vm = CreateVm(store);
            var simple = new SimplePage { DataContext = vm };
            var advanced = new SubscribePage { DataContext = vm };
            var window = new Window
            {
                Content = simple, Width = width, Height = 850,
                RequestedThemeVariant = connected ? ThemeVariant.Dark : ThemeVariant.Light,
            };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var header = simple.FindControl<ToggleButton>("SimpleChangeServer")!;
                var simpleGroups = FindOnPage<ItemsControl>(simple, "SimpleServerGroups")!;
                var link = FindOnPage<ToggleButton>(simple, "SimpleLinkButton")!;
                var advancedGroups = FindOnPage<ItemsControl>(advanced, "SimpleServerGroups")!;
                Assert.Empty(header.GetVisualDescendants().OfType<Button>());
                Assert.Equal(Strings.SmpExpandCategory, AutomationProperties.GetName(header));
                Assert.True(link.IsEffectivelyVisible);
                Assert.False(Shown(simpleGroups));
                var before = store.SaveCount;
                var initial = vm.SelectedSubscriptionServer;
                Click(window, simple.FindControl<TextBlock>("ConfigName")!);
                Assert.Equal(Strings.SmpCollapseCategory, AutomationProperties.GetName(header));
                Assert.True(simpleGroups.IsEffectivelyVisible);
                Assert.Equal(before, store.SaveCount);
                Assert.Same(initial, vm.SelectedSubscriptionServer);
                Assert.Null(vm.SimpleServerPreference);
                Toggle(simple, "SimpleLinkButton", true);
                Assert.True(simpleGroups.IsEffectivelyVisible);
                Toggle(simple, "SimpleLinkButton", false);

                FindGroupButton(simpleGroups, ProtocolGroup.Daily, disclosure: false)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                var automatic = vm.SimpleServerPreference;
                Assert.Equal(ProtocolGroup.Daily, automatic?.GetCategory());
                Assert.False(vm.AutoSelectBestServer);
                Assert.False(vm.IsConnecting);
                Assert.Equal(before + 1, store.SaveCount);
                Assert.Equal(Strings.SmpExpandCategory, AutomationProperties.GetName(header));
                Capture(window, $"simple-home-unified-auto-collapsed-{width}");
                Click(window, simple.FindControl<TextBlock>("ConfigName")!);
                Capture(window, $"simple-picker-unified-simple-auto-{width}");

                if (connected)
                {
                    Engine(vm).CommitTunnelInfo(Generate(VpnEngine.CopySessionSettings(store.Load(AppPaths.ConfigYamlPath))));
                    vm.IsConnected = true;
                }

                vm.ToggleUiModeCommand.Execute(null);
                Assert.False(vm.IsSimpleMode);
                window.Content = advanced;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(automatic, vm.SimpleServerPreference);
                Assert.True(Assert.Single(advancedGroups.Items.Cast<SimpleServerGroup>(), group => group.IsCurrent).CanAutoSelect);
                Assert.Equal("ws", Assert.Single(advancedGroups.Items.Cast<SimpleServerGroup>(),
                    group => group.Category == ProtocolGroup.WebFallback).Protocols);
                Assert.False(vm.IsConnecting);
                Assert.False(vm.HasPendingAppChanges);
                Capture(window, $"simple-picker-unified-advanced-auto-{width}");

                var unchanged = store.SaveCount;
                Toggle(advanced, "SimpleServerSearchButton", true);
                var search = FindOnPage<TextBox>(advanced, "SimpleServerSearch")!;
                search.Text = "Хельсинки";
                Dispatcher.UIThread.RunJobs();
                var results = FindOnPage<ServerPickerListBox>(advanced, "SimpleServerResults")!;
                Assert.Same(vm.SubscriptionServers[1], Assert.Single(results.Items));
                vm.SubscriptionServers[0].TestStatus = ServerProbeStatus.Ok;
                vm.SubscriptionServers[0].PingMs = 28;
                Assert.Equal(automatic, vm.SimpleServerPreference);
                Assert.Equal(2, automatic!.Candidates(store.Load(AppPaths.ConfigYamlPath).App.Subscriptions).Count());
                Assert.Same(initial, vm.SelectedSubscriptionServer);
                Assert.Equal(unchanged, store.SaveCount);
                PressPickerKey(search, Key.Escape);
                Dispatcher.UIThread.RunJobs();
                Assert.True(string.IsNullOrEmpty(search.Text));

                var dailyGroup = Assert.Single(advancedGroups.Items.Cast<SimpleServerGroup>(),
                    group => group.Category == ProtocolGroup.Daily);
                if (!dailyGroup.IsExpanded)
                    FindGroupButton(advancedGroups, dailyGroup.Category, disclosure: true)
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                var list = Assert.Single(advancedGroups.GetVisualDescendants().OfType<ServerPickerListBox>(),
                    control => control.IsEffectivelyVisible);
                var selectedRow = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(1));
                selectedRow.Focus();
                PressPickerKey(selectedRow, Key.Enter);
                Dispatcher.UIThread.RunJobs();
                Assert.Same(vm.SubscriptionServers[1], vm.SelectedSubscriptionServer);
                Assert.Equal(SubscriptionEntry.RowKey(dailySecond), vm.SimpleServerPreference?.RowKey);
                Assert.Equal(unchanged + 1, store.SaveCount);
                Assert.Equal(connected, vm.HasPendingAppChanges);
                Assert.False(vm.IsConnecting);
                Assert.Single(list.GetVisualDescendants().OfType<Image>(),
                    flag => flag.IsEffectivelyVisible && ReferenceEquals(flag.DataContext, vm.SelectedSubscriptionServer));
                if (connected)
                {
                    Assert.Contains("Применить", vm.StatusText);
                    Assert.Equal(daily.Name, vm.SmpConfigServer?.Name);
                }
                Capture(window, $"simple-picker-unified-advanced-manual-{width}");

                var pinned = vm.SimpleServerPreference;
                vm.ToggleUiModeCommand.Execute(null);
                window.Content = simple;
                Dispatcher.UIThread.RunJobs();
                Assert.True(vm.IsSimpleMode);
                Assert.Equal(pinned, vm.SimpleServerPreference);
                Assert.Equal(connected, vm.HasPendingAppChanges);
                Assert.False(vm.IsConnecting);
                Assert.Equal(unchanged + 2, store.SaveCount);
                if (!Shown(simpleGroups))
                    Click(window, simple.FindControl<TextBlock>("ConfigName")!);
                Assert.Contains(dailySecond.Name, FindOnPage<TextBlock>(simple, "PickerSelection")!.Text);
                Assert.Equal(connected ? $"{Strings.SmpCurrentServer} · {Strings.SmpKindSubscription}" : Strings.SmpPinnedServer,
                    BoundText(simple, "ConfigKind"));
                Capture(window, $"simple-picker-unified-simple-manual-{width}");

                FindGroupButton(simpleGroups, ProtocolGroup.Daily, disclosure: false)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(ProtocolGroup.Daily, vm.SimpleServerPreference?.GetCategory());
                Assert.False(vm.IsConnecting);
            }
            finally { window.Close(); }
        }
        finally { VPNRouter.App.Localization.Strings.Lang = language; }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpandedSubscriptionRowsUseTheHostsWheelScroll(bool advanced)
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        var rows = Enumerable.Range(1, 24).Select(i => Entry($"Test {i}", $"node-{i}.invalid")).ToList();
        var dnstt = Entry("Test backup", "backup.invalid");
        dnstt.Protocol = "dnstt";
        rows.Add(dnstt);
        settings.App.Subscriptions = [new() { Url = "https://feed.invalid/scroll", Servers = rows }];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = CreateVm(store);
        UserControl page = advanced ? new SubscribePage() : new SimplePage();
        page.DataContext = vm;
        var window = new Window { Content = page, Width = 360, Height = 680 };
        window.Show();
        try
        {
            if (advanced) vm.ToggleUiModeCommand.Execute(null);
            else vm.OpenConfigPickerCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var viewport = page.FindControl<ScrollViewer>("ServerPickerViewport")!;
            var groups = FindOnPage<ItemsControl>(page, "SimpleServerGroups")!;
            var emergency = Assert.Single(groups.Items.Cast<SimpleServerGroup>(), group => group.Category == ProtocolGroup.Emergency);
            Assert.Equal("dnstt", emergency.Protocols);
            FindGroupButton(groups, ProtocolGroup.Daily, disclosure: true).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            var list = Assert.Single(groups.GetVisualDescendants().OfType<ServerPickerListBox>(), Shown);
            Assert.Equal(24, list.Items.Count);
            Assert.True(viewport.Extent.Height > viewport.Viewport.Height);
            var initial = vm.SelectedSubscriptionServer;
            var saved = store.SaveCount;
            var first = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
            var point = first.TranslatePoint(new Point(first.Bounds.Width / 2, first.Bounds.Height / 2), window)!.Value;
            var previousOffset = viewport.Offset.Y;
            window.MouseWheel(point, new Vector(0, -1));
            Assert.True(viewport.Offset.Y > previousOffset);

            var lastIndex = list.Items.Count - 1;
            list.ScrollIntoView(lastIndex);
            Dispatcher.UIThread.RunJobs();
            var last = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(lastIndex));
            var lastPosition = last.TranslatePoint(new Point(0, last.Bounds.Height / 2), viewport)!.Value;
            Assert.InRange(lastPosition.Y, 0, viewport.Viewport.Height);
            Assert.Same(initial, vm.SelectedSubscriptionServer);
            Assert.Equal(saved, store.SaveCount);
            Capture(window, $"simple-picker-scroll-{(advanced ? "advanced" : "simple")}-360");
        }
        finally { window.Close(); }
    }

    private static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
        bitmap.Save(Path.Combine(ScreenshotHelper.ScreenshotsDir, $"{name}.png"), PngBitmapEncoderOptions.Default);
    }

    private static bool Shown(Control control) => control.IsAttachedToVisualTree() && control.IsEffectivelyVisible;

    private static T? FindOnPage<T>(UserControl page, string name) where T : Control =>
        page.FindControl<T>(name) ??
        page.FindControl<SubscriptionServerPicker>(page is SimplePage ? "SimplePicker" : "AdvancedPicker")?.FindControl<T>(name) ??
        page.FindControl<SubscriptionLinkEditor>(page is SimplePage ? "SimpleLink" : "AdvancedLink")?.FindControl<T>(name);

    private static void PressPickerKey(InputElement item, Key key) => item.RaiseEvent(new KeyEventArgs
    {
        Source = item,
        RoutedEvent = InputElement.KeyDownEvent,
        Key = key
    });

    private static void Toggle(UserControl page, string name, bool open)
    {
        var button = FindOnPage<ToggleButton>(page, name)!;
        button.IsChecked = open;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
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

        public OpenPage(MainWindowViewModel vm, int width = 520, ThemeVariant? theme = null)
        {
            Page = new SimplePage { DataContext = vm };
            _window = new Window { Content = Page, Width = width, Height = 800 };
            if (theme != null) _window.RequestedThemeVariant = theme;
            _window.Show();
        }

        public void Dispose() => _window.Close();

        public void Capture(string name) => SimpleConnectStatusTests.Capture(_window, name);
    }

    private static string BoundText(SimplePage page, string name)
    {
        Dispatcher.UIThread.RunJobs();
        return FindOnPage<TextBlock>(page, name)!.Text!;
    }

    private static void ReportStatus(VpnEngine engine, string message)
    {
        typeof(VpnEngine).GetMethod("OnStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, [message]);
        Dispatcher.UIThread.RunJobs();
    }
}
