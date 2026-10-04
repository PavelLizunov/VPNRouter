using Avalonia;
using Avalonia.Controls;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views;
using VPNRouter.App.Views.Pages;
using VPNRouter.Core.Services;

namespace VPNRouter.Tools.UiProbe;

// One thing a person can look at: a page placed in a bare window, or a whole window.
public sealed record Surface(string Name, string Kind, string Description, Func<MainWindowViewModel, Window> Create);

public static class Surfaces
{
    public static IReadOnlyList<Surface> All { get; } = Build();

    public static Surface? Find(string? name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<Surface> Build()
    {
        var list = new List<Surface>();

        void Page(string name, string description, Func<UserControl> make) =>
            list.Add(new Surface(name, "page", description, vm =>
            {
                var page = make();
                page.DataContext = vm;
                // The real main window sets these two; a bare window would draw a different background and a bigger default font.
                var host = new Window { Content = page, FontSize = 10 };
                host.Bind(Window.BackgroundProperty, host.GetResourceObservable("SurfaceAppBrush"));
                return host;
            }));

        Page("simple", "Simple mode home page (status hero, connect button)", () => new SimplePage());
        Page("servers", "Servers page (Servers / Custom Config)", () => new ServersPage());
        Page("subscribe", "Subscription page", () => new SubscribePage());
        Page("network", "Settings page (use state SelectedSettingsIndex 0..5 for its sections)", () => new NetworkPage());
        Page("apps", "Applications page (routing rules by application)", () => new ApplicationsPage());
        Page("tools", "Tools page (Zapret / Telegram proxy)", () => new ToolsPage());
        Page("dpi", "DPI bypass (Zapret) page", () => new DpiBypassPage());
        Page("telegram", "Telegram proxy page", () => new TelegramPage());
        Page("free", "Free configs page (Search / Saved)", () => new FreeConfigsPage());

        list.Add(new Surface("window-simple", "window", "Main window in simple mode", vm =>
        {
            vm.IsSimpleMode = true;
            return new MainWindow { DataContext = vm };
        }));

        list.Add(new Surface("window-advanced", "window",
            "Main window in advanced mode (use state SelectedTabIndex 0..N to pick the tab)", vm =>
            {
                vm.IsSimpleMode = false;
                return new MainWindow { DataContext = vm };
            }));

        list.Add(new Surface("about", "window", "About window", vm => new AboutWindow { DataContext = vm }));

        list.Add(new Surface("setup-wizard", "window", "First-run setup wizard", vm =>
        {
            var wizard = new SetupWizardViewModel(
                1400,
                true,
                (_, _) => { },
                () => Array.Empty<HealthCheck.Result>(),
                () => Task.CompletedTask);
            return new SetupWizardWindow(wizard);
        }));

        return list;
    }
}
