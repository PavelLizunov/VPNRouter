using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views.Pages;
using VPNRouter.Core.Services.UpdateSources;
using VPNRouter.Tests.Fakes;

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
