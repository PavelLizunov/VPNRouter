using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
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
public sealed class BusinessConnectionRepairTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private sealed class Diagnostics : IBusinessDiagnostics
    {
        public int Collected { get; private set; }
        public List<string> Sent { get; } = [];
        public TaskCompletionSource<BusinessSupportReply>? Reply { get; set; }
        public bool RejectUpload { get; set; }
        public bool CancelPendingReply { get; set; }
        public Action? BeforeAcknowledgement { get; set; }

        public Task<string> CollectAsync(CancellationToken ct) => Task.FromResult($"report-{++Collected}.zip");

        public Task SendAsync(string path, CancellationToken ct) => throw new NotSupportedException();

        public Task<BusinessSupportReply> SendWithReplyAsync(string path, Action acknowledged, CancellationToken ct)
        {
            Sent.Add(path);
            if (RejectUpload) return Task.FromException<BusinessSupportReply>(new IOException("Synthetic upload rejection."));
            BeforeAcknowledgement?.Invoke();
            acknowledged();
            if (Reply is { } reply)
                return CancelPendingReply ? reply.Task.WaitAsync(ct) : reply.Task;
            return Task.FromResult(new BusinessSupportReply("ready", "Проверьте другой сервер.", "Try a different server."));
        }
    }

    private static BusinessClientConfiguration SupportOnly() => BusinessClientConfiguration.Parse(
        """{"business":{"support":{"endpoint":"https://support.example.invalid/api/reports","token":"fixture"}}}""");

    [AvaloniaFact]
    public async Task OneClickRepairsLocallyAndOnlyAnExplicitSecondClickSendsAFreshReport()
    {
        using var fixture = new RepairWindowFixture();
        await fixture.Services.RunDiagnosticsAsync(false);
        await ClickAsync(fixture.Window, FindButton(fixture.Repair, "BusinessRepairOpen"));
        Assert.Equal(1, fixture.LocalAttempts);
        Assert.True(fixture.Services.HasRepairPrimaryAction);
        Assert.Empty(fixture.Diagnostics.Sent);

        await ClickAsync(fixture.Window, FindButton(fixture.Repair, "BusinessRepairAnalyze"));

        Assert.Equal(new[] { "report-2.zip" }, fixture.Diagnostics.Sent);
        Assert.True(fixture.Services.IsRepairOpen);
        Assert.Contains("другой сервер", fixture.Services.DiagnosticStatus);
        Assert.Equal(1, fixture.LocalAttempts);
        Assert.False(fixture.Services.HasRepairPrimaryAction);
        var advice = fixture.Services.RepairBody;
        fixture.Window.OpenServerPanel();
        var selector = fixture.Window.FindControl<ComboBox>("BusinessServerSelector")!;
        selector.SelectedIndex = 1;
        Assert.Equal(BusinessDefaultsTests.ReserveName, fixture.Vm.SelectedServer?.Name);
        Assert.True(fixture.Services.IsRepairOpen);
        Assert.Equal(advice, fixture.Services.RepairBody);
        Click(fixture.Window, fixture.Window.FindControl<Button>("BusinessServerMenuButton")!);
        ScreenshotHelper.Capture(fixture.Window, "business-repair-advice-360");
    }

    [AvaloniaFact]
    public async Task MenuDiagnostics_OpensVisibleConsentBeforeCollectingOrUploading()
    {
        using var fixture = new RepairWindowFixture();
        var menu = fixture.Window.FindControl<Button>("BusinessMenuButton")!;
        Click(fixture.Window, menu);
        var flyout = Assert.IsType<Flyout>(menu.Flyout);
        Assert.True(flyout.IsOpen);
        Click(fixture.Window, FindButton(Assert.IsAssignableFrom<Control>(flyout.Content), "BusinessDiagnosticsSend"));

        Assert.False(flyout.IsOpen);
        Assert.True(fixture.Services.IsRepairOpen);
        Assert.Contains(fixture.Repair.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.IsEffectivelyVisible && text.Text == Strings.BusinessRepairConsentTitle);
        Assert.Contains(fixture.Repair.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.IsEffectivelyVisible && text.Text == Strings.BusinessRepairConsent);
        Assert.Equal(0, fixture.LocalAttempts);
        Assert.Equal(0, fixture.Diagnostics.Collected);
        Assert.Empty(fixture.Diagnostics.Sent);

        await ClickAsync(fixture.Window, FindButton(fixture.Repair, "BusinessRepairAnalyze"));

        Assert.Equal(0, fixture.LocalAttempts);
        Assert.Equal(1, fixture.Diagnostics.Collected);
        Assert.Equal(new[] { "report-1.zip" }, fixture.Diagnostics.Sent);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedRepairUpload_OffersOneClickRetryWithoutCollectingOrSearchingAgain(bool retry)
    {
        using var fixture = new RepairWindowFixture();
        fixture.Diagnostics.RejectUpload = true;
        await ClickAsync(fixture.Window, FindButton(fixture.Repair, "BusinessRepairOpen"));
        await ClickAsync(fixture.Window, FindButton(fixture.Repair, "BusinessRepairAnalyze"));

        Assert.Equal(1, fixture.LocalAttempts);
        Assert.Equal(1, fixture.Diagnostics.Collected);
        Assert.Equal(new[] { "report-1.zip" }, fixture.Diagnostics.Sent);
        Assert.True(fixture.Services.IsRepairError);
        Assert.Equal(Strings.BusinessRepairRetrySend, fixture.Services.RepairActionText);
        var retryButton = FindButton(fixture.Repair, "BusinessRepairAnalyze");
        Assert.True(retryButton.IsEffectivelyVisible);
        Assert.True(retryButton.IsEffectivelyEnabled);
        if (!retry)
        {
            ScreenshotHelper.Capture(fixture.Window, "business-repair-retry-360");
            return;
        }

        fixture.Diagnostics.RejectUpload = false;
        await ClickAsync(fixture.Window, retryButton);

        Assert.Equal(1, fixture.LocalAttempts);
        Assert.Equal(1, fixture.Diagnostics.Collected);
        Assert.Equal(new[] { "report-1.zip", "report-1.zip" }, fixture.Diagnostics.Sent);
        Assert.False(fixture.Services.HasRepairPrimaryAction);
        Assert.False(fixture.Services.IsRepairError);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcknowledgedRepairReport_DistinguishesReceiptAndAnalysisAndKeepsReceiptWhenWaitStops(bool stopWaiting)
    {
        using var fixture = new RepairWindowFixture(initiallyConnected: true);
        fixture.Diagnostics.Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Diagnostics.CancelPendingReply = true;
        await ClickAsync(fixture.Window, FindButton(fixture.Repair, "BusinessRepairOpen"));
        Assert.Equal(Strings.BusinessRepairConsentOnline, fixture.Services.RepairBody);
        Assert.Empty(fixture.Diagnostics.Sent);
        var sending = ClickAsync(fixture.Window, FindButton(fixture.Repair, "BusinessRepairAnalyze"));
        try
        {
            Assert.False(sending.IsCompleted);
            Assert.Equal(Strings.BusinessRepairReceiptTitle, fixture.Services.RepairHeading);
            Assert.Equal(Strings.BusinessDiagnosticsReplyPending, fixture.Services.DiagnosticStatus);
            Assert.Equal(Strings.BusinessRepairStopWaiting, fixture.Services.RepairDismissText);
            Assert.False(fixture.Services.HasRepairPrimaryAction);
            Assert.Equal(0, fixture.LocalAttempts);
            Assert.True(fixture.Vm.IsConnected);

            if (!stopWaiting)
            {
                ScreenshotHelper.Capture(fixture.Window, "business-repair-receipt-pending-360");
                return;
            }

            Click(fixture.Window, FindButton(fixture.Repair, "BusinessRepairDismiss"));
            await sending;

            Assert.True(fixture.Services.IsRepairOpen);
            Assert.False(fixture.Services.IsRepairBusy);
            Assert.Equal(Strings.BusinessRepairReceiptTitle, fixture.Services.RepairHeading);
            Assert.Equal(Strings.BusinessDiagnosticsReplyWaitCancelled, fixture.Services.DiagnosticStatus);
            Assert.Equal(new[] { "report-1.zip" }, fixture.Diagnostics.Sent);
            Assert.True(fixture.Vm.IsConnected);
            Assert.False(FindButton(fixture.Repair, "BusinessRepairDismiss").IsEffectivelyVisible);
            Click(fixture.Window, FindButton(fixture.Repair, "BusinessRepairClose"));
            Assert.False(fixture.Services.IsRepairOpen);
            Assert.True(fixture.Services.HasDeliveredReceiptBadge);
            Click(fixture.Window, FindButton(fixture.Repair, "BusinessRepairReceiptBadge"));
            Assert.True(fixture.Services.IsRepairOpen);
            Assert.True(fixture.Services.IsRepairReceipt);
        }
        finally
        {
            fixture.Diagnostics.Reply.TrySetResult(BusinessSupportReply.Unavailable);
            await sending;
        }
    }

    [AvaloniaFact]
    public async Task CancelDuringAcknowledgementHandoff_KeepsReceipt()
    {
        using var fixture = new RepairWindowFixture(initiallyConnected: true);
        fixture.Diagnostics.Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Diagnostics.CancelPendingReply = true;
        fixture.Diagnostics.BeforeAcknowledgement = () => fixture.Services.CloseRepairCommand.Execute(null);
        await ClickAsync(fixture.Window, FindButton(fixture.Repair, "BusinessRepairOpen"));

        await ClickAsync(fixture.Window, FindButton(fixture.Repair, "BusinessRepairAnalyze"));

        Assert.True(fixture.Services.IsRepairOpen);
        Assert.True(fixture.Services.IsRepairReceipt);
        Assert.False(fixture.Services.IsRepairBusy);
        Assert.Equal(Strings.BusinessRepairReceiptWaitCancelled, fixture.Services.RepairBody);
        Assert.Equal(new[] { "report-1.zip" }, fixture.Diagnostics.Sent);
        ScreenshotHelper.Capture(fixture.Window, "business-repair-receipt-stopped-360");
    }

    [AvaloniaFact]
    public async Task FailedLocalSearch_CanSearchAgainWithoutCollecting()
    {
        using var fixture = new RepairWindowFixture();
        await ClickAsync(fixture.Window, FindButton(fixture.Repair, "BusinessRepairOpen"));

        await ClickAsync(fixture.Window, FindButton(fixture.Repair, "BusinessRepairRetrySearch"));

        Assert.Equal(2, fixture.LocalAttempts);
        Assert.True(fixture.Services.IsRepairError);
        Assert.True(fixture.Services.HasRepairPrimaryAction);
        Assert.Equal(0, fixture.Diagnostics.Collected);
        Assert.Empty(fixture.Diagnostics.Sent);
    }

    [AvaloniaFact]
    public async Task LocalSearch_ShowsBusyFeedbackBeforeAnyDiagnosticCollection()
    {
        var searchResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new RepairWindowFixture(connect: ct => searchResult.Task.WaitAsync(ct));
        var searching = ClickAsync(fixture.Window, FindButton(fixture.Repair, "BusinessRepairOpen"));
        try
        {
            Assert.False(searching.IsCompleted);
            Assert.Equal(Strings.BusinessRepairChecking, fixture.Services.RepairHeading);
            Assert.True(fixture.Services.IsRepairBusy);
            Assert.False(fixture.Services.HasRepairPrimaryAction);
            Assert.Contains(fixture.Repair.GetVisualDescendants().OfType<ProgressBar>(),
                progress => progress.IsEffectivelyVisible && progress.IsIndeterminate);
            Assert.True(FindButton(fixture.Repair, "BusinessRepairDismiss").IsEffectivelyEnabled);
            Assert.False(FindButton(fixture.Repair, "BusinessRepairClose").IsEffectivelyVisible);
            Assert.Equal(1, fixture.LocalAttempts);
            Assert.Equal(0, fixture.Diagnostics.Collected);
            Assert.Empty(fixture.Diagnostics.Sent);
            ScreenshotHelper.Capture(fixture.Window, "business-repair-search-360");
        }
        finally
        {
            searchResult.TrySetResult(false);
            await searching;
        }
    }

    private static Button FindButton(Control root, string automationId) => Assert.Single(
        root.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == automationId);

    private static void Click(BusinessWindow window, Button button)
    {
        Assert.True(button.IsEffectivelyEnabled);
        Assert.True(button.IsEffectivelyVisible);
        new ButtonAutomationPeer(button).Invoke();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static Task ClickAsync(BusinessWindow window, Button button)
    {
        var command = Assert.IsAssignableFrom<IAsyncRelayCommand>(button.Command);
        Click(window, button);
        return Assert.IsAssignableFrom<Task>(command.ExecutionTask).WaitAsync(TestTimeout);
    }

    private sealed class RepairWindowFixture : IDisposable
    {
        private readonly string _previousLanguage = Strings.Lang;

        internal readonly MainWindowViewModel Vm;
        internal readonly Diagnostics Diagnostics = new();
        internal readonly BusinessWindow Window;
        internal readonly BusinessServicesViewModel Services;
        internal StackPanel Repair => Window.FindControl<StackPanel>("BusinessRepairActions")!;
        internal int LocalAttempts { get; private set; }

        internal RepairWindowFixture(bool initiallyConnected = false, Func<CancellationToken, Task<bool>>? connect = null)
        {
            var store = new InMemorySettingsStore();
            var settings = new AppSettings();
            BusinessDefaults.Apply(settings, BusinessDefaultsTests.MultiServerConnection, BusinessBootstrap.ReadRules());
            settings.App.Language = "ru";
            store.Save(settings, AppPaths.ConfigYamlPath);
            Vm = new(store, null, null, null, businessBootstrap: (_, _) => null);
            Vm.IsConnected = initiallyConnected;
            if (initiallyConnected) Vm.StatusText = Strings.Connected("split", null, null);
            BusinessServicesViewModel? services = null;
            Window = new BusinessWindow(static (_, _) => Task.FromResult(ServerProbeResult.Unknown), (model, changed) =>
                services = new(model, SupportOnly(), new BusinessClientHttp(), changed, Diagnostics, ct =>
                {
                    LocalAttempts++;
                    return connect?.Invoke(ct) ?? Task.FromResult(false);
                }))
            {
                Width = 360,
                DataContext = Vm
            };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Services = services!;
        }

        public void Dispose()
        {
            Window.Close();
            Vm.Dispose();
            Strings.Lang = _previousLanguage;
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false, "business-repair-home-360")]
    [InlineData(false, true, "business-repair-consent-360")]
    [InlineData(true, false, "business-repair-home-connected-360")]
    [InlineData(true, true, "business-repair-consent-connected-360")]
    public async Task NarrowWindowKeepsRepairUnderConnect(bool initiallyConnected, bool expand, string screenshot)
    {
        using var fixture = new RepairWindowFixture(initiallyConnected);
        var siblings = Assert.IsType<StackPanel>(fixture.Repair.Parent).Children;
        var before = Assert.IsType<Border>(siblings[siblings.IndexOf(fixture.Repair) - 1]);
        Assert.Same(fixture.Vm.ToggleConnectionCommand, Assert.IsType<Button>(before.Child).Command);
        if (expand) await ClickAsync(fixture.Window, FindButton(fixture.Repair, "BusinessRepairOpen"));
        Assert.Equal(expand, fixture.Services.IsRepairOpen);
        var scroll = Assert.IsType<ScrollViewer>(fixture.Window.Content);
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
        ScreenshotHelper.Capture(fixture.Window, screenshot);
    }

    [AvaloniaTheory]
    [InlineData(true, "business-repair-home-server-360")]
    [InlineData(false, "business-repair-home-mode-360")]
    public void NarrowHomeSettings_ExpandInlineWithoutHorizontalOverflow(bool serverPanel, string screenshot)
    {
        using var fixture = new RepairWindowFixture();
        if (serverPanel)
            fixture.Window.OpenServerPanel();
        else
            fixture.Window.OpenModePanel();

        var name = serverPanel ? "BusinessServerPanel" : "BusinessModePanel";
        var panel = fixture.Window.FindControl<Border>(name)!;
        panel.BringIntoView();
        fixture.Window.UpdateLayout();
        var scroll = Assert.IsType<ScrollViewer>(fixture.Window.Content);
        Assert.True(panel.IsEffectivelyVisible);
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
        ScreenshotHelper.Capture(fixture.Window, screenshot);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActiveTunnelOrLateAnalysisCannotStartARepair(bool initiallyConnected)
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore(), null, null, null, businessBootstrap: (_, _) => null);
        vm.IsConnected = initiallyConnected;
        var diagnostics = new Diagnostics { Reply = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var localAttempts = 0;
        using var services = new BusinessServicesViewModel(vm, SupportOnly(), new BusinessClientHttp(), () => { },
            diagnostics, _ => { localAttempts++; return Task.FromResult(false); });
        services.SetVisible(true);
        await services.OpenRepairCommand.ExecuteAsync(null);
        Assert.Equal(initiallyConnected ? 0 : 1, localAttempts);
        var pending = services.RepairPrimaryCommand.ExecuteAsync(null);
        Assert.Single(diagnostics.Sent);
        if (!initiallyConnected) services.SetVisible(false);
        diagnostics.Reply.SetResult(BusinessSupportReply.Unavailable);
        await pending;
        Assert.False(services.OpenRepairCommand.CanExecute(null));
        Assert.True(services.IsRepairOpen);
        Assert.True(services.IsRepairReceipt);
        Assert.Equal(initiallyConnected, vm.IsConnected);
        if (initiallyConnected) Assert.Equal(Strings.BusinessDiagnosticsReplyUnavailable, services.DiagnosticStatus);
    }

    [AvaloniaFact]
    public async Task HidingWindowPreservesStickyAdviceAndClosesTransientConsent()
    {
        using var fixture = new RepairWindowFixture(initiallyConnected: true);
        await ClickAsync(fixture.Window, FindButton(fixture.Repair, "BusinessRepairOpen"));
        Assert.True(fixture.Services.IsRepairOpen);
        fixture.Services.SetVisible(false);
        Assert.False(fixture.Services.IsRepairOpen);

        fixture.Services.SetVisible(true);
        await ClickAsync(fixture.Window, FindButton(fixture.Repair, "BusinessRepairOpen"));
        await ClickAsync(fixture.Window, FindButton(fixture.Repair, "BusinessRepairAnalyze"));
        Assert.True(fixture.Services.HasRepairAdvice);

        fixture.Services.SetVisible(false);
        Assert.True(fixture.Services.IsRepairOpen);
        Assert.True(fixture.Services.HasRepairAdvice);
        fixture.Services.SetVisible(true);

        Click(fixture.Window, FindButton(fixture.Repair, "BusinessRepairClose"));
        Assert.False(fixture.Services.IsRepairOpen);
        Assert.True(fixture.Services.HasDeliveredReceiptBadge);
        Click(fixture.Window, FindButton(fixture.Repair, "BusinessRepairReceiptBadge"));
        Assert.True(fixture.Services.HasRepairAdvice);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalRepairIsOneClickWithoutAnAnalysisEndpoint(bool succeeds)
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore(), null, null, null, businessBootstrap: (_, _) => null);
        var diagnostics = new Diagnostics();
        var attempts = 0;
        using var services = new BusinessServicesViewModel(vm, BusinessClientConfiguration.Empty,
            new BusinessClientHttp(), () => { }, diagnostics, _ =>
            {
                attempts++;
                vm.IsConnected = succeeds;
                return Task.FromResult(succeeds);
            });
        services.SetVisible(true);
        Assert.True(services.IsRefreshStatusVisible);
        Assert.Equal(Strings.BusinessSubscriptionNotConfigured, services.RefreshStatus);
        await services.OpenRepairCommand.ExecuteAsync(null);
        Assert.Equal(1, attempts);
        Assert.Equal(succeeds, vm.IsConnected);
        Assert.Equal(!succeeds, services.IsRepairOpen);
        if (!succeeds) Assert.Equal(Strings.BusinessRepairFailed, services.RepairHeading);
        Assert.Equal(0, diagnostics.Collected);
        Assert.Empty(diagnostics.Sent);
    }
}
