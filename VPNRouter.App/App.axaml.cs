using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views;

namespace VPNRouter.App;

public partial class App : Application
{
    private MainWindowViewModel? _viewModel;
    private TrayIcon? _trayIcon;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                SkiaSharp.SKGraphics.SetFontCacheLimit(4 * 1024 * 1024);
                SkiaSharp.SKGraphics.SetFontCacheCountLimit(64);
            }
            catch { }

            try
            {
                var receiptWarn = VPNRouter.Core.Services.UpdateChecker
                    .CheckInstallReceipt(VPNRouter.Core.AppVersion.Version);
                if (!string.IsNullOrEmpty(receiptWarn))
                {
                    Serilog.Log.Warning("[Startup] {Warning}", receiptWarn);
                    Program.PendingUpdateWarning = receiptWarn;
                }
            }
            catch { }

            if (Program.SafeMode)
                Serilog.Log.Warning(
                    "[Startup] SAFE MODE active — user overrides disabled, forcing Full tunnel, " +
                    "using bundled catalogue only");

            try
            {
                VPNRouter.Core.AppPaths.EnsureDirectories();
                var crashNotice = VPNRouter.Core.Services.LockFile
                    .DetectPreviousCrash(Serilog.Log.Logger);
                if (!string.IsNullOrEmpty(crashNotice))
                    Serilog.Log.Warning("[Startup] {Notice}", crashNotice);

                VPNRouter.Core.Services.LockFile.Acquire(Serilog.Log.Logger);
            }
            catch { }

            _viewModel = new MainWindowViewModel();
            var mainWindow = new MainWindow { DataContext = _viewModel };

#if PLATFORM_WINDOWS
            if (OperatingSystem.IsWindows())
                try
                {
                    var cats = _viewModel.AppGroups
                        .Where(g => g.IsCustomGroup || g.IsCustomCategory)
                        .Select(g => g.Name)
                        .ToList();
                    VPNRouter.App.Services.ShellMenuRegistrar.Register(cats, Serilog.Log.Logger);
                }
                catch { }
#endif

            desktop.MainWindow = mainWindow;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            VPNRouter.App.Services.AppAutomationDriver.StartIfConfigured(mainWindow, _viewModel);

            mainWindow.Closing += (_, e) =>
            {
                e.Cancel = true;
                mainWindow.Hide();
            };

            desktop.ShutdownRequested += (_, _) =>
            {
                VPNRouter.App.Services.AppAutomationDriver.Stop();
                _viewModel?.QuitCommand.Execute(null);
                try { VPNRouter.Core.Services.LockFile.Release(Serilog.Log.Logger); } catch { }
            };

            SetupTrayIcon(desktop);

            VPNRouter.App.Services.SingleInstance.ShowWindowRequested += () =>
            {
                VPNRouter.App.Services.WindowForegroundHelper.BringToFront(desktop.MainWindow);
            };

#if PLATFORM_WINDOWS
            VPNRouter.App.Services.SingleInstance.RouteAppRequested += (path, category) =>
            {
                try { _viewModel?.RouteAppFromShell(path, category); } catch { }
                VPNRouter.App.Services.WindowForegroundHelper.BringToFront(desktop.MainWindow);
            };

            VPNRouter.App.Services.SingleInstance.UnrouteAppRequested += path =>
            {
                try { _viewModel?.UnrouteAppFromShell(path); } catch { }
                VPNRouter.App.Services.WindowForegroundHelper.BringToFront(desktop.MainWindow);
            };
#endif

            desktop.Exit += (_, _) =>
            {
                try { VPNRouter.App.Services.SingleInstance.Release(); } catch { }
            };

            if (Program.StartMinimized)
                mainWindow.Hide();
            else
                mainWindow.Show();

#if PLATFORM_WINDOWS
            if (!string.IsNullOrEmpty(Program.PendingRouteAppPath))
            {
                var pending = Program.PendingRouteAppPath;
                var pendingCat = Program.PendingRouteAppCategory;
                Program.PendingRouteAppPath = null;
                Program.PendingRouteAppCategory = null;
                try { _viewModel?.RouteAppFromShell(pending, pendingCat); } catch { }
            }

            if (!string.IsNullOrEmpty(Program.PendingUnrouteAppPath))
            {
                var pendingUn = Program.PendingUnrouteAppPath;
                Program.PendingUnrouteAppPath = null;
                try { _viewModel?.UnrouteAppFromShell(pendingUn); } catch { }
            }
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();

        var showItem = new NativeMenuItem(Localization.Strings.TraySettings);
        showItem.Click += (_, _) =>
        {
            desktop.MainWindow?.Show();
            desktop.MainWindow?.Activate();
        };

        var connectItem = new NativeMenuItem(Localization.Strings.TrayStart);
        connectItem.Click += (_, _) => _viewModel?.ToggleConnectionCommand.Execute(null);

        var quitItem = new NativeMenuItem(Localization.Strings.TrayExit);
        quitItem.Click += (_, _) => _viewModel?.QuitCommand.Execute(null);

        menu.Items.Add(showItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(connectItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quitItem);

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new System.Uri(GetTrayIconUri(ActualThemeVariant)))),
            ToolTipText = "VPNRouter",
            Menu = menu,
            IsVisible = true
        };

        ActualThemeVariantChanged += (_, _) =>
        {
            try
            {
                if (_trayIcon != null)
                {
                    _trayIcon.Icon = new WindowIcon(AssetLoader.Open(new System.Uri(GetTrayIconUri(ActualThemeVariant))));
                }
            }
            catch { }
        };

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += (_, e) =>
            {
                // The tray menu follows the connection state and the language (a language switch raises "" or IsRussian).
                if (e.PropertyName is nameof(MainWindowViewModel.IsConnected) or nameof(MainWindowViewModel.IsRussian) or "" or null)
                {
                    showItem.Header = Localization.Strings.TraySettings;
                    quitItem.Header = Localization.Strings.TrayExit;
                    connectItem.Header = _viewModel.IsConnected ? Localization.Strings.TrayStop : Localization.Strings.TrayStart;
                    _trayIcon.ToolTipText = _viewModel.IsConnected
                        ? "VPNRouter - Connected" : "VPNRouter";
                }
            };
        }

        _trayIcon.Clicked += (_, _) =>
        {
            desktop.MainWindow?.Show();
            desktop.MainWindow?.Activate();
        };
    }

    internal static string GetTrayIconUri(Avalonia.Styling.ThemeVariant theme)
    {
        bool useWhite = System.OperatingSystem.IsLinux() ||
                        (System.OperatingSystem.IsMacOS() && theme == Avalonia.Styling.ThemeVariant.Dark);

        return useWhite
            ? "avares://VPNRouter.App/Assets/penguin_mascot_white.ico"
            : "avares://VPNRouter.App/Assets/penguin_mascot.ico";
    }
}
