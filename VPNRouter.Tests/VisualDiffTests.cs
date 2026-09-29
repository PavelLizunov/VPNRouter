using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views.Pages;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public class VisualDiffTests
{
    private const double MaxDifferingFraction = 0.02;

    private static readonly string BaselineDir =
        Path.Combine(ScreenshotHelper.ScreenshotsDir, "baseline");

    private static MainWindowViewModel GetVm() =>
        new(new InMemorySettingsStore());

    private static void AssertMatchesBaseline(
        UserControl page,
        string name,
        int width = 1200,
        int height = 800)
    {
        if (!OperatingSystem.IsWindows()) return;

        var baselinePath = Path.Combine(BaselineDir, $"{name}.png");
        if (!File.Exists(baselinePath))
        {
            using var missingBaselineVm = GetVm();
            page.DataContext = missingBaselineVm;
            var pinSrc = ScreenshotHelper.CapturePage(page, name, width, height);
            Assert.Fail(
                $"No baseline for '{name}'. To pin the current render, run:\n" +
                $"  copy \"{pinSrc}\" \"{baselinePath}\"\n" +
                $"Then re-run this test and commit the baseline PNG.");
            return;
        }

        using var vm = GetVm();
        if (name is "page-dpi-bypass" or "page-tools")
        {
            vm.ZapretEnabled = false;
            vm.ZapretStatus = VPNRouter.App.Localization.Strings.Stopped;
            vm.ZapretVersionText = "1.9.8c";
        }
        page.DataContext = vm;

        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
        }

        var actualPath = ScreenshotHelper.CapturePage(page, name, width, height);

        var diff = VisualDiffHelper.Compare(baselinePath, actualPath);

        Assert.True(
            diff.DimensionsMatch,
            $"Dimensions mismatch for '{name}': baseline is " +
            $"{diff.BaselineWidth}x{diff.BaselineHeight}, actual is " +
            $"{diff.ActualWidth}x{diff.ActualHeight}. " +
            $"Either CapturePage args drifted or the page's intrinsic " +
            $"size changed.");

        Assert.True(
            diff.DifferingFraction <= MaxDifferingFraction,
            $"Visual diff for '{name}' = {diff.DifferingFraction:P2} " +
            $"(threshold {MaxDifferingFraction:P0}). " +
            $"Differing pixels: {diff.DifferingPixels}/{diff.TotalPixels}. " +
            $"Inspect:\n" +
            $"  actual:   {actualPath}\n" +
            $"  baseline: {baselinePath}");
    }

    [AvaloniaFact]
    public void DpiBypassPage_MatchesBaseline()
        => AssertMatchesBaseline(new DpiBypassPage(), "page-dpi-bypass");

    [AvaloniaFact]
    public void TelegramPage_MatchesBaseline()
        => AssertMatchesBaseline(new TelegramPage(), "page-telegram");

    [AvaloniaFact]
    public void ToolsPage_MatchesBaseline()
        => AssertMatchesBaseline(new ToolsPage(), "page-tools");
}
