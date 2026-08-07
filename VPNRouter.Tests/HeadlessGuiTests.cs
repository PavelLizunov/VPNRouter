using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using VPNRouter.App.Views;

namespace VPNRouter.Tests;

/// <summary>
/// Smoke tests for the Avalonia GUI driven from the headless test runner.
/// These don't exercise real VPN behaviour — they verify the view tree
/// assembles without exceptions, resource lookups resolve, and basic
/// interactive surface area is present. Each test owns its window and
/// disposes at the end so Avalonia's dispatcher stays clean between runs.
///
/// <para>To extend: add an <see cref="AvaloniaFactAttribute"/>-decorated
/// method, new a window/UserControl, assert on its runtime state (Title,
/// FindControl, bindings). Anything that needs the full VpnEngine should
/// live in the separate runtime integration suite instead — these tests
/// run on any host (CI, headless VM) with no admin / no sing-box.</para>
/// </summary>
public class HeadlessGuiTests
{
    /// <summary>
    /// The baseline sanity check: can we even construct MainWindow without
    /// an exception? If this breaks, something in App.axaml's style/resource
    /// graph or a page's x:Class initialiser has regressed — usually a XAML
    /// typo or a missing DI binding exposed by ViewLocator.
    /// </summary>
    [AvaloniaFact]
    public void MainWindow_Opens_WithoutExceptions()
    {
        var window = new MainWindow();
        Assert.NotNull(window);
        Assert.IsType<MainWindow>(window);
    }

    /// <summary>
    /// Show MainWindow and confirm the view tree actually measures to a
    /// non-zero size — i.e. content templates resolved, no dangling
    /// StaticResource lookups and no layout-blocking exception. Title is
    /// intentionally blank in XAML (custom chrome), so we assert on Bounds
    /// instead.
    /// </summary>
    [AvaloniaFact]
    public void MainWindow_Shows_WithNonZeroBounds()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            Assert.True(window.Bounds.Width > 0,
                $"MainWindow.Bounds.Width should be positive after Show(), got {window.Bounds.Width}");
            Assert.True(window.Bounds.Height > 0,
                $"MainWindow.Bounds.Height should be positive after Show(), got {window.Bounds.Height}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Smoke-test the AboutWindow which is the simplest secondary view —
    /// if this breaks it's usually because an assembly-info field used in
    /// AppVersion / AppBranding has changed signature without updating the
    /// XAML binding.
    /// </summary>
    [AvaloniaFact]
    public void AboutWindow_Opens_WithoutExceptions()
    {
        var window = new AboutWindow();
        Assert.NotNull(window);
    }

    [AvaloniaFact]
    public void SetupWizardWindow_Shows_WithBindingsResolved()
    {
        var viewModel = new VPNRouter.App.ViewModels.SetupWizardViewModel(
            VPNRouter.Core.Models.TunSettings.DefaultMtu,
            true,
            (_, _) => { },
            () => [],
            () => System.Threading.Tasks.Task.CompletedTask);
        var window = new SetupWizardWindow(viewModel);
        try
        {
            window.Show();
            Assert.True(window.Bounds.Width > 0);
            Assert.True(window.Bounds.Height > 0);
            Assert.Equal(viewModel.TitleText, window.Title);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Captures the full MainWindow at three widths to repro the v2.27.0-r2
    /// user report: "обводки/фоны у отдельных лейблов (VPN/Zapret/TG pills,
    /// Режим chip и пр.) по-разному скрываются при сужении". Screenshots
    /// let us see exactly which pill clips first and at which width, then
    /// pin MinWidth / Margin / ClipToBounds on the offender.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(520, "mainwindow-520")]
    [InlineData(440, "mainwindow-440")]
    [InlineData(360, "mainwindow-360")]
    [InlineData(300, "mainwindow-300")]
    public void MainWindow_FullApp_Narrow(int width, string name)
    {
        var window = new MainWindow { Width = width, Height = 700 };
        window.DataContext = new VPNRouter.App.ViewModels.MainWindowViewModel();
        ScreenshotHelper.Capture(window, name);
    }

    /// <summary>
    /// Demonstrates real input simulation: programmatically click a button
    /// inside a hosted UserControl and assert on the resulting state change.
    /// Uses a tiny test-only window so we're not pulling in MainWindow's
    /// full dependency graph — the point here is to prove the harness can
    /// route Avalonia input events end-to-end, not to exercise production UI.
    /// Real coverage for production pages lives in dedicated per-page test
    /// classes (see e.g. <see cref="MainWindowViewModelTests"/>).
    /// </summary>
    [AvaloniaFact]
    public void Button_Click_InputRouting_Works()
    {
        var clickCount = 0;
        var button = new Button { Content = "Test", Name = "TestBtn" };
        button.Click += (_, _) => clickCount++;

        var window = new Window
        {
            Width = 200,
            Height = 100,
            Content = button
        };

        try
        {
            window.Show();

            // Simulate a real click via Avalonia's RoutedEvent pipeline —
            // same route production Button.OnPointerReleased takes to raise
            // Click, so handlers + ICommand bindings all fire exactly like
            // a user tap would. For actual mouse positioning (hit-testing
            // overlays, etc.) use HeadlessWindowExtensions.MouseDown/Up.
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(1, clickCount);
        }
        finally
        {
            window.Close();
        }
    }
}
