using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views.Pages;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Captures a PNG of each main page in the app into
/// <see cref="ScreenshotHelper.ScreenshotsDir"/>. Pages are hosted inside a
/// <see cref="Window"/> with a real <see cref="MainWindowViewModel"/> as
/// <c>DataContext</c> — same wiring production uses — so bindings resolve
/// and the screenshot reflects what a user actually sees at first launch
/// (localization, theme, default values).
///
/// <para>Two jobs:</para>
/// <list type="number">
///   <item>
///     <b>Regression for view-tree assembly + bindings.</b> Broken template
///     resolution, missing StaticResource, or a renamed VM property (so a
///     <c>{Binding Foo}</c> can't resolve) surface either as a thrown
///     exception during rendering or as a visible hole in the PNG.
///   </item>
///   <item>
///     <b>Inspectable artefacts per release.</b> A PNG per page lets me
///     (or a reviewer) eyeball the app without launching the full GUI.
///     Screenshots land in <c>VPNRouter.Tests/screenshots/</c>; see
///     <c>plans/ui-testing-workflow.md</c> for the post-release routine.
///   </item>
/// </list>
///
/// <para>Each test creates and disposes a background-disabled ViewModel on
/// Avalonia's dispatcher thread. The test assembly redirects AppPaths to a
/// temporary directory, so screenshots cannot read or write the installed
/// application's settings, logs or runtime state.</para>
/// </summary>
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

    /// <summary>
    /// NetworkPage has a left-rail navigator with 6 sections (Routing /
    /// Rules / Leak Protection / Content / Updates / Autostart). The default
    /// screenshot above captures Routing; this one explicitly selects
    /// Autostart (index 5) before rendering so the v2.27 Bug C redesign
    /// surface is visible in the PNG suite. Without this we'd ship the
    /// whole UX redesign and have no visual record of what it looks like.
    /// <para>v2.31.10: index corrected from 4 to 5 — the v2.30.0-r2 Rules
    /// section addition shifted everything down by one, but the test stayed
    /// on 4 (Updates) and was silently capturing the wrong page for
    /// multiple releases.</para>
    /// </summary>
    [AvaloniaFact]
    public void NetworkPage_AutostartTab()
    {
        using var vm = GetVm();
        vm.SelectedSettingsIndex = 5; // Autostart tab
        try
        {
            ScreenshotHelper.CapturePage(new NetworkPage { DataContext = vm }, "page-network-autostart");
        }
        finally
        {
            vm.SelectedSettingsIndex = 0; // restore default so sibling tests aren't affected
        }
    }

    /// <summary>
    /// Reproduction of the v2.27.0-r1 user report: "обводка не умещается
    /// когда сужаю окно". Captures the Autostart and Routing tabs at a
    /// narrow 720x800 viewport — same resolution as an undocked half of a
    /// 1440p screen. If cards, borders or wrapping labels overflow the
    /// viewport, the diff in the PNG is immediately visible. Goal of any
    /// fix: content wraps inside borders, borders stretch to available
    /// width, no horizontal scroll.
    /// </summary>
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
        vm.SelectedSettingsIndex = 0; // Routing
        ScreenshotHelper.CapturePage(new NetworkPage { DataContext = vm }, "page-network-routing-narrow", width: 720, height: 800);
    }

    /// <summary>Extra-narrow 500px variant — pushes past the point where
    /// native window chrome still renders comfortably. Goal: identify the
    /// control that overflows so we can pin HorizontalAlignment="Stretch"
    /// / TextWrapping="Wrap" on the exact offender.</summary>
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

    /// <summary>Absolute-worst-case 400px. User's real test shrunk the
    /// window until borders visibly broke; this reproduces what they saw
    /// without depending on their WM chrome.</summary>
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

    /// <summary>
    /// v2.31.10 (autostart UX clarity): renders the Network → Autostart
    /// sub-tab with the Service NOT installed. Per-component status
    /// badges below VPN/Zapret/TgProxy CheckBoxes show the red ⛔ "won't
    /// fire without service" wording — this is the dominant pre-install
    /// state for new users, and the badge is what the user-reported
    /// "Auto-start with Windows for tgproxy doesn't work" feedback
    /// resolves. Capturing this state explicitly so a future regression
    /// (status badge removed, IsAutostart*StatusBad binding broken)
    /// shows up in the visual artefact.
    /// </summary>
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

    /// <summary>
    /// v2.31.10 (autostart UX clarity): same Autostart sub-tab, but with
    /// the Service installed and running. All three component badges
    /// flip to green ✓ "via Windows Service (boot)" — this is the steady
    /// "everything wired up" state. Pinning both branches per release
    /// catches a regression where the binding stays stuck on one
    /// status (e.g. ServiceVm.PropertyChanged handler unhooked or Bool
    /// ⇄ String wiring broken).
    /// </summary>
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

    /// <summary>
    /// v2.31.10 (autostart UX clarity): Service installed but NOT
    /// running. Status badge logic deliberately stays on the green ✓
    /// boot branch because the boot semantics depend on IsInstalled
    /// alone (SCM brings the service back up at next reboot regardless
    /// of current runtime state). This screenshot pins that intent —
    /// if a future change makes the badge flip to amber/red just
    /// because the service is currently stopped, the diff highlights
    /// the regression.
    /// </summary>
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

    /// <summary>
    /// v2.31.6-r3 — render TelegramPage at app window width (520 px) to
    /// confirm the design-handoff cell 6 layout (description + 2-col
    /// Port|Secret grid + info banner + 3-button row + note paragraph
    /// + secondary footer) wraps cleanly without horizontal overflow.
    ///
    /// The default <see cref="TelegramPage"/> capture above uses 1200 px,
    /// which leaves the 2-column grid generously spaced; the real risk
    /// is overflow at 520 px when the Secret hex string forces the
    /// flex grid wider than the column allows. That's caught here.
    /// </summary>
    [AvaloniaFact]
    public void TelegramPage_Narrow520()
    {
        using var vm = GetVm();
        ScreenshotHelper.CapturePage(
            new TelegramPage { DataContext = vm },
            "page-telegram-narrow520",
            width: 520, height: 800);
    }

    /// <summary>
    /// v2.31.6-r3 — render with TgProxyEnabled=true so the banner
    /// status line shows the "Running" wording instead of "Stopped".
    /// Pinning both branches per release prevents a regression where
    /// the runtime status binding silently breaks (e.g. setter no
    /// longer fires <c>OnPropertyChanged</c> on TgProxyStatus).
    /// </summary>
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
