using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views.Pages;

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
/// <para>The ViewModel is cached in a static field once per process. We
/// can't use an xUnit <c>IClassFixture</c> because its constructor runs on
/// a background thread, and <c>MainWindowViewModel</c> touches Avalonia's
/// dispatcher in <c>ApplyTheme</c>. Caching inside the first [AvaloniaFact]
/// call gets us dispatcher-thread construction AND avoids repeated VM
/// init across all 9 page tests.</para>
/// </summary>
public class PageScreenshotTests
{
    private static MainWindowViewModel? _sharedVm;

    private static MainWindowViewModel GetVm() => _sharedVm ??= new MainWindowViewModel();

    private static string Capture(UserControl page, string name)
    {
        page.DataContext = GetVm();
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

    /// <summary>
    /// NetworkPage has a left-rail navigator with 5 sections (Routing /
    /// Leak Protection / Content / Updates / Autostart). The default
    /// screenshot above captures Routing; this one explicitly selects
    /// Autostart (index 4) before rendering so the v2.27 Bug C redesign
    /// surface is visible in the PNG suite. Without this we'd ship the
    /// whole UX redesign and have no visual record of what it looks like.
    /// </summary>
    [AvaloniaFact]
    public void NetworkPage_AutostartTab()
    {
        var vm = GetVm();
        vm.SelectedSettingsIndex = 4; // Autostart tab
        try
        {
            ScreenshotHelper.CapturePage(new NetworkPage { DataContext = vm }, "page-network-autostart");
        }
        finally
        {
            vm.SelectedSettingsIndex = 0; // restore default so sibling tests aren't affected
        }
    }
}
