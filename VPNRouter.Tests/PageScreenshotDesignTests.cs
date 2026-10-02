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
        foreach (var page in new[] { "simple", "servers", "subscribe", "network", "apps", "tools", "dpi", "telegram", "free" })
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
        "apps" => new ApplicationsPage(),
        "tools" => new ToolsPage(),
        "dpi" => new DpiBypassPage(),
        "telegram" => new TelegramPage(),
        "free" => new FreeConfigsPage(),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    // The view model applies the saved theme when it is built, so the theme is set after it.
    private static void WithTheme(bool dark, bool russian, Action<MainWindowViewModel> action)
    {
        var app = Avalonia.Application.Current!;
        var previousTheme = app.RequestedThemeVariant;
        var previousLanguage = VPNRouter.App.Localization.Strings.Lang;
        try
        {
            using var vm = new MainWindowViewModel(new InMemorySettingsStore());
            if (russian) vm.SetLanguageRussianCommand.Execute(null);
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            action(vm);
        }
        finally
        {
            app.RequestedThemeVariant = previousTheme;
            VPNRouter.App.Localization.Strings.Lang = previousLanguage;
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Pages))]
    public void Page_InBothThemes(string page, bool dark, int width) => WithTheme(dark, false, vm =>
    {
        var control = Page(page);
        control.DataContext = vm;
        ScreenshotHelper.CapturePage(control, $"design-{page}-{(dark ? "dark" : "light")}-{width}", width, 1400);
    });

    // Applications with a category open (default apps plus one custom app), in full-tunnel mode and in the bypass list.
    [AvaloniaTheory]
    [InlineData("group", false, 520)]
    [InlineData("group", true, 520)]
    [InlineData("group", false, 360)]
    [InlineData("group-ru", false, 520)]
    [InlineData("full", false, 520)]
    [InlineData("exclude", true, 520)]
    public void Applications_States(string state, bool dark, int width) => WithTheme(dark, state.EndsWith("-ru"), vm =>
    {
        if (state == "full") vm.IsFullTunnel = true;
        if (state == "exclude") vm.AppsListEditorMode = "exclude";
        var group = vm.ActiveAppGroups.FirstOrDefault(g => g.Apps.Count > 0) ?? vm.ActiveAppGroups.FirstOrDefault();
        if (group is not null)
        {
            group.Apps.Add(new AppItemViewModel("MyGame.exe", isChecked: true, isCustom: true));
            if (group.Apps.Count > 1) group.Apps[0].IsChecked = true;
            vm.SelectedActiveAppGroup = group;
        }
        var page = new ApplicationsPage { DataContext = vm };
        ScreenshotHelper.CapturePage(page, $"design-apps-{state}-{(dark ? "dark" : "light")}-{width}", width, 900);
    });

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Settings_SubTabs(int index) => WithTheme(false, false, vm =>
    {
        vm.SelectedSettingsIndex = index;
        ScreenshotHelper.CapturePage(new NetworkPage { DataContext = vm }, $"design-settings{index}-light-520", 520, 1400);
    });

    [AvaloniaTheory]
    [InlineData("simple", false)]
    [InlineData("simple", true)]
    [InlineData("free", false)]
    [InlineData("subscribe", false)]
    public void Page_Russian360(string page, bool dark) => WithTheme(dark, true, vm =>
    {
        var control = Page(page);
        control.DataContext = vm;
        ScreenshotHelper.CapturePage(control, $"design-{page}-ru-{(dark ? "dark" : "light")}-360", 360, 1400);
    });

    [AvaloniaTheory]
    [InlineData(true, false, 0, 520)]
    [InlineData(true, true, 0, 520)]
    [InlineData(false, false, 0, 520)]
    [InlineData(false, false, 1, 520)]
    [InlineData(false, false, 5, 520)]
    [InlineData(false, true, 0, 520)]
    [InlineData(false, true, 2, 520)]
    [InlineData(false, false, 0, 360)]
    [InlineData(false, true, 5, 360)]
    public void Window(bool simple, bool dark, int tab, int width) => WithTheme(dark, false, vm =>
    {
        vm.IsSimpleMode = simple;
        if (!simple) vm.SelectedTabIndex = tab;
        var window = new MainWindow { Width = width, Height = 760, DataContext = vm };
        var suffix = width == 520 ? string.Empty : $"-{width}";
        ScreenshotHelper.Capture(window, $"design-window-{(simple ? "simple" : "advanced" + tab)}-{(dark ? "dark" : "light")}{suffix}");
    });
}
