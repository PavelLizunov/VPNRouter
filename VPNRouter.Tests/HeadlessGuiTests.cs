using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
}
