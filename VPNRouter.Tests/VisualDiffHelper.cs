using SkiaSharp;

namespace VPNRouter.Tests;

/// <summary>
/// Pixel-tolerance diff for screenshot regression testing. Decodes two PNGs
/// via SkiaSharp (already a transitive dependency through Avalonia.Skia
/// in <see cref="TestAppBuilder"/>'s <c>UseSkia()</c> chain), compares
/// pixel-by-pixel with an intensity threshold, and reports the fraction
/// of differing pixels.
///
/// <para>Anti-aliasing noise typically diffs by 5–15 sum-RGB units per
/// pixel; visible regressions (control moved, theme inverted, text
/// changed) diff by 100+. The default 30-unit threshold sits between,
/// so AA jitter slips through but real layout drift trips the test.</para>
///
/// <para>Used by <see cref="VisualDiffTests"/>. Baselines live in
/// <c>screenshots/baseline/</c> and are committed to the repo. Live test
/// runs write to <c>screenshots/</c> directly (gitignored). On dimension
/// mismatch the result reports <see cref="DiffResult.DimensionsMatch"/>
/// = false and treats every pixel as differing — a resized window IS
/// the regression we want to catch.</para>
/// </summary>
public static class VisualDiffHelper
{
    public sealed class DiffResult
    {
        public int TotalPixels { get; init; }
        public int DifferingPixels { get; init; }
        public double DifferingFraction =>
            TotalPixels > 0 ? (double)DifferingPixels / TotalPixels : 0;
        public int BaselineWidth { get; init; }
        public int BaselineHeight { get; init; }
        public int ActualWidth { get; init; }
        public int ActualHeight { get; init; }
        public bool DimensionsMatch =>
            BaselineWidth == ActualWidth && BaselineHeight == ActualHeight;
    }

    /// <summary>
    /// Per-pixel diff. <paramref name="intensityThreshold"/> is the sum
    /// |R-R'| + |G-G'| + |B-B'| above which a pixel counts as "different".
    /// Alpha is ignored — opaque page renders coming out of
    /// <c>CaptureRenderedFrame</c> have a constant alpha channel.
    /// </summary>
    public static DiffResult Compare(
        string baselinePath,
        string actualPath,
        int intensityThreshold = 30)
    {
        if (!File.Exists(baselinePath))
            throw new FileNotFoundException("Baseline PNG missing", baselinePath);
        if (!File.Exists(actualPath))
            throw new FileNotFoundException("Actual PNG missing", actualPath);

        using var baseline = SKBitmap.Decode(baselinePath)
            ?? throw new InvalidOperationException(
                $"SkiaSharp could not decode '{baselinePath}' — file corrupt or not a PNG?");
        using var actual = SKBitmap.Decode(actualPath)
            ?? throw new InvalidOperationException(
                $"SkiaSharp could not decode '{actualPath}' — file corrupt or not a PNG?");

        if (baseline.Width != actual.Width || baseline.Height != actual.Height)
        {
            // Dimension mismatch counted as 100% diff. Caller can branch on
            // DimensionsMatch for a clearer error message, but the metric
            // stays consistent (≥ MaxDifferingFraction → fail).
            var totalBaseline = baseline.Width * baseline.Height;
            return new DiffResult
            {
                BaselineWidth = baseline.Width,
                BaselineHeight = baseline.Height,
                ActualWidth = actual.Width,
                ActualHeight = actual.Height,
                TotalPixels = totalBaseline,
                DifferingPixels = totalBaseline,
            };
        }

        var w = baseline.Width;
        var h = baseline.Height;
        int total = w * h;
        int differing = 0;

        // SKBitmap.Pixels allocates a fresh SKColor[width*height] — for our
        // worst case 1200x800 page that's ~3.7 MB transient, GC'd after
        // the test. Cheaper than mucking around with raw pin-buffer access
        // in a one-shot diff.
        var bp = baseline.Pixels;
        var ap = actual.Pixels;

        for (int i = 0; i < total; i++)
        {
            var b = bp[i];
            var a = ap[i];
            int delta =
                Math.Abs(b.Red - a.Red) +
                Math.Abs(b.Green - a.Green) +
                Math.Abs(b.Blue - a.Blue);
            if (delta > intensityThreshold) differing++;
        }

        return new DiffResult
        {
            BaselineWidth = w,
            BaselineHeight = h,
            ActualWidth = w,
            ActualHeight = h,
            TotalPixels = total,
            DifferingPixels = differing,
        };
    }
}
