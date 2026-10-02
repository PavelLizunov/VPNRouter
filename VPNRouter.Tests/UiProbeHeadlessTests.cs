using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using VPNRouter.Tests.Fakes;
using VPNRouter.Tools.UiProbe;

namespace VPNRouter.Tests;

// The engine behind the UI exploration MCP server (VPNRouter.Tools/UiProbe): every surface renders, and clicking through it
// (navigation only on Windows, see Sweeper.Refuse) raises no exception. The D-3 crash (render-pass invalidation on the
// Applications tab) is the kind of defect this is meant to catch before an owner does.
[Collection(SafeModeStateCollection.Name)]
public class UiProbeHeadlessTests
{
    public static TheoryData<string> SurfaceNames()
    {
        var data = new TheoryData<string>();
        foreach (var surface in Surfaces.All) data.Add(surface.Name);
        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(SurfaceNames))]
    public void EverySurface_Renders(string surface)
    {
        var result = Probe.Render(new ProbeOptions { Surface = surface, Height = 1200 });
        Assert.True(result.Png.Length > 1000, $"{surface}: image is suspiciously small");
    }

    [AvaloniaTheory]
    [MemberData(nameof(SurfaceNames))]
    public void EverySurface_ClickThroughRaisesNoException(string surface)
    {
        var result = Sweeper.Run(new SweepOptions
        {
            Probe = new ProbeOptions { Surface = surface, Height = 1400 },
            SpeedMs = 0,
            MaxSteps = 120,
            MaxSeconds = 60,
        });
        Assert.True(
            result.Failures.Count == 0,
            string.Join("\n", result.Failures.Select(f => $"{f.Element}: {f.Error}")));
    }

    [AvaloniaFact]
    public void FastTabSwitching_RaisesNoException()
    {
        var result = Sweeper.Run(new SweepOptions
        {
            Probe = new ProbeOptions { Surface = "window-advanced", Height = 900 },
            Mode = "monkey",
            SpeedMs = 0,
            RenderEvery = 7,
            MaxSteps = 200,
            MaxSeconds = 60,
            Seed = 12345,
        });
        Assert.True(
            result.Failures.Count == 0,
            string.Join("\n", result.Failures.Select(f => $"{f.Element}: {f.Error}")));
    }

    [AvaloniaFact]
    public void Lint_FlagsTextSymbolsAndClippedText()
    {
        var panel = new StackPanel { Width = 200 };
        panel.Children.Add(new TextBlock { Text = "⚡ free", Name = "symbol" });
        panel.Children.Add(new TextBlock
        {
            Text = "A very long line of text that cannot fit in two hundred pixels",
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            Name = "clipped",
        });
        var window = new Window { Width = 200, Height = 200, Content = panel };
        window.Show();
        try
        {
            window.UpdateLayout();
            var findings = LayoutLint.Run(window);
            Assert.Contains(findings, f => f.Rule == "symbol-glyph");
            Assert.Contains(findings, f => f.Rule == "text-clipped");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Steps_ClickAnInnerTabBeforeTheCapture()
    {
        var tree = Probe.Tree(new ProbeOptions { Surface = "window-advanced", Height = 900, Steps = new List<string> { "Settings" } }, 400);
        // After the click the Settings page is on screen: its section list ("Routing", "Rules", ...) is part of the tree.
        Assert.Contains("Routing", tree);
        var missing = Probe.Render(new ProbeOptions { Surface = "window-advanced", Height = 900, Steps = new List<string> { "No such tab" } });
        Assert.Contains(missing.Notes, n => n.Contains("No such tab", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Lint_FlagsTextCutOffByAClippingContainerAndOverlappingText()
    {
        var canvas = new Canvas { Width = 200, Height = 100 };
        var clipper = new Border { Width = 80, Height = 30, ClipToBounds = true };
        clipper.Child = new TextBlock
        {
            Text = "A label that is much wider than its box",
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            Width = 300,
        };
        Canvas.SetLeft(clipper, 10);
        Canvas.SetTop(clipper, 10);
        canvas.Children.Add(clipper);
        var first = new TextBlock { Text = "First line of text" };
        var second = new TextBlock { Text = "Second line of text" };
        Canvas.SetLeft(first, 10); Canvas.SetTop(first, 60);
        Canvas.SetLeft(second, 14); Canvas.SetTop(second, 62);
        canvas.Children.Add(first);
        canvas.Children.Add(second);

        var window = new Window { Width = 400, Height = 200, Content = canvas };
        window.Show();
        try
        {
            window.UpdateLayout();
            var findings = LayoutLint.Run(window);
            Assert.Contains(findings, f => f.Rule == "cut-off");
            Assert.Contains(findings, f => f.Rule == "overlap");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Matrix_BuildsOneSheet()
    {
        var matrix = Probe.Matrix(
            new ProbeOptions { Height = 700 },
            new[] { "simple", "apps" },
            new[] { "default" },
            new[] { "light", "dark" },
            new[] { "en" },
            new[] { 520 },
            2);
        Assert.Equal(4, matrix.Cells.Count);
        Assert.True(matrix.Sheet.Length > 1000);
    }
}
