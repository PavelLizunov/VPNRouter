using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views;
using VPNRouter.Core;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class BusinessRollbackWindowTests
{
    [AvaloniaFact]
    public async Task HistorySelectionAndCancel_DoNotDownloadStopApplyOrChangeProfile()
    {
        using var fixture = new Fixture();
        var selected = fixture.Vm.SelectedServer;
        var pool = fixture.Vm.Servers.ToArray();
        var saves = fixture.Store.SaveCount;
        await fixture.OpenHistory();
        Assert.Equal(new[] { AppVersion.Version, "2.50.7", "2.50.6" },
            fixture.Vm.UpdateVm.StableVersions.Select(item => item.Version));
        Assert.True(fixture.Vm.UpdateVm.StableVersions[0].IsInstalled);
        Assert.Equal(Strings.RollbackSafetyHint, fixture.Vm.UpdateVm.VersionHistoryMessage);
        Assert.Same(fixture.Vm.UpdateVm.ToggleVersionHistoryCommand,
            fixture.ById<Button>("BusinessVersionHistory").Command);
        Assert.False(fixture.ById<Button>("BusinessRollbackConfirm").IsEffectivelyVisible);

        fixture.Vm.UpdateVm.StableVersions[1].SelectCommand.Execute(null);
        fixture.Flush();
        Assert.True(fixture.Vm.UpdateVm.IsRollbackConfirmationVisible);
        Assert.Equal("2.50.7", fixture.Vm.UpdateVm.SelectedRollback!.Version);
        Assert.Contains("2.50.7", fixture.Vm.UpdateVm.RollbackConfirmationText);
        Assert.Same(fixture.Vm.UpdateVm.ConfirmRollbackCommand,
            fixture.ById<Button>("BusinessRollbackConfirm").Command);
        fixture.ById<Button>("BusinessRollbackCancel").Command!.Execute(null);
        fixture.Flush();
        Assert.Null(fixture.Vm.UpdateVm.SelectedRollback);
        Assert.False(fixture.Vm.UpdateVm.IsRollbackConfirmationVisible);
        Assert.Empty(fixture.Events);
        Assert.Null(fixture.ExitCode);
        Assert.Same(selected, fixture.Vm.SelectedServer);
        Assert.Equal(pool, fixture.Vm.Servers.ToArray());
        Assert.False(fixture.Vm.IsSplitTunnel);
        Assert.Equal(saves, fixture.Store.SaveCount);
    }

    [AvaloniaFact]
    public async Task ConfirmRollback_RealMainWindowPreparationStopsOwnedEngineBeforeApplyAndExit()
    {
        using var fixture = new Fixture();
        fixture.Vm.IsConnected = true;
        var selected = fixture.Vm.SelectedServer;
        var pool = fixture.Vm.Servers.ToArray();
        var saves = fixture.Store.SaveCount;
        fixture.OnStop = () => Assert.True(fixture.Vm.IsConnecting);
        fixture.Installer.BeforeApply = () =>
        {
            Assert.False(fixture.Vm.IsConnected);
            Assert.False(fixture.Vm.IsConnecting);
            Assert.True(fixture.Vm.UpdateVm.IsDownloading);
        };
        await fixture.OpenHistory();
        fixture.Vm.UpdateVm.StableVersions[1].SelectCommand.Execute(null);
        await fixture.Vm.UpdateVm.ConfirmRollbackCommand.ExecuteAsync(null);
        fixture.Flush();
        Assert.Equal(new[] { "download", "owned-stop", "apply", "exit" }, fixture.Events);
        Assert.Equal("2.50.7", fixture.Installer.Applied!.Version);
        Assert.Equal("synthetic-stage", fixture.Installer.AppliedPath);
        Assert.Equal(0, fixture.ExitCode);
        Assert.Same(selected, fixture.Vm.SelectedServer);
        Assert.Equal(pool, fixture.Vm.Servers.ToArray());
        Assert.False(fixture.Vm.IsSplitTunnel);
        Assert.Equal(saves, fixture.Store.SaveCount);
    }

    [AvaloniaTheory]
    [InlineData("connecting")]
    [InlineData("applying")]
    [InlineData("reconnecting")]
    [InlineData("stop-failure")]
    public async Task ConfirmRollback_TransitionOrOwnedStopFailurePreventsApplyAndExit(string failure)
    {
        using var fixture = new Fixture();
        await fixture.OpenHistory();
        fixture.Vm.UpdateVm.StableVersions[1].SelectCommand.Execute(null);
        if (failure == "connecting") fixture.Vm.IsConnecting = true;
        if (failure == "applying") fixture.Vm.IsApplying = true;
        if (failure == "reconnecting")
            typeof(MainWindowViewModel).GetField("_isReconnecting",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(fixture.Vm, true);
        if (failure == "stop-failure") fixture.OnStop = () => throw new IOException("Synthetic owned stop failure.");
        await fixture.Vm.UpdateVm.ConfirmRollbackCommand.ExecuteAsync(null);
        fixture.Flush();
        Assert.Equal(failure == "stop-failure" ? new[] { "download", "owned-stop" } : new[] { "download" }, fixture.Events);
        Assert.Null(fixture.Installer.Applied);
        Assert.Null(fixture.ExitCode);
        Assert.False(fixture.Vm.UpdateVm.IsDownloading);
        Assert.Contains(Strings.UpdateFailed.Split('{')[0], fixture.Vm.UpdateVm.Message);
        if (failure == "connecting") Assert.True(fixture.Vm.IsConnecting);
        if (failure == "applying") Assert.True(fixture.Vm.IsApplying);
        if (failure == "stop-failure") Assert.False(fixture.Vm.IsConnecting);
    }

    [AvaloniaTheory]
    [InlineData(340, "ru")]
    [InlineData(420, "ru")]
    [InlineData(340, "en")]
    [InlineData(420, "en")]
    public async Task HistoryAndConfirmation_FitNarrowMenuWithLocalizedActions(int width, string language)
    {
        using var fixture = new Fixture(width, language);
        await fixture.OpenHistory();
        fixture.Vm.UpdateVm.StableVersions[1].SelectCommand.Execute(null);
        fixture.Flush();
        var scroll = Assert.Single(fixture.Menu.GetVisualDescendants().OfType<ScrollViewer>());
        var confirm = fixture.ById<Button>("BusinessRollbackConfirm");
        var cancel = fixture.ById<Button>("BusinessRollbackCancel");
        cancel.BringIntoView();
        fixture.Flush();
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
        Assert.True(fixture.Menu.Bounds.Width <= width);
        Assert.True(confirm.IsEffectivelyVisible);
        Assert.True(cancel.IsEffectivelyVisible);
        Assert.Contains(confirm.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == Strings.ConfirmRollback);
        Assert.Contains(cancel.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == Strings.Cancel);
        foreach (var button in new[] { confirm, cancel })
        {
            var point = button.TranslatePoint(default, scroll);
            Assert.True(point.HasValue);
            Assert.True(point.Value.X >= -1 && point.Value.X + button.Bounds.Width <= scroll.Viewport.Width + 1);
            Assert.True(point.Value.Y >= -1 && point.Value.Y + button.Bounds.Height <= scroll.Viewport.Height + 1);
        }
        using var bitmap = new RenderTargetBitmap(new PixelSize(
            (int)Math.Ceiling(fixture.Menu.Bounds.Width), (int)Math.Ceiling(fixture.Menu.Bounds.Height)), new Vector(96, 96));
        bitmap.Render(fixture.Menu);
        bitmap.Save(Path.Combine(ScreenshotHelper.ScreenshotsDir, $"business-rollback-{language}-{width}.png"));
        Assert.Empty(fixture.Events);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _previousData = AppPaths.DataDir;
        private readonly string _previousLanguage = Strings.Lang;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "business-rollback-ui-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        private readonly Flyout _flyout;
        internal InMemorySettingsStore Store { get; } = new();
        internal List<string> Events { get; } = new();
        internal MainWindowViewModel Vm { get; }
        internal BusinessWindow Window { get; }
        internal Control Menu => (Control)_flyout.Content!;
        internal Installer Installer { get; }
        internal Action? OnStop { get; set; }
        internal int? ExitCode { get; private set; }

        internal Fixture(int width = 420, string language = "en")
        {
            AppPaths.OverrideDataDir(_root);
            _http = new(new CatalogHandler());
            Installer = new(Events);
            var settings = new AppSettings();
            BusinessDefaults.Apply(settings, BusinessClientConfigurationTests.Three, BusinessBootstrap.ReadRules());
            settings.App.Language = language;
            settings.App.RoutingMode = "full";
            settings.Update.Channel = "experimental";
            Store.Save(settings, AppPaths.ConfigYamlPath);
            Vm = new(Store,
                prepare => new BusinessUpdateSource(new Uri("https://updates.example.invalid/latest.json"),
                    AppVersion.Version, Installer, prepare, _http, true),
                code => { Events.Add("exit"); ExitCode = code; },
                () => { Events.Add("owned-stop"); OnStop?.Invoke(); });
            Window = new(static (_, _) => Task.FromResult(new ServerProbeResult(ServerProbeStatus.Ok, 12, null)),
                (vm, changed) => new(vm, BusinessClientConfiguration.Empty, new(_http), changed))
                { DataContext = Vm, Width = width };
            Window.Show();
            Flush();
            _flyout = (Flyout)Window.FindControl<Button>("BusinessMenuButton")!.Flyout!;
        }
        internal async Task OpenHistory()
        {
            _flyout.ShowAt(Window.FindControl<Button>("BusinessMenuButton")!);
            Flush();
            await ((IAsyncRelayCommand)ById<Button>("BusinessVersionHistory").Command!).ExecuteAsync(null);
            Flush();
        }
        internal T ById<T>(string id) where T : Control => Assert.Single(Menu.GetVisualDescendants().OfType<T>()
            .Where(control => AutomationProperties.GetAutomationId(control) == id));
        internal void Flush() { Dispatcher.UIThread.RunJobs(); Window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        public void Dispose()
        {
            _flyout.Hide();
            Dispatcher.UIThread.RunJobs();
            Window.Close();
            Vm.Dispose();
            _http.Dispose();
            Strings.Lang = _previousLanguage;
            AppPaths.OverrideDataDir(_previousData);
            try { Directory.Delete(_root, true); } catch { }
        }
    }

    private sealed class Installer(List<string> events) : IDesktopInstaller
    {
        internal UpdateSourceInfo? Applied;
        internal string? AppliedPath;
        internal Action? BeforeApply;
        public Task<string> DownloadAndStageAsync(UpdateSourceInfo info, IProgress<DownloadProgress>? progress, CancellationToken ct)
        { events.Add("download"); return Task.FromResult("synthetic-stage"); }
        public Task<bool> ApplyStagedAsync(UpdateSourceInfo info, string stagedPath, CancellationToken ct)
        { BeforeApply?.Invoke(); events.Add("apply"); Applied = info; AppliedPath = stagedPath; return Task.FromResult(true); }
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            JsonObject Entry(string version) => new()
            {
                ["schema"] = 1, ["edition"] = "Business", ["architecture"] = "win-x64", ["version"] = version,
                ["size"] = 12345, ["sha256"] = new string('a', 64),
                ["asset"] = $"VPNRouterBusiness-update-v{version}-win-x64.zip",
                ["url"] = $"https://updates.example.invalid/VPNRouterBusiness-update-v{version}-win-x64.zip"
            };
            var catalog = Entry("2.50.9");
            catalog["releases"] = new JsonArray(Entry("2.50.7"), Entry("2.50.6"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(catalog.ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }
}
