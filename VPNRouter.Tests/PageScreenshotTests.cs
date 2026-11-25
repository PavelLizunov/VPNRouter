using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views.Pages;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;
using VPNRouter.Tests.Fakes;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public class PageScreenshotTests
{
    private static MainWindowViewModel GetVm() =>
        new(new InMemorySettingsStore());

    private static string Capture(UserControl page, string name)
    {
        using var vm = GetVm();
        page.DataContext = vm;
        return ScreenshotHelper.CapturePage(page, name);
    }

    [AvaloniaFact] public void SubscribePage() => Capture(new SubscribePage(), "page-subscribe");
    [AvaloniaFact] public void ServersPage() => Capture(new ServersPage(), "page-servers");
    [AvaloniaFact] public void NetworkPage() => Capture(new NetworkPage(), "page-network");
    [AvaloniaFact] public void ApplicationsPage() => Capture(new ApplicationsPage(), "page-applications");
    [AvaloniaFact] public void ToolsPage() => Capture(new ToolsPage(), "page-tools");
    [AvaloniaFact] public void DpiBypassPage() => Capture(new DpiBypassPage(), "page-dpi-bypass");
    [AvaloniaFact] public void TelegramPage() => Capture(new TelegramPage(), "page-telegram");
    [AvaloniaFact] public void FreeConfigsPage() => Capture(new FreeConfigsPage(), "page-free-configs");
    [AvaloniaFact] public void SimplePage() => Capture(new SimplePage(), "page-simple");

    [AvaloniaFact]
    public void SimplePage_ConnectedWithAssistantAt520x680()
    {
        using var vm = GetVm();
        vm.Subscriptions.Add(new SubscriptionViewModel(new SubscriptionEntry
        {
            Name = "Fixture subscription",
            Url = "https://ninitux.com/sub/synthetic-viewport",
            Enabled = true
        }));
        vm.IsConnected = true;
        ScreenshotHelper.CapturePage(new SimplePage { DataContext = vm },
            "simple-connected-with-assistant", width: 520, height: 680);
    }

    private static VlessServerEntry PickerEntry(string name, string protocol = "vless", string transport = "tcp") =>
        new()
        {
            Name = name,
            Server = $"{name.Replace(' ', '-').ToLowerInvariant()}.invalid",
            Port = 443,
            Protocol = protocol,
            Security = "none",
            Uuid = "00000000-0000-4000-8000-000000000001",
            Transport = new VPNRouter.Core.Models.VlessTransportConfig { Type = transport },
        };

    [AvaloniaTheory]
    [InlineData(520, 680)]
    [InlineData(360, 720)]
    public void SimplePage_ServerCategories(int width, int height)
    {
        var previousLanguage = VPNRouter.App.Localization.Strings.Lang;
        try
        {
            using var vm = GetVm();
            vm.SetLanguageRussianCommand.Execute(null);
            foreach (var entry in new[]
                     {
                         PickerEntry("Aruba Cloud VLESS"),
                         PickerEntry("BitFolk XHTTP", transport: "xhttp"),
                         PickerEntry("Hysteria Games", protocol: "hysteria2"),
                         PickerEntry("Naive Web", protocol: "naive"),
                     })
                vm.SubscriptionServers.Add(new ServerViewModel(entry));
            vm.OpenConfigPickerCommand.Execute(null);
            ScreenshotHelper.CapturePage(new SimplePage { DataContext = vm },
                $"simple-server-categories-{width}", width, height);
        }
        finally
        {
            VPNRouter.App.Localization.Strings.Lang = previousLanguage;
        }
    }

    [AvaloniaFact]
    public void Assistant_one_primary_step_520()
    {
        var previousLanguage = VPNRouter.App.Localization.Strings.Lang;
        try
        {
            using var vm = GetVm();
            vm.SetLanguageRussianCommand.Execute(null);
            vm.Subscriptions.Add(new SubscriptionViewModel(new SubscriptionEntry
            {
                Name = "Fixture subscription",
                Url = "https://ninitux.com/sub/synthetic-panel",
                Enabled = true
            }));
            vm.SubscriptionServers.Add(new ServerViewModel(PickerEntry("Fixture server")));
            ScreenshotHelper.CapturePage(new SimplePage { DataContext = vm }, "assistant-one-step-520", 520, 680);
        }
        finally
        {
            VPNRouter.App.Localization.Strings.Lang = previousLanguage;
        }
    }

    [AvaloniaFact]
    public void Assistant_more_checks_360()
    {
        var previousLanguage = VPNRouter.App.Localization.Strings.Lang;
        try
        {
            using var vm = GetVm();
            vm.SetLanguageRussianCommand.Execute(null);
            vm.Subscriptions.Add(new SubscriptionViewModel(new SubscriptionEntry
            {
                Name = "Fixture subscription",
                Url = "https://ninitux.com/sub/synthetic-panel",
                Enabled = true
            }));
            vm.SubscriptionServers.Add(new ServerViewModel(PickerEntry("Fixture server")));
            var page = new SimplePage { DataContext = vm };
            var assistant = page.FindControl<AssistantSetupPanel>("SetupAssistant")!;
            assistant.DataContext = vm;
            assistant.FindControl<Button>("MoreChecksToggle")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ScreenshotHelper.CapturePage(page, "assistant-more-checks-360", 360, 720);
        }
        finally
        {
            VPNRouter.App.Localization.Strings.Lang = previousLanguage;
        }
    }

    [AvaloniaFact]
    public async Task Assistant_consent_360()
    {
        var previous = AppPaths.DataDir;
        var directory = Directory.CreateTempSubdirectory();
        var previousLanguage = VPNRouter.App.Localization.Strings.Lang;
        try
        {
            AppPaths.OverrideDataDir(directory.FullName);
            var crashes = Directory.CreateDirectory(Path.Combine(directory.FullName, "crashes"));
            File.WriteAllText(Path.Combine(crashes.FullName, "crash-001.txt"), """
                VPNRouter crash report
                Version:   2.51.1-r1
                Fatal:     True

                ──── Exception ────
                System.InvalidOperationException: synthetic crash
                """);
            var settings = new AppSettings();
            settings.App.ConfigMode = "subscribe";
            settings.App.Language = "ru";
            settings.App.Subscriptions =
            [
                new() { Url = "https://ninitux.com/sub/synthetic-consent", Enabled = true }
            ];
            var store = new InMemorySettingsStore();
            store.Save(settings, AppPaths.ConfigYamlPath);
            using var vm = new MainWindowViewModel(store)
            {
                StepAssistantAsync = (_, _, _, _, _) =>
                    Task.FromResult(new AssistantStep(AssistantStepStatus.Diagnosis, Text: "unexpected")),
            };
            var panel = new AssistantSetupPanel { DataContext = vm };
            var window = new Window { Width = 360, Height = 360, Content = panel };
            window.Show();
            try
            {
                var consent = panel.FindControl<Border>("AssistantConsent")!;
                var consentText = panel.FindControl<TextBlock>("AssistantConsentText")!;
                var shown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                consentText.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBlock.TextProperty && !string.IsNullOrEmpty(consentText.Text))
                        shown.TrySetResult(true);
                };
                panel.FindControl<Button>("MoreChecksToggle")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                panel.FindControl<Button>("DiagnoseCrash")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await shown.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                window.UpdateLayout();
                Assert.True(consent.IsVisible);
                Assert.Equal(Strings.AssistantCrashConsent, consentText.Text);
                ScreenshotHelper.Capture(window, "assistant-consent-360");
            }
            finally { window.Close(); }
        }
        finally
        {
            VPNRouter.App.Localization.Strings.Lang = previousLanguage;
            AppPaths.OverrideDataDir(previous);
            directory.Delete(true);
        }
    }

    [AvaloniaFact]
    public void Assistant_panel_follows_subscription_eligibility()
    {
        using var vm = GetVm();
        var panel = new AssistantSetupPanel { DataContext = vm };
        var subscription = new SubscriptionViewModel(new SubscriptionEntry
        {
            Name = "Fixture subscription",
            Url = "https://ninitux.com/sub/synthetic-assistant-visual",
            Enabled = false
        });

        vm.Subscriptions.Add(subscription);
        Assert.False(panel.IsVisible);
        subscription.Enabled = true;
        Assert.True(panel.IsVisible);
        Assert.True(panel.FindControl<TextBox>("AssistantGoal")!.IsVisible);
        Assert.True(panel.FindControl<Button>("RunAssistant")!.IsVisible);

        var eligibleUrl = subscription.Url;
        subscription.Url = "https://provider.example/sub/synthetic-assistant-visual";
        Assert.False(panel.IsVisible);
        subscription.Url = eligibleUrl;
        Assert.True(panel.IsVisible);

        var previousLanguage = VPNRouter.App.Localization.Strings.Lang;
        try
        {
            vm.SetLanguageRussianCommand.Execute(null);
            ScreenshotHelper.CapturePage(panel, "assistant-setup-narrow360", width: 360, height: 320);
        }
        finally
        {
            VPNRouter.App.Localization.Strings.Lang = previousLanguage;
        }
    }

    [AvaloniaFact]
    public void ApplicationsPage_Narrow529()
    {
        using var vm = GetVm();
        ScreenshotHelper.CapturePage(
            new ApplicationsPage { DataContext = vm },
            "page-applications-narrow529",
            width: 529, height: 800);
    }

    [AvaloniaFact]
    public void ApplicationsPage_Narrow529_Russian()
    {
        var previousLanguage = VPNRouter.App.Localization.Strings.Lang;
        try
        {
            using var vm = GetVm();
            vm.SetLanguageRussianCommand.Execute(null);
            ScreenshotHelper.CapturePage(
                new ApplicationsPage { DataContext = vm },
                "page-applications-narrow529-ru",
                width: 529, height: 800);
        }
        finally
        {
            VPNRouter.App.Localization.Strings.Lang = previousLanguage;
        }
    }

    [AvaloniaFact]
    public void ApplicationsPage_Narrow360()
    {
        using var vm = GetVm();
        ScreenshotHelper.CapturePage(
            new ApplicationsPage { DataContext = vm },
            "page-applications-narrow360",
            width: 360, height: 800);
    }

    [AvaloniaFact]
    public void NetworkPage_AutostartTab()
    {
        using var vm = GetVm();
        vm.SelectedSettingsIndex = 5;
        try
        {
            ScreenshotHelper.CapturePage(new NetworkPage { DataContext = vm }, "page-network-autostart");
        }
        finally
        {
            vm.SelectedSettingsIndex = 0;
        }
    }

    [AvaloniaFact]
    public void NetworkPage_Autostart_Narrow720()
    {
        using var vm = GetVm();
        vm.SelectedSettingsIndex = 5;
        try
        {
            ScreenshotHelper.CapturePage(new NetworkPage { DataContext = vm }, "page-network-autostart-narrow", width: 720, height: 800);
        }
        finally { vm.SelectedSettingsIndex = 0; }
    }

    [AvaloniaFact]
    public void NetworkPage_Routing_Narrow720()
    {
        using var vm = GetVm();
        vm.SelectedSettingsIndex = 0;
        ScreenshotHelper.CapturePage(new NetworkPage { DataContext = vm }, "page-network-routing-narrow", width: 720, height: 800);
    }

    [AvaloniaFact]
    public void NetworkPage_UpdatesRollbackConfirmation_Narrow400_Russian()
    {
        var previousLanguage = VPNRouter.App.Localization.Strings.Lang;
        try
        {
            using var vm = GetVm();
            vm.SetLanguageRussianCommand.Execute(null);
            vm.SelectedSettingsIndex = 4;
            vm.UpdateVm.IsVersionHistoryVisible = true;
            vm.UpdateVm.StableVersions.Add(new RollbackReleaseItemViewModel(
                "2.49.3", isInstalled: true, info: null, onSelect: null));
            var older = new UpdateSourceInfo(
                Version: "2.49.2",
                ReleaseUrl: string.Empty,
                AssetName: "VPNRouter-v2.49.2-win.zip",
                DownloadUrl: "https://example.invalid/VPNRouter-v2.49.2-win.zip",
                AssetSize: 1,
                AssetSha256: new string('a', 64),
                IsPrerelease: false,
                ReleaseNotes: string.Empty);
            var olderItem = new RollbackReleaseItemViewModel(
                older.Version, isInstalled: false, older, _ => { });
            vm.UpdateVm.StableVersions.Add(olderItem);
            vm.UpdateVm.SelectedRollback = older;
            vm.UpdateVm.IsRollbackConfirmationVisible = true;

            ScreenshotHelper.CapturePage(
                new NetworkPage { DataContext = vm },
                "page-network-updates-rollback-narrow400-ru",
                width: 400, height: 900);
        }
        finally
        {
            VPNRouter.App.Localization.Strings.Lang = previousLanguage;
        }
    }

    [AvaloniaFact]
    public void NetworkPage_Autostart_Narrow500()
    {
        using var vm = GetVm();
        vm.SelectedSettingsIndex = 5;
        try
        {
            ScreenshotHelper.CapturePage(new NetworkPage { DataContext = vm }, "page-network-autostart-narrow500", width: 500, height: 800);
        }
        finally { vm.SelectedSettingsIndex = 0; }
    }

    [AvaloniaFact]
    public void NetworkPage_Autostart_Narrow400()
    {
        using var vm = GetVm();
        vm.SelectedSettingsIndex = 5;
        try
        {
            ScreenshotHelper.CapturePage(new NetworkPage { DataContext = vm }, "page-network-autostart-narrow400", width: 400, height: 800);
        }
        finally { vm.SelectedSettingsIndex = 0; }
    }

    [AvaloniaFact]
    public void NetworkPage_AutostartTab_ServiceNotInstalled()
    {
        using var vm = GetVm();
        var prev = vm.SelectedSettingsIndex;
        var prevInstalled = vm.ServiceVm.IsInstalled;
        try
        {
            vm.SelectedSettingsIndex = 5;
            vm.ServiceVm.IsInstalled = false;
            ScreenshotHelper.CapturePage(
                new NetworkPage { DataContext = vm },
                "page-network-autostart-no-service");
        }
        finally
        {
            vm.ServiceVm.IsInstalled = prevInstalled;
            vm.SelectedSettingsIndex = prev;
        }
    }

    [AvaloniaFact]
    public void NetworkPage_AutostartTab_ServiceInstalled()
    {
        using var vm = GetVm();
        var prev = vm.SelectedSettingsIndex;
        var prevInstalled = vm.ServiceVm.IsInstalled;
        var prevRunning = vm.ServiceVm.IsRunning;
        try
        {
            vm.SelectedSettingsIndex = 5;
            vm.ServiceVm.IsInstalled = true;
            vm.ServiceVm.IsRunning = true;
            ScreenshotHelper.CapturePage(
                new NetworkPage { DataContext = vm },
                "page-network-autostart-service-installed");
        }
        finally
        {
            vm.ServiceVm.IsRunning = prevRunning;
            vm.ServiceVm.IsInstalled = prevInstalled;
            vm.SelectedSettingsIndex = prev;
        }
    }

    [AvaloniaFact]
    public void NetworkPage_AutostartTab_ServiceInstalledStopped()
    {
        using var vm = GetVm();
        var prev = vm.SelectedSettingsIndex;
        var prevInstalled = vm.ServiceVm.IsInstalled;
        var prevRunning = vm.ServiceVm.IsRunning;
        try
        {
            vm.SelectedSettingsIndex = 5;
            vm.ServiceVm.IsInstalled = true;
            vm.ServiceVm.IsRunning = false;
            ScreenshotHelper.CapturePage(
                new NetworkPage { DataContext = vm },
                "page-network-autostart-service-installed-stopped");
        }
        finally
        {
            vm.ServiceVm.IsRunning = prevRunning;
            vm.ServiceVm.IsInstalled = prevInstalled;
            vm.SelectedSettingsIndex = prev;
        }
    }

    [AvaloniaFact]
    public void TelegramPage_Narrow520()
    {
        using var vm = GetVm();
        ScreenshotHelper.CapturePage(
            new TelegramPage { DataContext = vm },
            "page-telegram-narrow520",
            width: 520, height: 800);
    }

    [AvaloniaFact]
    public void TelegramPage_RunningStateBanner()
    {
        using var vm = GetVm();
        vm.TgProxyEnabled = true;
        vm.TgProxyStatus = "Running (PID 18636)";

        ScreenshotHelper.CapturePage(
            new TelegramPage { DataContext = vm },
            "page-telegram-running");
    }
}
