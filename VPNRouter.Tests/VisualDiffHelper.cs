using SkiaSharp;

namespace VPNRouter.Tests;

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
