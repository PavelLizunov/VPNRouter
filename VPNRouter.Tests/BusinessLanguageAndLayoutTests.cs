using System.Net;
using System.Net.Http;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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

[Collection(SafeModeStateCollection.Name)]
public sealed class BusinessLanguageAndLayoutTests
{
    [AvaloniaFact]
    public void LanguageSelector_UpdatesStaticAndCountryLabels_PersistsAndRetainsOwnersSelectionAndMode()
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        var selector = window.FindControl<ComboBox>("BusinessServerSelector")!;
        var choice = Assert.IsType<BusinessServerChoiceViewModel>(selector.SelectedItem);
        choice.Update(new(ServerProbeStatus.Ok, 12, null));
        var before = fixture.Vm.SelectedServer;
        var controller = fixture.Services;
        var language = window.FindControl<ComboBox>("BusinessLanguageSelector")!;
        Assert.Equal("Send", Send(window).Content);
        var saves = fixture.Store.SaveCount;
        language.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.True(fixture.Vm.IsRussian);
        Assert.True(fixture.Store.SaveCount > saves);
        Assert.Equal("ru", fixture.Store.Load(AppPaths.ConfigYamlPath).App.Language);
        Assert.Equal("Отправить", Send(window).Content);
        Assert.Equal("Сервер VPN", AutomationProperties.GetName(selector));
        Assert.Equal("Исландия", choice.CountryName);
        Assert.Equal("12 мс", choice.PingText);
        Assert.Same(choice, selector.SelectedItem);
        Assert.Same(before, fixture.Vm.SelectedServer);
        Assert.Same(controller, fixture.Services);
        Assert.False(fixture.Vm.IsSplitTunnel);
        language.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Send", Send(window).Content);
        Assert.Equal("Iceland", choice.CountryName);
        Assert.Equal("en", fixture.Store.Load(AppPaths.ConfigYamlPath).App.Language);
    }

    [AvaloniaFact]
    public async Task ChangingLanguageDuringCollection_LocalizesProgressAndCompletion_WithoutCancellingOrSending()
    {
        using var fixture = new Fixture();
        var operation = fixture.Services.RunDiagnosticsAsync(false);
        Assert.True(fixture.Services.IsDiagnosticBusy);
        Assert.Equal("Collecting diagnostics…", fixture.Services.DiagnosticStatus);
        fixture.Window.FindControl<ComboBox>("BusinessLanguageSelector")!.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Собираем диагностику…", fixture.Services.DiagnosticStatus);
        Assert.False(fixture.Diagnostics.Token.IsCancellationRequested);
        fixture.Diagnostics.Gate.SetResult("synthetic-report.zip");
        await operation;
        Assert.Equal("Отчёт сохранён.", fixture.Services.DiagnosticStatus);
        Assert.Equal(1, fixture.Diagnostics.Collections);
        Assert.Equal(0, fixture.Diagnostics.Sends);
        Assert.True(fixture.Services.HasReport);
        fixture.Window.FindControl<ComboBox>("BusinessLanguageSelector")!.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Report saved.", fixture.Services.DiagnosticStatus);
    }

    [AvaloniaTheory]
    [InlineData(true, "en", "split")]
    [InlineData(false, "en", "manual/full")]
    [InlineData(true, "ru", "full")]
    [InlineData(false, "ru", "subscribe/split")]
    public async Task ChangingLanguageWhileConnectionIsBusy_TranslatesStateAndRetainsActiveOwners(bool connecting, string initialLanguage, string mode)
    {
        using var fixture = new Fixture(language: initialLanguage);
        SetBusyState(fixture, connecting, mode);
        var controller = fixture.Services;
        var selected = fixture.Vm.SelectedServer;
        var pool = fixture.Vm.Servers.ToArray();
        var operation = controller.RunDiagnosticsAsync(false);
        var language = fixture.Window.FindControl<ComboBox>("BusinessLanguageSelector")!;

        foreach (var targetLanguage in new[] { initialLanguage == "en" ? "ru" : "en", initialLanguage })
        {
            language.SelectedIndex = targetLanguage == "ru" ? 0 : 1;
            FlushLayout(fixture.Window);

            Assert.Equal(targetLanguage == "ru", fixture.Vm.IsRussian);
            Assert.Equal(targetLanguage, fixture.Store.PersistedLanguage);
            AssertConnectionLabels(fixture, connecting,
                connecting ? Strings.ButtonConnecting : Strings.BusinessConnected);
            AssertBusyOwnersUnchanged(fixture, connecting, controller, selected, pool);
            Assert.Equal(targetLanguage == "ru" ? "Собираем диагностику…" : "Collecting diagnostics…",
                controller.DiagnosticStatus);
        }

        fixture.Diagnostics.Gate.SetResult("synthetic-report.zip");
        await operation;
        Assert.True(controller.HasReport);
        Assert.Equal(1, fixture.Diagnostics.Collections);
        Assert.Equal(0, fixture.Diagnostics.Sends);
    }

    [AvaloniaTheory]
    [InlineData(true, "en")]
    [InlineData(false, "en")]
    [InlineData(true, "ru")]
    [InlineData(false, "ru")]
    public async Task LanguageWriteFailure_ShowsTranslatedErrorAndStateCorrectButton_WithoutInterruptingWork(bool connecting, string initialLanguage)
    {
        using var fixture = new Fixture(language: initialLanguage);
        SetBusyState(fixture, connecting);
        var controller = fixture.Services;
        var selected = fixture.Vm.SelectedServer;
        var pool = fixture.Vm.Servers.ToArray();
        var operation = controller.RunDiagnosticsAsync(false);
        var before = fixture.Store.SaveCount;
        fixture.Store.RejectWrites = true;
        var language = fixture.Window.FindControl<ComboBox>("BusinessLanguageSelector")!;

        var error = Record.Exception(() => language.SelectedIndex = initialLanguage == "en" ? 0 : 1);
        FlushLayout(fixture.Window);

        Assert.Null(error);
        Assert.Equal(initialLanguage == "en", fixture.Vm.IsRussian);
        Assert.Equal(before, fixture.Store.SaveCount);
        Assert.Equal(initialLanguage, fixture.Store.PersistedLanguage);
        var expectedError = initialLanguage == "en" ? "Не удалось сохранить язык." : "Could not save language.";
        AssertConnectionLabels(fixture, connecting, expectedError);
        Assert.DoesNotContain("synthetic-write-failure", fixture.Window.Texts.ConnectionStatus);
        AssertBusyOwnersUnchanged(fixture, connecting, controller, selected, pool);
        Assert.Equal(initialLanguage == "en" ? "Собираем диагностику…" : "Collecting diagnostics…",
            controller.DiagnosticStatus);

        // A later successful choice clears the save error while retaining the connection state.
        fixture.Store.RejectWrites = false;
        language.SelectedIndex = initialLanguage == "ru" ? 0 : 1;
        FlushLayout(fixture.Window);
        Assert.Equal(initialLanguage, fixture.Store.PersistedLanguage);
        Assert.True(fixture.Store.SaveCount > before);
        AssertConnectionLabels(fixture, connecting,
            connecting ? Strings.ButtonConnecting : Strings.BusinessConnected);
        AssertBusyOwnersUnchanged(fixture, connecting, controller, selected, pool);
        Assert.Equal(initialLanguage == "ru" ? "Собираем диагностику…" : "Collecting diagnostics…",
            controller.DiagnosticStatus);

        fixture.Diagnostics.Gate.SetResult("synthetic-report.zip");
        await operation;
        Assert.True(controller.HasReport);
        Assert.Equal(1, fixture.Diagnostics.Collections);
        Assert.Equal(0, fixture.Diagnostics.Sends);
    }

    [AvaloniaFact]
    public void ChangingLanguage_PreservesConnectedWarningAndConnectingRecoveryProgress()
    {
        using var fixture = new Fixture();
        SetBusyState(fixture, connecting: false);
        var controller = fixture.Services;
        var selected = fixture.Vm.SelectedServer;
        var language = fixture.Window.FindControl<ComboBox>("BusinessLanguageSelector")!;
        const string warning = "Connected [split] — connection needs attention: recovery is in progress.";
        fixture.Vm.StatusText = warning;

        foreach (var index in new[] { 0, 1 })
        {
            language.SelectedIndex = index;
            FlushLayout(fixture.Window);
            AssertConnectionLabels(fixture, connecting: false, warning);
            Assert.True(fixture.Vm.IsConnected);
            Assert.Same(controller, fixture.Services);
            Assert.Same(selected, fixture.Vm.SelectedServer);
        }

        fixture.Vm.IsConnecting = true;
        const string recovery = "Switching to synthetic reserve...";
        fixture.Vm.StatusText = recovery;
        foreach (var index in new[] { 0, 1 })
        {
            language.SelectedIndex = index;
            FlushLayout(fixture.Window);
            AssertConnectionLabels(fixture, connecting: true, recovery);
            Assert.True(fixture.Vm.IsConnecting);
            Assert.True(fixture.Vm.IsConnected);
            Assert.Same(controller, fixture.Services);
            Assert.Same(selected, fixture.Vm.SelectedServer);
        }
    }

    [AvaloniaTheory]
    [InlineData("status")]
    [InlineData("connecting")]
    [InlineData("connected")]
    public void RuntimeTransition_ClearsLanguageSaveFailure_AndDisplaysCurrentVpnState(string transition)
    {
        using var fixture = new Fixture();
        if (transition == "status") SetBusyState(fixture, connecting: false);
        if (transition == "connected") fixture.Vm.StatusText = Strings.Connected("split", null, null);
        var controller = fixture.Services;
        var selected = fixture.Vm.SelectedServer;
        fixture.Store.RejectWrites = true;
        fixture.Window.FindControl<ComboBox>("BusinessLanguageSelector")!.SelectedIndex = 0;
        FlushLayout(fixture.Window);
        Assert.Equal("Не удалось сохранить язык.", fixture.Window.Texts.ConnectionStatus);

        const string warning = "Synthetic recovery warning after reconnect.";
        if (transition == "status") fixture.Vm.StatusText = warning;
        else if (transition == "connecting") fixture.Vm.IsConnecting = true;
        else fixture.Vm.IsConnected = true;
        FlushLayout(fixture.Window);

        var expected = transition == "status" ? warning
            : transition == "connecting" ? "Подключение…" : "Подключено";
        AssertConnectionLabels(fixture, transition == "connecting", expected);
        Assert.Same(controller, fixture.Services);
        Assert.Same(selected, fixture.Vm.SelectedServer);
        Assert.Equal("en", fixture.Store.PersistedLanguage);
        Assert.Equal("ru", Strings.Lang);
    }

    [AvaloniaFact]
    public async Task OpenServerPopup_TooltipsKeepPerRowHealth_AndTranslateWithLanguage()
    {
        using var fixture = new Fixture();
        await fixture.Window.RefreshBusinessServersAsync();
        var selector = fixture.Window.FindControl<ComboBox>("BusinessServerSelector")!;
        var slow = Assert.IsType<BusinessServerChoiceViewModel>(selector.Items[1]);
        var unavailable = Assert.IsType<BusinessServerChoiceViewModel>(selector.Items[2]);
        slow.Update(new(ServerProbeStatus.Slow, 930, null));
        unavailable.Update(new(ServerProbeStatus.Unreachable, 0, null));
        fixture.Vm.StatusText = "Synthetic overall connection status";
        selector.IsDropDownOpen = true;
        FlushLayout(fixture.Window);
        var slowRow = Assert.IsType<ComboBoxItem>(selector.ContainerFromIndex(1));
        var unavailableRow = Assert.IsType<ComboBoxItem>(selector.ContainerFromIndex(2));

        try
        {
            foreach (var index in new[] { 1, 0, 1 })
            {
                fixture.Window.FindControl<ComboBox>("BusinessLanguageSelector")!.SelectedIndex = index;
                FlushLayout(fixture.Window);
                Assert.True(selector.IsDropDownOpen);
                Assert.True(slowRow.IsEffectivelyVisible);
                Assert.True(unavailableRow.IsEffectivelyVisible);
                Assert.Same(slow, slowRow.DataContext);
                Assert.Same(unavailable, unavailableRow.DataContext);
                AssertHealthTooltip(slowRow, index == 0 ? "Медленный ответ" : "Slow response",
                    fixture.Window.Texts.ConnectionStatus);
                AssertHealthTooltip(unavailableRow, index == 0 ? "Нет ответа" : "No response",
                    fixture.Window.Texts.ConnectionStatus);
                Assert.Equal(index == 0 ? "930 мс" : "930 ms", slow.PingText);
                Assert.True(slow.IsWarning);
                Assert.True(unavailable.IsBad);
            }
        }
        finally { selector.IsDropDownOpen = false; }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public void ChangingLanguageDuringDisconnect_KeepsStoppingLabelsAndBusyConnectedState(string initialLanguage)
    {
        using var fixture = new Fixture(language: initialLanguage);
        SetBusyState(fixture, connecting: false);
        fixture.Vm.IsConnecting = true;
        fixture.Vm.StatusText = Strings.Stopping;
        var selected = fixture.Vm.SelectedServer;
        var controller = fixture.Services;
        var language = fixture.Window.FindControl<ComboBox>("BusinessLanguageSelector")!;

        foreach (var index in initialLanguage == "en" ? new[] { 0, 1 } : new[] { 1, 0 })
        {
            language.SelectedIndex = index;
            FlushLayout(fixture.Window);
            AssertConnectionLabels(fixture, connecting: true, Strings.Stopping, expectedButton: Strings.Stopping);
            Assert.Equal(index == 0 ? "Остановка..." : "Stopping...", fixture.Window.Texts.ConnectionStatus);
            Assert.True(fixture.Vm.IsConnecting);
            Assert.True(fixture.Vm.IsConnected);
            Assert.Same(selected, fixture.Vm.SelectedServer);
            Assert.Same(controller, fixture.Services);
            Assert.False(fixture.Vm.IsSplitTunnel);
        }
    }

    [AvaloniaTheory]
    [InlineData("en", "manual/full")]
    [InlineData("ru", "subscribe/split")]
    public void ChangingLanguage_LocalizesHealthyServiceConnection_AndPreservesServiceWarning(string initialLanguage, string mode)
    {
        using var fixture = new Fixture(language: initialLanguage);
        SetBusyState(fixture, connecting: false);
        var source = initialLanguage == "ru" ? $"Подключено через службу [{mode}]" : $"Connected via service [{mode}]";
        fixture.Vm.StatusText = source;
        var selected = fixture.Vm.SelectedServer;
        var controller = fixture.Services;
        var language = fixture.Window.FindControl<ComboBox>("BusinessLanguageSelector")!;

        foreach (var index in initialLanguage == "en" ? new[] { 0, 1 } : new[] { 1, 0 })
        {
            language.SelectedIndex = index;
            FlushLayout(fixture.Window);
            AssertConnectionLabels(fixture, connecting: false, Strings.BusinessServiceConnected);
            Assert.False(fixture.Vm.IsConnecting);
            Assert.True(fixture.Vm.IsConnected);
            Assert.Same(selected, fixture.Vm.SelectedServer);
            Assert.Same(controller, fixture.Services);
        }

        var warning = source + " — synthetic service recovery warning.";
        fixture.Vm.StatusText = warning;
        language.SelectedIndex = initialLanguage == "en" ? 0 : 1;
        FlushLayout(fixture.Window);
        AssertConnectionLabels(fixture, connecting: false, warning);
    }

    [AvaloniaTheory]
    [InlineData(340, "ru")]
    [InlineData(420, "ru")]
    [InlineData(340, "en")]
    [InlineData(420, "en")]
    public void CompactWindow_DefaultsToCollapsedDetails_AndKeepsActionsAndBottomReachable(int width, string language)
    {
        using var fixture = new Fixture(width, language);
        var window = fixture.Window;
        var details = Assert.Single(window.GetVisualDescendants().OfType<Expander>());
        Assert.False(details.IsExpanded);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == Strings.BusinessServerHint && text.IsEffectivelyVisible);
        Assert.True(Send(window).IsEffectivelyEnabled);
        Assert.False(fixture.Services.IsDiagnosticStatusVisible);
        Assert.False(fixture.Services.IsRefreshStatusVisible);
        var scroll = Assert.IsType<ScrollViewer>(window.Content);
        FlushLayout(window);
        AssertInsideViewport(Send(window), scroll);
        AssertInsideViewport(ConnectionButton(fixture), scroll);
        AssertInsideViewport(window.FindControl<ComboBox>("BusinessLanguageSelector")!, scroll);
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
        details.IsExpanded = true;
        FlushLayout(window);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == Strings.BusinessServerHint && text.IsEffectivelyVisible);
        var privacy = Assert.Single(details.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.Text == Strings.BusinessDiagnosticsHint));
        Assert.True(privacy.IsEffectivelyVisible);
        var privacyPosition = privacy.TranslatePoint(default, scroll);
        Assert.True(privacyPosition.HasValue);
        scroll.Offset = new(0, scroll.Offset.Y + privacyPosition!.Value.Y);
        FlushLayout(window);
        AssertInsideViewport(privacy, scroll);
        scroll.Offset = new(0, scroll.Extent.Height);
        FlushLayout(window);
        Assert.True(scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 1);
        var bottom = Assert.Single(details.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.Text == Strings.BusinessHint));
        Assert.True(bottom.IsEffectivelyVisible);
        AssertInsideViewport(bottom, scroll);
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
        details.IsExpanded = false;
        scroll.Offset = default;
        FlushLayout(window);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == Strings.BusinessServerHint && text.IsEffectivelyVisible);
        AssertInsideViewport(Send(window), scroll);
        // Capture closes the window, so it must follow all live-window assertions.
        ScreenshotHelper.Capture(window, $"business-compact-{language}-{width}");
    }

    private static Button Send(BusinessWindow window) => Assert.Single(window.GetVisualDescendants().OfType<Button>()
        .Where(button => AutomationProperties.GetAutomationId(button) == "BusinessDiagnosticsSend"));

    private static Button ConnectionButton(Fixture fixture) => Assert.Single(fixture.Window.GetVisualDescendants().OfType<Button>()
        .Where(button => ReferenceEquals(button.Command, fixture.Vm.ToggleConnectionCommand)));

    private static void SetBusyState(Fixture fixture, bool connecting, string mode = "split")
    {
        fixture.Vm.IsConnecting = connecting;
        fixture.Vm.IsConnected = !connecting;
        // Representative runtime text deliberately belongs to the original language.
        fixture.Vm.ConnectButtonText = connecting ? Strings.Starting : Strings.StopVPN;
        fixture.Vm.StatusText = connecting ? Strings.ButtonConnecting : Strings.Connected(mode, null, null);
        FlushLayout(fixture.Window);
    }

    private static void AssertHealthTooltip(ComboBoxItem row, string expected, string connectionStatus)
    {
        var tooltipOwner = Assert.Single(row.GetVisualDescendants().OfType<Grid>()
            .Where(grid => ToolTip.GetTip(grid) is string));
        Assert.Equal(expected, ToolTip.GetTip(tooltipOwner));
        Assert.NotEqual(connectionStatus, ToolTip.GetTip(tooltipOwner));
    }

    private static void AssertConnectionLabels(Fixture fixture, bool connecting, string expectedStatus, string? expectedButton = null)
    {
        var button = ConnectionButton(fixture);
        expectedButton ??= connecting ? Strings.ButtonConnecting : Strings.StopVPN;
        Assert.Equal(expectedButton, fixture.Window.Texts.ConnectButton);
        Assert.Equal(expectedButton, AutomationProperties.GetName(button));
        Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == expectedButton);
        Assert.Equal(!connecting, button.IsEffectivelyEnabled);
        Assert.Equal(expectedStatus, fixture.Window.Texts.ConnectionStatus);
        var content = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(fixture.Window.Content).Content);
        Assert.Equal(expectedStatus, content.Children.OfType<TextBlock>().First().Text);
    }

    private static void AssertBusyOwnersUnchanged(Fixture fixture, bool connecting,
        BusinessServicesViewModel controller, ServerViewModel? selected, ServerViewModel[] pool)
    {
        Assert.Equal(connecting, fixture.Vm.IsConnecting);
        Assert.Equal(!connecting, fixture.Vm.IsConnected);
        Assert.Same(controller, fixture.Services);
        Assert.Same(controller, fixture.Window.FindControl<Border>("BusinessActions")!.DataContext);
        Assert.Same(controller, fixture.Window.FindControl<StackPanel>("BusinessSubscriptionActions")!.DataContext);
        Assert.Same(selected, fixture.Vm.SelectedServer);
        Assert.Equal(pool, fixture.Vm.Servers.ToArray());
        Assert.False(fixture.Vm.IsSplitTunnel);
        Assert.False(fixture.Window.FindControl<ComboBox>("BusinessServerSelector")!.IsEffectivelyEnabled);
        Assert.True(controller.IsDiagnosticBusy);
        Assert.False(fixture.Diagnostics.Token.IsCancellationRequested);
        Assert.Equal(1, fixture.Diagnostics.Collections);
        Assert.Equal(0, fixture.Diagnostics.Sends);
    }

    private static void AssertInsideViewport(Control control, ScrollViewer scroll)
    {
        var position = control.TranslatePoint(default, scroll);
        Assert.True(control.IsEffectivelyVisible);
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0);
        Assert.True(position.HasValue);
        Assert.InRange(position!.Value.X, -1, scroll.Viewport.Width + 1);
        Assert.InRange(position.Value.Y, -1, scroll.Viewport.Height + 1);
        Assert.True(position.Value.X + control.Bounds.Width <= scroll.Viewport.Width + 1);
        Assert.True(position.Value.Y + control.Bounds.Height <= scroll.Viewport.Height + 1);
    }

    private static void FlushLayout(BusinessWindow window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly RejectingSettingsStore Store = new();
        internal readonly FakeDiagnostics Diagnostics = new();
        internal MainWindowViewModel Vm { get; }
        internal BusinessWindow Window { get; }
        internal BusinessServicesViewModel Services { get; private set; } = null!;
        private readonly HttpClient _client;
        private readonly string _previousLanguage = Strings.Lang;

        internal Fixture(int width = 420, string language = "en")
        {
            var settings = new AppSettings();
            BusinessDefaults.Apply(settings, BusinessClientConfigurationTests.Three, BusinessBootstrap.ReadRules());
            settings.App.Language = language;
            settings.App.RoutingMode = "full";
            Store.Save(settings, AppPaths.ConfigYamlPath);
            Vm = new(Store);
            _client = new(new BusinessClientHttpTests.Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(BusinessClientConfigurationTests.Three) })));
            Window = new(static (_, _) => Task.FromResult(new ServerProbeResult(ServerProbeStatus.Ok, 12, null)),
                (vm, changed) => Services = new(vm, BusinessClientConfiguration.Parse(BusinessClientConfigurationTests.Bundle()),
                    new(_client), changed, Diagnostics)) { DataContext = Vm, Width = width };
            Window.Show();
            FlushLayout(Window);
        }

        public void Dispose() { Window.Close(); Vm.Dispose(); _client.Dispose(); Strings.Lang = _previousLanguage; }
    }

    private sealed class RejectingSettingsStore : ISettingsStore
    {
        private readonly InMemorySettingsStore _inner = new();
        internal bool RejectWrites { get; set; }
        internal int SaveCount => _inner.SaveCount;
        internal string? PersistedLanguage { get; private set; }
        public AppSettings Load(string? path = null) => _inner.Load(path);
        public void Save(AppSettings settings, string? path = null)
        {
            if (RejectWrites) throw new IOException("synthetic-write-failure");
            _inner.Save(settings, path);
            PersistedLanguage = settings.App.Language;
        }
        public string? ResetToDefaults(string? path = null) => _inner.ResetToDefaults(path);
        public string? ConsumeRecoveryNotice() => _inner.ConsumeRecoveryNotice();
        public (int Count, string AtUtc) ConsumePlaceholderPruneNotice(AppSettings settings) => _inner.ConsumePlaceholderPruneNotice(settings);
        public void StartWatching(string? path = null, Action<AppSettings>? onReload = null) => _inner.StartWatching(path, onReload);
        public void StopWatching() => _inner.StopWatching();
    }

    private sealed class FakeDiagnostics : IBusinessDiagnostics
    {
        internal readonly TaskCompletionSource<string> Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token;
        internal int Collections, Sends;
        public Task<string> CollectAsync(CancellationToken ct) { Token = ct; Collections++; return Gate.Task.WaitAsync(ct); }
        public Task SendAsync(string path, CancellationToken ct) { Sends++; return Task.CompletedTask; }
    }
}
