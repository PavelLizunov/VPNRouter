using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VPNRouter.App.ViewModels;
using VPNRouter.App.ViewModels.Internals;
using VPNRouter.App.Views;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.Tests;

public sealed class BusinessServerHealthTests
{
    private const string ThreeNodes = BusinessDefaultsTests.MultiServerConnection +
        "\nvless://third-user@192.0.2.3:443?security=tls&type=tcp#Third%20office%20server";

    [AvaloniaTheory]
    [InlineData(ServerProbeStatus.Ok, 25, true, false, false)]
    [InlineData(ServerProbeStatus.Slow, 950, false, true, false)]
    [InlineData(ServerProbeStatus.Timeout, 0, false, false, true)]
    [InlineData(ServerProbeStatus.Unreachable, 0, false, false, true)]
    [InlineData(ServerProbeStatus.Implausible, 1, false, true, false)]
    [InlineData(ServerProbeStatus.Unknown, 0, false, false, false)]
    [InlineData(ServerProbeStatus.SkippedNotApplicable, 0, false, false, false)]
    public void Choice_MapsProbeWithoutExposingRawErrors(ServerProbeStatus status, int latency,
        bool good, bool warning, bool bad)
    {
        var choice = new BusinessServerChoiceViewModel(new ServerViewModel());
        choice.Update(new ServerProbeResult(status, latency, "private-host-and-error"));
        Assert.Equal(good, choice.IsGood);
        Assert.Equal(warning, choice.IsWarning);
        Assert.Equal(bad, choice.IsBad);
        Assert.DoesNotContain("private-host", choice.PingText + choice.StatusText);
        if (good || status == ServerProbeStatus.Slow) Assert.Equal(Strings.BusinessServerLatency(latency), choice.PingText);
        if (status == ServerProbeStatus.Implausible) Assert.Equal(Strings.BusinessServerInconclusive, choice.PingText);

        choice.Update(ServerProbeResult.Unknown, checking: true);
        Assert.False(choice.IsGood || choice.IsWarning || choice.IsBad);
        Assert.Equal(Strings.BusinessServerChecking, choice.PingText);
    }

    [AvaloniaFact]
    public void Choice_SubMillisecondIsMeasured_NotMissing()
    {
        var choice = new BusinessServerChoiceViewModel(new ServerViewModel());
        choice.Update(new ServerProbeResult(ServerProbeStatus.Ok, 0, null));
        Assert.Equal(Strings.BusinessServerSubMillisecond, choice.PingText);
        Assert.True(choice.IsGood);
    }

    [AvaloniaFact]
    public async Task Refresh_OnlyVisibleDisconnectedNodes_NoOverlap_NoSettingsMutation()
    {
        using var vm = CreateVm(out var store);
        var gate = NewResult();
        var tokens = new List<CancellationToken>();
        using var health = new BusinessServerHealthMonitor(vm, (_, ct) => { tokens.Add(ct); return gate.Task; });
        var saves = store.SaveCount;
        var selected = vm.SelectedServer;
        await health.RefreshAsync();
        Assert.Empty(tokens);

        health.SetVisible(true);
        var first = health.RefreshAsync();
        Assert.Same(first, health.RefreshAsync());
        Assert.Equal(3, tokens.Count);
        Assert.All(health.Choices, c => Assert.Equal(Strings.BusinessServerChecking, c.PingText));
        gate.SetResult(new ServerProbeResult(ServerProbeStatus.Ok, 45, "not shown"));
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(health.Choices, c => Assert.True(c.IsGood));
        Assert.Same(selected, vm.SelectedServer);
        Assert.Equal(saves, store.SaveCount);
        Assert.All(vm.Servers, s => Assert.Equal(ServerProbeStatus.Unknown, s.TestStatus));

        health.SetVisible(false);
        await health.RefreshAsync();
        Assert.Equal(3, tokens.Count);
        Assert.All(health.Choices, c => Assert.Equal(Strings.BusinessServerNotChecked, c.PingText));
    }

    [AvaloniaFact]
    public async Task ConnectingThenDisconnecting_CancelsAndDiscardsLateResultsBeforeFreshRound()
    {
        using var vm = CreateVm(out _);
        vm.IsSplitTunnel = false;
        var firstGate = NewResult();
        var nextGate = NewResult();
        var tokens = new List<CancellationToken>();
        using var health = new BusinessServerHealthMonitor(vm, (_, ct) =>
        {
            tokens.Add(ct);
            return tokens.Count <= 3 ? firstGate.Task : nextGate.Task;
        });
        health.SetVisible(true);
        var first = health.RefreshAsync();
        vm.IsConnecting = true;
        Assert.All(tokens, ct => Assert.True(ct.IsCancellationRequested));
        Assert.All(health.Choices, c => Assert.Equal(Strings.BusinessServerNotChecked, c.PingText));
        vm.IsConnected = true;
        vm.IsConnecting = false;
        await health.RefreshAsync();
        Assert.Equal(3, tokens.Count);
        vm.IsConnected = false;
        Assert.Equal(3, tokens.Count); // Cancelled work still owns the single round.
        firstGate.SetResult(new ServerProbeResult(ServerProbeStatus.Ok, 1, null));
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(6, tokens.Count);
        Assert.All(health.Choices, c => Assert.False(c.IsGood));
        var next = health.RefreshAsync();
        nextGate.SetResult(new ServerProbeResult(ServerProbeStatus.Slow, 950, null));
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(health.Choices, c => Assert.True(c.IsWarning));
    }

    [AvaloniaFact]
    public async Task Dispose_UnsubscribesAndIgnoresUncooperativeCompletion()
    {
        using var vm = CreateVm(out _);
        var gate = NewResult();
        var tokens = new List<CancellationToken>();
        var health = new BusinessServerHealthMonitor(vm, (_, ct) => { tokens.Add(ct); return gate.Task; });
        health.SetVisible(true);
        var round = health.RefreshAsync();
        health.Dispose();
        health.Dispose();
        Assert.All(tokens, ct => Assert.True(ct.IsCancellationRequested));
        vm.IsConnecting = true;
        vm.IsConnecting = false;
        health.SetVisible(true);
        gate.SetResult(new ServerProbeResult(ServerProbeStatus.Ok, 10, null));
        await round.WaitAsync(TimeSpan.FromSeconds(5));
        await health.RefreshAsync();
        Assert.Equal(3, tokens.Count);
        Assert.All(health.Choices, c => Assert.Equal(Strings.BusinessServerNotChecked, c.PingText));
    }

    [AvaloniaFact]
    public async Task FailedProbe_IsGenericAndDoesNotStopSiblingChecks()
    {
        using var vm = CreateVm(out _);
        var gate = NewResult();
        using var health = new BusinessServerHealthMonitor(vm, (server, _) => server == vm.Servers[0]
            ? Task.FromException<ServerProbeResult>(new IOException("private endpoint")) : gate.Task);
        health.SetVisible(true);
        var round = health.RefreshAsync();
        gate.SetResult(new ServerProbeResult(ServerProbeStatus.Ok, 22, null));
        await round.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(health.Choices[0].IsBad);
        Assert.Equal(Strings.BusinessServerNoReply, health.Choices[0].PingText);
        Assert.All(health.Choices.Skip(1), c => Assert.True(c.IsGood));
    }

    [AvaloniaFact]
    public async Task Window_ContextSwapHideAndClose_CancelOwnedProbes()
    {
        using var firstVm = CreateVm(out _);
        using var nextVm = CreateVm(out _);
        var tokens = new List<CancellationToken>();
        var gate = NewResult();
        var window = new BusinessWindow((_, ct) =>
        {
            tokens.Add(ct);
            return gate.Task;
        }) { DataContext = firstVm };
        window.Show();
        Task? lastRound = null;
        try
        {
            Assert.Equal(3, tokens.Count);
            var firstRound = window.RefreshBusinessServersAsync();
            window.DataContext = nextVm;
            Assert.All(tokens.Take(3), ct => Assert.True(ct.IsCancellationRequested));
            Assert.Equal(6, tokens.Count);
            var nextRound = window.RefreshBusinessServersAsync();
            window.Hide();
            Assert.All(tokens, ct => Assert.True(ct.IsCancellationRequested));
            firstVm.IsConnecting = true;
            firstVm.IsConnecting = false;
            Assert.Equal(6, tokens.Count);
            gate.SetResult(new ServerProbeResult(ServerProbeStatus.Ok, 12, null));
            await Task.WhenAll(firstRound, nextRound).WaitAsync(TimeSpan.FromSeconds(5));
            gate = NewResult();
            window.Show();
            Assert.Equal(9, tokens.Count);
            lastRound = window.RefreshBusinessServersAsync();
        }
        finally { window.Close(); }
        Assert.All(tokens, ct => Assert.True(ct.IsCancellationRequested));
        gate.TrySetResult(new ServerProbeResult(ServerProbeStatus.Ok, 12, null));
        if (lastRound != null) await lastRound.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [AvaloniaFact]
    public async Task Window_RefreshButtonProbesWithoutSavingOrSelecting_AndHonorsBusyState()
    {
        using var vm = CreateVm(out var store);
        var gate = NewResult();
        var calls = 0;
        var window = new BusinessWindow((_, _) => { calls++; return gate.Task; }) { DataContext = vm };
        window.Show();
        try
        {
            var initial = window.RefreshBusinessServersAsync();
            gate.SetResult(new ServerProbeResult(ServerProbeStatus.Ok, 15, null));
            await initial.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, calls);
            var saves = store.SaveCount;
            var selected = vm.SelectedServer;
            gate = NewResult();
            var refresh = RefreshButton(window);
            refresh.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(6, calls);
            var refreshed = window.RefreshBusinessServersAsync();
            gate.SetResult(new ServerProbeResult(ServerProbeStatus.Ok, 25, null));
            await refreshed.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(saves, store.SaveCount);
            Assert.Same(selected, vm.SelectedServer);
            vm.IsConnecting = true;
            refresh.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(6, calls);
            Assert.False(refresh.IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(340, 0, "good", "SuccessSolidBrush")]
    [InlineData(420, 0, "good", "SuccessSolidBrush")]
    [InlineData(340, 1, "slow", "WarningSolidBrush")]
    [InlineData(420, 1, "slow", "WarningSolidBrush")]
    [InlineData(340, 2, "offline", "DangerSolidBrush")]
    [InlineData(420, 2, "offline", "DangerSolidBrush")]
    public void Window_ThreeColorsAndLatency_AreBoundAndFitNarrowLayout(int width, int selected, string label, string brushKey)
    {
        using var vm = CreateVm(out _);
        vm.StatusText = "VPNRouter Business";
        var window = new BusinessWindow((server, _) => Task.FromResult(server == vm.Servers[0]
            ? new ServerProbeResult(ServerProbeStatus.Ok, 22, null)
            : server == vm.Servers[1] ? new ServerProbeResult(ServerProbeStatus.Slow, 950, null)
            : new ServerProbeResult(ServerProbeStatus.Timeout, 0, "private error"))) { DataContext = vm, Width = width };
        window.Show();
        try
        {
            var selector = window.FindControl<ComboBox>("BusinessServerSelector")!;
            selector.SelectedIndex = selected;
            Dispatcher.UIThread.RunJobs();
            var choices = selector.Items.Cast<BusinessServerChoiceViewModel>().ToArray();
            Assert.True(choices[0].IsGood);
            Assert.True(choices[1].IsWarning);
            Assert.True(choices[2].IsBad);
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains(choices[selected].PingText, texts);
            Assert.DoesNotContain(texts, t => t?.Contains("192.0.2.") == true || t?.Contains("private error") == true);
            var dot = Assert.Single(window.GetVisualDescendants().OfType<Ellipse>().Where(e => e.Classes.Contains("business-health-dot")));
            Assert.True(window.TryFindResource(brushKey, window.ActualThemeVariant, out var brush));
            Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color, Assert.IsAssignableFrom<ISolidColorBrush>(dot.Fill).Color);
            var scroll = Assert.IsType<ScrollViewer>(window.Content);
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
            var refresh = RefreshButton(window);
            vm.IsConnecting = true;
            Assert.False(refresh.IsEffectivelyEnabled);
            Assert.All(choices, c => Assert.False(c.IsGood || c.IsWarning || c.IsBad));
            vm.IsConnecting = false;
            OpenMenu(window).Hide();
            ScreenshotHelper.Capture(window, $"business-health-{label}-{width}");
        }
        finally { window.Close(); }
    }

    private static TaskCompletionSource<ServerProbeResult> NewResult() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Button RefreshButton(BusinessWindow window)
    {
        OpenMenu(window);
        return window.FindControl<Button>("BusinessServerRefresh")!;
    }

    private static Flyout OpenMenu(BusinessWindow window)
    {
        var button = window.FindControl<Button>("BusinessMenuButton")!;
        var flyout = Assert.IsType<Flyout>(button.Flyout);
        if (!flyout.IsOpen) flyout.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return flyout;
    }

    private static MainWindowViewModel CreateVm(out InMemorySettingsStore store)
    {
        store = new InMemorySettingsStore();
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, ThreeNodes, App.Services.BusinessBootstrap.ReadRules());
        settings.App.Language = "ru";
        store.Save(settings, AppPaths.ConfigYamlPath);
        return new MainWindowViewModel(store);
    }
}
