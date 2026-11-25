using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using VPNRouter.App.ViewModels.Internals;
using VPNRouter.App.Views;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class BusinessConnectionRepairTests
{
    private sealed class Diagnostics : IBusinessDiagnostics
    {
        public int Collected { get; private set; }
        public List<string> Sent { get; } = [];
        public TaskCompletionSource<BusinessSupportReply>? Reply { get; set; }

        public Task<string> CollectAsync(CancellationToken ct) => Task.FromResult($"report-{++Collected}.zip");

        public Task SendAsync(string path, CancellationToken ct) => throw new NotSupportedException();

        public Task<BusinessSupportReply> SendWithReplyAsync(string path, Action acknowledged, CancellationToken ct)
        {
            Sent.Add(path);
            acknowledged();
            return Reply?.Task ?? Task.FromResult(new BusinessSupportReply("ready", "Проверьте другой сервер.", "Try a different server."));
        }
    }

    private static BusinessClientConfiguration SupportOnly() => BusinessClientConfiguration.Parse(
        """{"business":{"support":{"endpoint":"https://support.example.invalid/api/reports","token":"fixture"}}}""");

    [AvaloniaFact]
    public async Task OneClickRepairsLocallyAndOnlyAnExplicitSecondClickSendsAFreshReport()
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore(), null, null, null, businessBootstrap: (_, _) => null);
        var diagnostics = new Diagnostics();
        var starts = 0;
        BusinessServicesViewModel? services = null;
        var window = new BusinessWindow(static (_, _) => Task.FromResult(ServerProbeResult.Unknown), (model, changed) =>
            services = new(model, SupportOnly(), new BusinessClientHttp(), changed, diagnostics, ct =>
            {
                ct.ThrowIfCancellationRequested();
                starts++;
                return Task.FromResult(false);
            })) { DataContext = vm };
        window.Show();
        try
        {
            var repair = window.FindControl<StackPanel>("BusinessRepairActions")!;
            var open = Assert.IsType<Button>(repair.Children[0]);
            Assert.True(open.IsEffectivelyVisible);
            Assert.True(open.Command!.CanExecute(null));
            await services!.RunDiagnosticsAsync(false);
            open.Command.Execute(null);
            await services.OpenRepairCommand.ExecutionTask!;
            Assert.Equal(1, starts);
            Assert.True(services.ShowAnalyze);
            Assert.Empty(diagnostics.Sent);
            await services.AnalyzeRepairCommand.ExecuteAsync(null);
            Assert.Equal(new[] { "report-2.zip" }, diagnostics.Sent);
            Assert.True(services.ShowRepairAnalysis);
            Assert.Contains(Strings.Lang == "ru" ? "другой сервер" : "different server", services.DiagnosticStatus);
            Assert.Equal(1, starts);
            Assert.False(services.ShowAnalyze);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NarrowWindowKeepsRepairUnderConnect(bool expand)
    {
        var previousLanguage = Strings.Lang;
        using var vm = new MainWindowViewModel(new InMemorySettingsStore(), null, null, null, businessBootstrap: (_, _) => null);
        vm.SetLanguageRussianCommand.Execute(null);
        BusinessServicesViewModel? services = null;
        var window = new BusinessWindow(static (_, _) => Task.FromResult(ServerProbeResult.Unknown),
            (model, changed) => services = new(model, SupportOnly(), new BusinessClientHttp(), changed, new Diagnostics(),
                _ => Task.FromResult(false)))
            { Width = 360, DataContext = vm };
        window.Show();
        try
        {
            var repair = window.FindControl<StackPanel>("BusinessRepairActions")!;
            var siblings = Assert.IsType<StackPanel>(repair.Parent).Children;
            var before = Assert.IsType<Border>(siblings[siblings.IndexOf(repair) - 1]);
            Assert.Same(vm.ToggleConnectionCommand, Assert.IsType<Button>(before.Child).Command);
            if (expand) await services!.OpenRepairCommand.ExecuteAsync(null);
            window.UpdateLayout();
            var scroll = Assert.IsType<ScrollViewer>(window.Content);
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
            ScreenshotHelper.Capture(window, expand ? "business-repair-consent-360" : "business-repair-home-360");
        }
        finally { window.Close(); Strings.Lang = previousLanguage; }
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
        var pending = services.AnalyzeRepairCommand.ExecuteAsync(null);
        Assert.Single(diagnostics.Sent);
        if (!initiallyConnected) services.SetVisible(false);
        diagnostics.Reply.SetResult(BusinessSupportReply.Unavailable);
        await pending;
        Assert.False(services.OpenRepairCommand.CanExecute(null));
        Assert.Equal(initiallyConnected, services.IsRepairOpen);
        Assert.Equal(initiallyConnected, vm.IsConnected);
        if (initiallyConnected) Assert.Equal(Strings.BusinessDiagnosticsReplyUnavailable, services.DiagnosticStatus);
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
        await services.OpenRepairCommand.ExecuteAsync(null);
        Assert.False(services.ShowAnalyze);
        Assert.Equal(1, attempts);
        Assert.Equal(succeeds, vm.IsConnected);
        Assert.Equal(!succeeds, services.IsRepairOpen);
        Assert.Equal(succeeds ? string.Empty : Strings.BusinessRepairFailed, services.RepairHint);
        Assert.Equal(0, diagnostics.Collected);
    }
}
