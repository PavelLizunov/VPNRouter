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
        if (VPNRouter.App.Services.UiMotion.Allowed())
            Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new System.Uri("avares://VPNRouter.App/"))
            {
                Source = new System.Uri("avares://VPNRouter.App/Styles/Motion.axaml")
            });
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
                if (e.CloseReason == WindowCloseReason.WindowClosing)
                    HideToTray(mainWindow);
                else
                    mainWindow.Hide();
            };

            desktop.ShutdownRequested += (_, _) =>
            {
                VPNRouter.App.Services.AppAutomationDriver.Stop();
                _viewModel?.QuitCommand.Execute(null);
                try { VPNRouter.Core.Services.LockFile.Release(Serilog.Log.Logger); } catch { }
            };

            SetupTrayIcon(desktop);
            StartTrayWatch(desktop);

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
        // BringToFront also restores a window the tray guard minimized to the taskbar.
        showItem.Click += (_, _) => VPNRouter.App.Services.WindowForegroundHelper.BringToFront(desktop.MainWindow);

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
        // Since Avalonia 12.1.3 a tray icon reaches the OS only once it is in the application's icon list; a TrayIcon that is only constructed shows
        // nothing, and closing the window then left no way back to it.
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });

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

        _trayIcon.Clicked += (_, _) => VPNRouter.App.Services.WindowForegroundHelper.BringToFront(desktop.MainWindow);
    }

    // The user closed the window: hide it to the tray only while the shell really shows our icon, otherwise keep it on the taskbar (see TrayGuard).
    private void HideToTray(Window window)
    {
        var registered = _trayIcon is null ? null : VPNRouter.App.Services.TrayGuard.EnsureIconRegistered(_trayIcon, Serilog.Log.Logger);
        if (VPNRouter.App.Services.TrayGuard.DecideClose(registered) == VPNRouter.App.Services.TrayCloseAction.MinimizeToTaskbar)
        {
            Serilog.Log.Warning("[Tray] the icon cannot be added to the notification area; window minimized to the taskbar instead of hidden");
            window.WindowState = WindowState.Minimized;
            return;
        }

        window.Hide();
        Serilog.Log.Information("[Tray] window hidden to the tray (icon in the notification area: {Registered})", registered?.ToString() ?? "unknown");

        if (VPNRouter.App.Services.TrayGuard.ShouldShowHint(registered, VPNRouter.App.Services.TrayGuard.HintShownBefore()))
        {
            var shown = VPNRouter.App.Services.TrayGuard.ShowHint(Localization.Strings.TrayHintTitle, Localization.Strings.TrayHintBody);
            if (shown) VPNRouter.App.Services.TrayGuard.MarkHintShown();
            Serilog.Log.Information("[Tray] one-time tray hint shown: {Shown}", shown);
        }
    }

    // A lost icon (a shell that was not ready at autostart) is re-added; if it cannot be, a window hidden at start is put on the taskbar.
    private void StartTrayWatch(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var trayIcon = _trayIcon;
        if (!System.OperatingSystem.IsWindows() || trayIcon is null) return;

        bool? lastState = null;
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) =>
        {
            timer.Interval = System.TimeSpan.FromMinutes(2);
            var state = VPNRouter.App.Services.TrayGuard.EnsureIconRegistered(trayIcon, Serilog.Log.Logger);
            if (state != lastState)
            {
                Serilog.Log.Information("[Tray] watch: icon in the notification area: {State}", state?.ToString() ?? "unknown");
                lastState = state;
            }

            var window = desktop.MainWindow;
            if (state == false && window is { IsVisible: false })
            {
                window.WindowState = WindowState.Minimized;
                window.Show();
                Serilog.Log.Warning("[Tray] the icon cannot be added; the hidden window is shown minimized on the taskbar");
            }
        };
        timer.Start();
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
