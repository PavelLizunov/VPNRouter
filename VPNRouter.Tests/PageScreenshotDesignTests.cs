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
        ScreenshotHelper.CapturePage(control, $"design-{page}-{(dark ? "dark" : "light")}-{width}", width, 1400, appBackground: true);
    });

    // The About window and the four steps of the setup wizard.
    [AvaloniaTheory]
    [InlineData("about", false)]
    [InlineData("about", true)]
    [InlineData("wizard0", false)]
    [InlineData("wizard1", false)]
    [InlineData("wizard2", true)]
    [InlineData("wizard3", false)]
    public void Windows(string name, bool dark) => WithTheme(dark, false, vm =>
    {
        Window window;
        if (name == "about")
        {
            window = new AboutWindow { DataContext = vm };
        }
        else
        {
            var wizard = new SetupWizardViewModel(
                VPNRouter.Core.Models.TunSettings.DefaultMtu, true, (_, _) => { }, () => [],
                () => System.Threading.Tasks.Task.CompletedTask);
            wizard.CurrentStep = name[^1] - '0';
            window = new SetupWizardWindow(wizard);
        }
        ScreenshotHelper.Capture(window, $"design-{name}-{(dark ? "dark" : "light")}");
    });

    // Pages with content and their inner tabs: servers with probe results, a subscription, custom rules in the three
    // views, the Zapret and Telegram inner tabs, the saved free configs.
    [AvaloniaTheory]
    [InlineData("servers-list", false)]
    [InlineData("servers-list", true)]
    [InlineData("servers-custom", false)]
    [InlineData("subscribe-list", false)]
    [InlineData("subscribe-list", true)]
    [InlineData("rules-cards", false)]
    [InlineData("rules-read", true)]
    [InlineData("rules-edit", false)]
    [InlineData("dpi-tab1", false)]
    [InlineData("dpi-tab2", true)]
    [InlineData("dpi-tab3", false)]
    [InlineData("tg-tab1", false)]
    [InlineData("tg-tab2", true)]
    [InlineData("free-saved", false)]
    public void States(string state, bool dark) => WithTheme(dark, false, vm =>
    {
        UserControl page;
        switch (state)
        {
            case "servers-list":
            case "subscribe-list":
                var list = state == "servers-list" ? vm.Servers : vm.SubscriptionServers;
                string[] names = ["Frankfurt-01", "Amsterdam-02", "Helsinki-very-long-server-name-03"];
                for (var i = 0; i < names.Length; i++)
                {
                    var s = new ServerViewModel(new VPNRouter.Core.Models.VlessServerEntry
                        { Name = names[i], Server = $"10.0.0.{i + 1}", Port = 443 + i });
                    s.IsActive = i == 0;
                    list.Add(s);
                }
                if (state == "subscribe-list")
                    vm.Subscriptions.Add(new SubscriptionViewModel(new VPNRouter.Core.Models.SubscriptionEntry
                        { Name = "Provider", Url = "https://example.invalid/sub" }));
                page = state == "servers-list" ? new ServersPage() : new SubscribePage();
                break;
            case "servers-custom":
                vm.SelectedServerModeIndex = 1;
                page = new ServersPage();
                break;
            case "free-saved":
                vm.FreeConfigsVm.SelectedFreeTabIndex = 1;
                page = new FreeConfigsPage();
                break;
            case var r when r.StartsWith("rules-"):
                vm.SelectedSettingsIndex = 1;
                foreach (var value in new[] { ".example.com", "10.0.0.0/8", "steam.exe" })
                {
                    vm.NewRuleValue = value;
                    vm.AddCustomRuleFromFormCommand.Execute(null);
                }
                vm.RulesViewMode = r[6..];
                page = new NetworkPage();
                break;
            case var d when d.StartsWith("dpi-tab"):
                vm.SetZapretTabCommand.Execute(d[^1..]);
                page = new DpiBypassPage();
                break;
            default:
                vm.SetTgProxyTabCommand.Execute(state[^1..]);
                page = new TelegramPage();
                break;
        }
        page.DataContext = vm;
        ScreenshotHelper.CapturePage(page, $"design-state-{state}-{(dark ? "dark" : "light")}", 520, 1000, appBackground: true);
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
        ScreenshotHelper.CapturePage(page, $"design-apps-{state}-{(dark ? "dark" : "light")}-{width}", width, 900, appBackground: true);
    });

    [AvaloniaTheory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public void Settings_SubTabs(int index, bool dark) => WithTheme(dark, false, vm =>
    {
        vm.SelectedSettingsIndex = index;
        ScreenshotHelper.CapturePage(new NetworkPage { DataContext = vm }, $"design-settings{index}-{(dark ? "dark" : "light")}-520", 520, 1400, appBackground: true);
    });

    [AvaloniaTheory]
    [InlineData("simple", false)]
    [InlineData("simple", true)]
    [InlineData("free", false)]
    [InlineData("subscribe", false)]
    [InlineData("servers", false)]
    [InlineData("network", false)]
    [InlineData("apps", false)]
    [InlineData("dpi", true)]
    [InlineData("telegram", false)]
    public void Page_Russian360(string page, bool dark) => WithTheme(dark, true, vm =>
    {
        var control = Page(page);
        control.DataContext = vm;
        ScreenshotHelper.CapturePage(control, $"design-{page}-ru-{(dark ? "dark" : "light")}-360", 360, 1400, appBackground: true);
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
