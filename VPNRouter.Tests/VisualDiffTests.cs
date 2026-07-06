using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views.Pages;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Visual-diff regression for stable pages. Captures a fresh PNG via the
/// same harness <see cref="PageScreenshotTests"/> uses, then compares
/// pixel-by-pixel against a baseline pinned in
/// <c>screenshots/baseline/</c>. Fails if more than 2% of pixels differ
/// beyond the AA noise threshold.
///
/// <para>Why not all pages? Free Configs and Servers depend on cached
/// state (subscriptions, free-pool entries) that varies between machines
/// and runs. DPI Bypass, Telegram, and Tools are pure-static layouts —
/// their render is deterministic given the same headless VM-cache state,
/// so they make solid regression sentinels.</para>
///
/// <para>Why Windows-only? Headless Skia output diverges on Mac/Linux
/// due to font hinting and AA strategy. Maintaining one baseline per
/// platform triples the cost without proportional regression-coverage
/// gain — Windows is where the project is primarily developed and where
/// most UI changes land first. Mac/Linux CI still benefits from
/// <see cref="PageScreenshotTests"/> (which catches view-tree assembly
/// failures, missing bindings, broken templates) — just not from this
/// pixel-diff layer.</para>
///
/// <para>Refresh workflow when a page legitimately changes:
/// <code>
/// dotnet test --filter "FullyQualifiedName~PageScreenshotTests"
/// copy VPNRouter.Tests\screenshots\page-foo.png ^
///      VPNRouter.Tests\screenshots\baseline\page-foo.png
/// dotnet test --filter "FullyQualifiedName~VisualDiffTests"
/// </code>
/// Then commit the updated baseline alongside the UI change. The diff
/// shows up in the PR as a binary blob, but reviewers can open both PNGs
/// side-by-side and eyeball the intended change.</para>
/// </summary>
public class VisualDiffTests
{
    /// <summary>
    /// Tolerance for unintentional drift. 2% is generous enough to
    /// absorb AA noise from font-cache state changes between runs,
    /// strict enough to catch: a new control added, control removed,
    /// theme inverted, layout shifted by &gt;~2 px, or a font swap.
    /// </summary>
    private const double MaxDifferingFraction = 0.02;

    private static readonly string BaselineDir =
        Path.Combine(ScreenshotHelper.ScreenshotsDir, "baseline");

    // Reuse the same shared-VM trick PageScreenshotTests uses — keeps
    // MainWindowViewModel construction off the per-test critical path.
    private static MainWindowViewModel? _sharedVm;
    private static MainWindowViewModel GetVm() => _sharedVm ??= new MainWindowViewModel(new InMemorySettingsStore());

    private static void AssertMatchesBaseline(
        UserControl page,
        string name,
        int width = 1200,
        int height = 800)
    {
        // Cross-platform pixel-diff is infeasible (see class doc). Skip
        // silently on non-Windows so Mac/Linux CI still passes the
        // suite without us polluting their reports with platform-only
        // failures.
        if (!OperatingSystem.IsWindows()) return;

        var baselinePath = Path.Combine(BaselineDir, $"{name}.png");
        if (!File.Exists(baselinePath))
        {
            // Fresh checkout where the baseline was never committed, OR
            // someone added a new test class entry without pinning the
            // baseline. Fail with the literal command to fix it so the
            // dev doesn't have to reverse-engineer the harness.
            page.DataContext = GetVm();
            var pinSrc = ScreenshotHelper.CapturePage(page, name, width, height);
            Assert.Fail(
                $"No baseline for '{name}'. To pin the current render, run:\n" +
                $"  copy \"{pinSrc}\" \"{baselinePath}\"\n" +
                $"Then re-run this test and commit the baseline PNG.");
            return;
        }

        page.DataContext = GetVm();

        // Wave 12 Phase 3 (2026-05-18) — Avalonia 12 changed
        // RequestedThemeVariant="Default" semantics: pre-12 the headless
        // platform fell back to Light when no OS theme was readable; in 12
        // the platform actually queries the host OS theme (which on this
        // VM happens to be Dark). The baselines were captured in Light, so
        // we force the variant before each diff to keep them stable. Real
        // launches still respect user setting via MainWindowViewModel
        // .ApplyTheme(); this only affects deterministic-fixture diffing.
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
