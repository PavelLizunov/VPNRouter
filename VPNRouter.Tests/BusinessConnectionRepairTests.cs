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
    public async Task HomeButtonOpensConsentAndFreshAnalysisNeverConnectsUntilConfirmed()
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        var diagnostics = new Diagnostics();
        var starts = 0;
        BusinessServicesViewModel? services = null;
        var window = new BusinessWindow(static (_, _) => Task.FromResult(ServerProbeResult.Unknown), (model, changed) =>
            services = new(model, SupportOnly(), new BusinessClientHttp(), changed, diagnostics, ct =>
            {
                ct.ThrowIfCancellationRequested();
                starts++;
                model.IsConnected = true;
                return Task.FromResult(true);
            })) { DataContext = vm };
        window.Show();
        try
        {
            var repair = window.FindControl<StackPanel>("BusinessRepairActions")!;
            var open = Assert.IsType<Button>(repair.Children[0]);
            Assert.True(open.IsEffectivelyVisible);
            Assert.True(open.Command!.CanExecute(null));
            open.Command.Execute(null);
            Assert.True(services!.ShowAnalyze);
            Assert.Empty(diagnostics.Sent);
            services.CloseRepairCommand.Execute(null);
            await services.RunDiagnosticsAsync(false);
            services.OpenRepairCommand.Execute(null);
            await services.AnalyzeRepairCommand.ExecuteAsync(null);
            Assert.Equal(new[] { "report-2.zip" }, diagnostics.Sent);
            Assert.True(services.ShowRepairAnalysis);
            Assert.Contains(Strings.Lang == "ru" ? "другой сервер" : "different server", services.DiagnosticStatus);
            Assert.Equal(0, starts);
            await services.ConnectRepairCommand.ExecuteAsync(null);
            Assert.Equal(1, starts);
            Assert.Equal(Strings.BusinessRepairConnected, services.RepairHint);
            Assert.False(services.ConnectRepairCommand.CanExecute(null));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NarrowWindowKeepsRepairUnderConnect(bool expand)
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        var window = new BusinessWindow(static (_, _) => Task.FromResult(ServerProbeResult.Unknown),
            (model, changed) => new(model, SupportOnly(), new BusinessClientHttp(), changed, new Diagnostics()))
            { Width = 360, DataContext = vm };
        window.Show();
        try
        {
            var repair = window.FindControl<StackPanel>("BusinessRepairActions")!;
            var siblings = Assert.IsType<StackPanel>(repair.Parent).Children;
            var before = Assert.IsType<Border>(siblings[siblings.IndexOf(repair) - 1]);
            Assert.Same(vm.ToggleConnectionCommand, Assert.IsType<Button>(before.Child).Command);
            if (expand) Assert.IsType<Button>(repair.Children[0]).Command!.Execute(null);
            window.UpdateLayout();
            var scroll = Assert.IsType<ScrollViewer>(window.Content);
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
            ScreenshotHelper.Capture(window, expand ? "business-repair-consent-360" : "business-repair-home-360");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActiveTunnelOrLateAnalysisCannotStartARepair(bool initiallyConnected)
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        vm.IsConnected = initiallyConnected;
        var diagnostics = new Diagnostics { Reply = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var services = new BusinessServicesViewModel(vm, SupportOnly(), new BusinessClientHttp(), () => { },
            diagnostics, _ => throw new InvalidOperationException("Must not start a tunnel"));
        services.SetVisible(true);
        services.OpenRepairCommand.Execute(null);
        var pending = services.AnalyzeRepairCommand.ExecuteAsync(null);
        Assert.Single(diagnostics.Sent);
        if (!initiallyConnected) services.SetVisible(false);
        diagnostics.Reply.SetResult(BusinessSupportReply.Unavailable);
        await pending;
        Assert.False(services.ConnectRepairCommand.CanExecute(null));
        Assert.Equal(initiallyConnected, services.IsRepairOpen);
        Assert.Equal(initiallyConnected, vm.IsConnected);
        if (initiallyConnected) Assert.Equal(Strings.BusinessDiagnosticsReplyUnavailable, services.DiagnosticStatus);
    }

    [AvaloniaFact]
    public async Task LocalRepairWorksWithoutAnAnalysisEndpointAndReportsFailure()
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        var diagnostics = new Diagnostics();
        var attempts = 0;
        using var services = new BusinessServicesViewModel(vm, BusinessClientConfiguration.Empty,
            new BusinessClientHttp(), () => { }, diagnostics, _ => { attempts++; return Task.FromResult(false); });
        services.SetVisible(true);
        services.OpenRepairCommand.Execute(null);
        Assert.False(services.ShowAnalyze);
        Assert.True(services.ConnectRepairCommand.CanExecute(null));
        await services.ConnectRepairCommand.ExecuteAsync(null);
        Assert.Equal(1, attempts);
        Assert.Equal(Strings.BusinessRepairFailed, services.RepairHint);
        Assert.Equal(0, diagnostics.Collected);
    }
}
