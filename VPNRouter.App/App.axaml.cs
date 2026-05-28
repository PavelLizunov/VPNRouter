using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views;
// Wave 12 Phase 3 (2026-05-18) — Avalonia 12 made the data annotations
// validation plugin disabled-by-default and Avalonia.Data.Core.Plugins
// internal. The pre-12 code below used to remove the plugin from
// BindingPlugins.DataValidators to avoid double-error messages from
// CommunityToolkit.Mvvm + DataAnnotations. With 12 the plugin is off
// out of the box so we drop the entire BindingPlugins block. No
// behavioural change because the plugin isn't running in either world.

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
            // v2.20.5: cap Skia's internal font cache so it doesn't grow
            // unbounded during a long session. Default behaviour keeps every
            // (typeface × pointSize × matrix) combination ever rendered,
            // which can swell the process by 10-30 MB over hours of use.
            // 4 MB bytes + 64 entries is plenty for our UI (Inter + mono +
            // a handful of sizes). When the limit is hit Skia evicts the
            // least-recent entry, re-upload happens lazily on next draw.
            // Based on research in plans/vpnrouter-memory-research.md.
            try
            {
                SkiaSharp.SKGraphics.SetFontCacheLimit(4 * 1024 * 1024);
                SkiaSharp.SKGraphics.SetFontCacheCountLimit(64);
            }
            catch { /* SkiaSharp native load failures aren't fatal; UI still renders */ }

            // Wave 12 Phase 3 (2026-05-18): the pre-12 BindingPlugins.DataValidators
            // de-registration of DataAnnotationsValidationPlugin lived here; in
            // Avalonia 12 the plugin is disabled by default and the plugins
            // collection is internal (CommunityToolkit.Mvvm + Avalonia 12 use
            // INotifyDataErrorInfo directly, no double-error problem). See
            // https://docs.avaloniaui.net/docs/avalonia12-breaking-changes for
            // the data-validation overhaul.

            // v2.22.0-r1: detect "update attempted but didn't land".
            // Logs a warning to the app log + (v2.29.0-r7+) hands it to
            // MainWindowViewModel via Program.PendingUpdateWarning so the
            // UI surfaces it as a dismissible banner. Pre-r7 was log-only
            // and got missed by users — failed updates went unnoticed
            // until the next attempted update also failed.
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
            catch { /* non-fatal */ }

            // v2.23.0: if launched with --safe, log it prominently so
            // a support request screenshot of the log makes the mode
            // obvious. UI banner binding uses MainWindowViewModel's
            // SafeModeBanner / IsSafeModeBanner properties.
            if (Program.SafeMode)
                Serilog.Log.Warning(
                    "[Startup] SAFE MODE active — user overrides disabled, forcing Full tunnel, " +
                    "using bundled catalogue only");

            // v2.23.0 crash detection: if the previous run left a stale
            // lock file, log it so the banner / support can pick it up.
            // Consumes the file regardless (deleted inside the helper).
            try
            {
                VPNRouter.Core.AppPaths.EnsureDirectories();
                var crashNotice = VPNRouter.Core.Services.LockFile
                    .DetectPreviousCrash(Serilog.Log.Logger);
                if (!string.IsNullOrEmpty(crashNotice))
                    Serilog.Log.Warning("[Startup] {Notice}", crashNotice);

                // Write our own lock so the NEXT run can detect if WE crash.
                VPNRouter.Core.Services.LockFile.Acquire(Serilog.Log.Logger);
            }
            catch { /* non-fatal */ }

            _viewModel = new MainWindowViewModel();
            var mainWindow = new MainWindow { DataContext = _viewModel };

#if PLATFORM_WINDOWS
            // v2.38.0-r2 — register the Explorer "route through VPN" context-menu
            // verb HERE, after the ViewModel constructor has run LoadSettingsIntoUI
            // (which sets Strings.Lang from the saved/OS language). r1 registered
            // it in Program.cs before settings load, so the verb label was pinned
            // to English even for RU users (user report 2026-05-28). Idempotent +
            // refreshed every launch → self-heals after an install-dir move or a
            // manual registry cleanup, and now picks up a language change on the
            // next launch too.
            // OperatingSystem.IsWindows() (in addition to the #if) keeps the
            // CA1416 platform analyzer happy — it tracks runtime guards, not
            // preprocessor symbols. Always true inside the Windows-only block.
            if (OperatingSystem.IsWindows())
                try
                {
                    // r4: pass the user's custom app-categories so the registrar
                    // shows a cascading "VPNRouter ▸" submenu when there's more
                    // than one (Custom Apps + named categories). ≤1 → flat verb,
                    // exactly as before (no regression for the common case).
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

            // Hide to tray on close instead of quitting
            mainWindow.Closing += (_, e) =>
            {
                e.Cancel = true;
                mainWindow.Hide();
            };

            // Cleanup sing-box on any kind of app exit
            desktop.ShutdownRequested += (_, _) =>
            {
                _viewModel?.QuitCommand.Execute(null);
                // v2.23.0: drop the crash-detection lock on clean exit so
                // the next run won't falsely think we crashed.
                try { VPNRouter.Core.Services.LockFile.Release(Serilog.Log.Logger); } catch { }
            };

            // Setup tray icon
            SetupTrayIcon(desktop);

            // v2.31.7-r2 — wire single-instance show-window handler.
            // Fired (on UI thread) when a second-instance launch signals
            // us via the named pipe. Brings the window out of any state
            // (minimised / hidden behind / on another virtual desktop)
            // to the foreground, working around Windows
            // ForegroundLockTimeout via WindowForegroundHelper.
            VPNRouter.App.Services.SingleInstance.ShowWindowRequested += () =>
            {
                VPNRouter.App.Services.WindowForegroundHelper.BringToFront(desktop.MainWindow);
            };

#if PLATFORM_WINDOWS
            // v2.38.0 — Explorer "route through VPN" context-menu verb. A
            // second launch with --route-app pipes the path here; resolve +
            // add to Custom Apps + toast in this running instance, then
            // surface the window so the user sees the result.
            VPNRouter.App.Services.SingleInstance.RouteAppRequested += (path, category) =>
            {
                try { _viewModel?.RouteAppFromShell(path, category); } catch { }
                VPNRouter.App.Services.WindowForegroundHelper.BringToFront(desktop.MainWindow);
            };
#endif

            // Release the single-instance Mutex on graceful shutdown so
            // the next launch sees a clean slot and can claim it.
            desktop.Exit += (_, _) =>
            {
                try { VPNRouter.App.Services.SingleInstance.Release(); } catch { }
            };

            // --minimized: start hidden in tray (autostart on logon)
            if (Program.StartMinimized)
                mainWindow.Hide();
            else
                mainWindow.Show();

#if PLATFORM_WINDOWS
            // v2.38.0 — if THIS instance was launched by the context-menu verb
            // (--route-app) with no other instance running, process the stashed
            // path now that the ViewModel + window are up.
            if (!string.IsNullOrEmpty(Program.PendingRouteAppPath))
            {
                var pending = Program.PendingRouteAppPath;
                var pendingCat = Program.PendingRouteAppCategory;
                Program.PendingRouteAppPath = null;
                Program.PendingRouteAppCategory = null;
                try { _viewModel?.RouteAppFromShell(pending, pendingCat); } catch { }
            }
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();

        var showItem = new NativeMenuItem("Settings...");
        showItem.Click += (_, _) =>
        {
            desktop.MainWindow?.Show();
            desktop.MainWindow?.Activate();
        };

        var connectItem = new NativeMenuItem("Connect");
        connectItem.Click += (_, _) => _viewModel?.ToggleConnectionCommand.Execute(null);

        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) => _viewModel?.QuitCommand.Execute(null);

        menu.Items.Add(showItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(connectItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quitItem);

        // Tray icon asset selection:
        //   • macOS + Windows → penguin_mascot.ico (black lineart on
        //     transparent) — renders fine on both light chrome title bars
        //     and the dark-ish macOS menu bar thanks to the platforms'
        //     subtle icon treatment.
        //   • Linux → penguin_mascot_white.ico (white lineart). Most
        //     distros default to a dark system panel (Mint Cinnamon, KDE
        //     Breeze Dark, GNOME with extensions, XFCE Arc-Dark). A black
        //     lineart on a dark panel is invisible — user reports confirmed.
        //     White shows up cleanly on dark AND stays legible (if soft)
        //     on light panels. A theme-aware icon would be nicer but
        //     StatusNotifierItem doesn't report panel brightness back.
        // v2.21.8.
        var trayIconUri = System.OperatingSystem.IsLinux()
            ? "avares://VPNRouter.App/Assets/penguin_mascot_white.ico"
            : "avares://VPNRouter.App/Assets/penguin_mascot.ico";
        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new System.Uri(trayIconUri))),
            ToolTipText = "VPNRouter",
            Menu = menu,
            IsVisible = true
        };

        // Update tray menu text based on connection state
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.IsConnected))
                {
                    connectItem.Header = _viewModel.IsConnected ? "Disconnect" : "Connect";
                    _trayIcon.ToolTipText = _viewModel.IsConnected
                        ? "VPNRouter - Connected" : "VPNRouter";
                }
            };
        }

        // Click on tray icon shows window
        _trayIcon.Clicked += (_, _) =>
        {
            desktop.MainWindow?.Show();
            desktop.MainWindow?.Activate();
        };
    }
}
