using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views;
using VPNRouter.App.Views.Pages;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

// Design review captures: every desktop page in light and dark, at the narrow and the default window width, plus the
// Settings sub-tabs and the whole window. They only write PNGs to VPNRouter.Tests/screenshots (design-*.png) for a
// person to look at; they assert nothing about pixels. The name keeps them out of CI (the filters skip PageScreenshot).
[Collection(SafeModeStateCollection.Name)]
public class PageScreenshotDesignTests
{
    public static TheoryData<string, bool, int> Pages()
    {
        var data = new TheoryData<string, bool, int>();
        foreach (var page in new[] { "simple", "servers", "subscribe", "network", "applications", "tools", "dpi", "telegram", "free" })
            foreach (var dark in new[] { false, true })
                foreach (var width in new[] { 360, 520 })
                    data.Add(page, dark, width);
        return data;
    }

    private static UserControl Page(string name) => name switch
    {
        "simple" => new SimplePage(),
        "servers" => new ServersPage(),
        "subscribe" => new SubscribePage(),
        "network" => new NetworkPage(),
        "applications" => new ApplicationsPage(),
        "tools" => new ToolsPage(),
        "dpi" => new DpiBypassPage(),
        "telegram" => new TelegramPage(),
        "free" => new FreeConfigsPage(),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static void WithTheme(bool dark, bool russian, Action action)
    {
        var app = Avalonia.Application.Current!;
        var previousTheme = app.RequestedThemeVariant;
        var previousLanguage = VPNRouter.App.Localization.Strings.Lang;
        try
        {
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            action();
        }
        finally
        {
            app.RequestedThemeVariant = previousTheme;
            VPNRouter.App.Localization.Strings.Lang = previousLanguage;
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Pages))]
    public void Page_InBothThemes(string page, bool dark, int width) => WithTheme(dark, false, () =>
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        var control = Page(page);
        control.DataContext = vm;
        ScreenshotHelper.CapturePage(control, $"design-{page}-{(dark ? "dark" : "light")}-{width}", width, 1400);
    });

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Settings_SubTabs(int index) => WithTheme(false, false, () =>
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        vm.SelectedSettingsIndex = index;
        ScreenshotHelper.CapturePage(new NetworkPage { DataContext = vm }, $"design-settings{index}-light-520", 520, 1400);
    });

    [AvaloniaTheory]
    [InlineData("simple", false)]
    [InlineData("simple", true)]
    [InlineData("free", false)]
    [InlineData("subscribe", false)]
    public void Page_Russian360(string page, bool dark) => WithTheme(dark, true, () =>
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        vm.SetLanguageRussianCommand.Execute(null);
        var control = Page(page);
        control.DataContext = vm;
        ScreenshotHelper.CapturePage(control, $"design-{page}-ru-{(dark ? "dark" : "light")}-360", 360, 1400);
    });

    [AvaloniaTheory]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, false, 5)]
    [InlineData(false, true, 0)]
    public void Window(bool simple, bool dark, int tab) => WithTheme(dark, false, () =>
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        vm.IsSimpleMode = simple;
        if (!simple) vm.SelectedTabIndex = tab;
        var window = new MainWindow { Width = 520, Height = 760, DataContext = vm };
        ScreenshotHelper.Capture(window, $"design-window-{(simple ? "simple" : "advanced" + tab)}-{(dark ? "dark" : "light")}");
    });
}
