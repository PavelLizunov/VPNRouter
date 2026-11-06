using System.Net.Http;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views;
using VPNRouter.Core;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class BusinessUpdatesWindowTests
{
    [AvaloniaFact]
    public void MenuAndNotice_UseTheExistingUpdaterCommands_AndDismissWithoutChangingVpnState()
    {
        using var fixture = new Fixture();
        var notice = ById<Border>(fixture.Window, "BusinessUpdateNotice");
        Assert.False(notice.IsEffectivelyVisible);
        var selected = fixture.Vm.SelectedServer;
        var pool = fixture.Vm.Servers.ToArray();
        var saves = fixture.Store.SaveCount;

        var menuButton = fixture.Window.FindControl<Button>("BusinessMenuButton")!;
        var flyout = Assert.IsType<Flyout>(menuButton.Flyout);
        flyout.ShowAt(menuButton);
        Flush(fixture.Window);
        var check = ById<Button>(Assert.IsAssignableFrom<Control>(flyout.Content), "BusinessUpdateCheck");
        Assert.Same(fixture.Vm.UpdateVm.CheckManuallyCommand, check.Command);
        Assert.Equal(fixture.Vm.UpdateVm.CheckLinkText, check.Content);
        Assert.Equal(fixture.Vm.UpdateVm.CanCheckUpdates, check.IsEffectivelyEnabled);
        flyout.Hide();

        fixture.Vm.UpdateVm.Message = "Synthetic Business update 2.50.6";
        fixture.Vm.UpdateVm.IsVisible = true;
        Flush(fixture.Window);
        Assert.True(notice.IsEffectivelyVisible);
        Assert.Contains(notice.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == fixture.Vm.UpdateVm.Message && text.IsEffectivelyVisible);
        Assert.Same(fixture.Vm.UpdateVm.DownloadAndApplyCommand,
            ById<Button>(fixture.Window, "BusinessUpdateInstall").Command);
        var dismiss = Assert.Single(notice.GetVisualDescendants().OfType<Button>()
            .Where(button => ReferenceEquals(button.Command, fixture.Vm.UpdateVm.DismissCommand)));
        dismiss.Command!.Execute(null);
        Flush(fixture.Window);
        Assert.False(notice.IsEffectivelyVisible);
        Assert.Same(selected, fixture.Vm.SelectedServer);
        Assert.Equal(pool, fixture.Vm.Servers.ToArray());
        Assert.False(fixture.Vm.IsSplitTunnel);
        Assert.False(fixture.Vm.IsConnected);
        Assert.False(fixture.Vm.IsConnecting);
        Assert.Equal(saves, fixture.Store.SaveCount);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Downloading_DisablesTheConnectionControl_WithoutChangingSelectionModeOrConnectionState(bool connected)
    {
        using var fixture = new Fixture();
        fixture.Vm.IsConnected = connected;
        Flush(fixture.Window);
        var connection = Assert.Single(fixture.Window.GetVisualDescendants().OfType<Button>()
            .Where(button => ReferenceEquals(button.Command, fixture.Vm.ToggleConnectionCommand)));
        Assert.True(connection.IsEffectivelyEnabled);
        var selected = fixture.Vm.SelectedServer;
        var pool = fixture.Vm.Servers.ToArray();
        var saves = fixture.Store.SaveCount;
        var mode = fixture.Vm.IsSplitTunnel;
        fixture.Vm.UpdateVm.IsVisible = true;
        fixture.Vm.UpdateVm.IsDownloading = true;
        fixture.Vm.UpdateVm.DownloadProgress = 37;
        Flush(fixture.Window);

        Assert.False(connection.IsEffectivelyEnabled);
        var notice = ById<Border>(fixture.Window, "BusinessUpdateNotice");
        var progress = Assert.Single(notice.GetVisualDescendants().OfType<ProgressBar>());
        Assert.True(progress.IsEffectivelyVisible);
        Assert.Equal(37d, progress.Value);
        Assert.False(ById<Button>(fixture.Window, "BusinessUpdateInstall").IsEffectivelyVisible);
        Assert.Same(selected, fixture.Vm.SelectedServer);
        Assert.Equal(pool, fixture.Vm.Servers.ToArray());
        Assert.Equal(mode, fixture.Vm.IsSplitTunnel);
        Assert.Equal(connected, fixture.Vm.IsConnected);
        Assert.False(fixture.Vm.IsConnecting);
        Assert.Equal(saves, fixture.Store.SaveCount);

        fixture.Vm.UpdateVm.IsDownloading = false;
        Flush(fixture.Window);
        Assert.True(connection.IsEffectivelyEnabled);
    }

    [AvaloniaTheory]
    [InlineData(340, "ru")]
    [InlineData(420, "ru")]
    [InlineData(340, "en")]
    [InlineData(420, "en")]
    public void UpdateNotice_RendersLocalizedActionsWithinNarrowWindows(int width, string language)
    {
        using var fixture = new Fixture(width, language);
        fixture.Vm.UpdateVm.Message = string.Format(Strings.UpdateAvailableMessage, "2.50.6", 60.0);
        fixture.Vm.UpdateVm.IsVisible = true;
        Flush(fixture.Window);
        var notice = ById<Border>(fixture.Window, "BusinessUpdateNotice");
        var install = ById<Button>(fixture.Window, "BusinessUpdateInstall");
        var dismiss = Assert.Single(notice.GetVisualDescendants().OfType<Button>()
            .Where(button => ReferenceEquals(button.Command, fixture.Vm.UpdateVm.DismissCommand)));
        Assert.Equal(Strings.UpdateButton, install.Content);
        Assert.Equal(Strings.UpdateButtonDismiss, dismiss.Content);
        Assert.Same(fixture.Vm.UpdateVm.DownloadAndApplyCommand, install.Command);
        Assert.Contains(notice.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == fixture.Vm.UpdateVm.Message && text.IsEffectivelyVisible);
        var scroll = Assert.IsType<ScrollViewer>(fixture.Window.Content);
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
        foreach (var control in new Control[] { notice, install, dismiss })
        {
            Assert.True(control.IsEffectivelyVisible);
            Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0);
            var position = control.TranslatePoint(default, scroll);
            Assert.True(position.HasValue);
            Assert.True(position!.Value.X >= -1 && position.Value.X + control.Bounds.Width <= scroll.Viewport.Width + 1);
        }
        ScreenshotHelper.Capture(fixture.Window, $"business-update-notice-{language}-{width}");
    }

    private static T ById<T>(Control root, string id) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>()
            .Where(control => AutomationProperties.GetAutomationId(control) == id));

    private static void Flush(BusinessWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Fixture : IDisposable
    {
        internal InMemorySettingsStore Store { get; } = new();
        internal MainWindowViewModel Vm { get; }
        internal BusinessWindow Window { get; }
        private readonly string _previousLanguage = Strings.Lang;
        private readonly HttpClient _http = new(new NoNetwork());

        internal Fixture(int width = 420, string language = "en")
        {
            var settings = new AppSettings();
            BusinessDefaults.Apply(settings, BusinessClientConfigurationTests.Three, BusinessBootstrap.ReadRules());
            settings.App.Language = language;
            settings.App.RoutingMode = "full";
            settings.Vless.ActiveServer = "Germany VLESS";
            Store.Save(settings, AppPaths.ConfigYamlPath);
            Vm = new(Store);
            Window = new(static (_, _) => Task.FromResult(new ServerProbeResult(ServerProbeStatus.Ok, 12, null)),
                (vm, changed) => new(vm, BusinessClientConfiguration.Empty, new(_http), changed))
                { DataContext = Vm, Width = width };
            Window.Show();
            Flush(Window);
        }

        public void Dispose()
        {
            Window.FindControl<Button>("BusinessMenuButton")?.Flyout?.Hide();
            Dispatcher.UIThread.RunJobs();
            Window.Close();
            Vm.Dispose();
            _http.Dispose();
            Strings.Lang = _previousLanguage;
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected network access from the synthetic update window fixture.");
    }
}
