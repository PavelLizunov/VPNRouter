using System.Runtime.CompilerServices;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using VPNRouter.App.Controls;

namespace VPNRouter.Tests;

public class PictogramTextTests
{
    private static string[] Ids(TextBlock text) => text.Inlines!
        .OfType<InlineUIContainer>().Select(x => Assert.IsType<Pictogram>(x.Child).Id!).ToArray();
    private static string Runs(TextBlock text) => string.Concat(text.Inlines!.OfType<Run>().Select(x => x.Text));

    [AvaloniaFact]
    public void InlineIconsAreMeasuredAtZeroWidthSoARenderBeforeLayoutFindsThemUnchanged()
    {
        // A fresh icon whose first measure happens inside TextBlock.RenderCore changes its DesiredSize there and Avalonia
        // throws "Visual was invalidated during the render pass" (the crash on the Applications tab).
        var text = new TextBlock { FontSize = 11 };
        PictogramText.SetIcons(text, "check,warning");
        PictogramText.SetText(text, "\u2713 ok \u26a0");
        var icons = text.Inlines!.OfType<InlineUIContainer>().Select(x => Assert.IsType<Pictogram>(x.Child)).ToArray();
        Assert.Equal(2, icons.Length);
        // InlineUIContainer measures with the block width, which is 0 for a text block that was never laid out.
        Assert.All(icons, icon =>
        {
            var before = icon.DesiredSize;
            icon.Measure(new Avalonia.Size(0, double.PositiveInfinity));
            Assert.Equal(before, icon.DesiredSize);
            Assert.Equal(11, icon.DesiredSize.Height);
        });
    }

    [AvaloniaFact]
    public void ExplicitSemanticsKeepOriginalAccessibleNameAndUpdate()
    {
        var text = new TextBlock();
        PictogramText.SetIcons(text, "verified,check,error");
        PictogramText.SetText(text, "42 ms \u2713\u2713");
        Assert.Equal(new[] { "verified" }, Ids(text));
        Assert.Equal("42 ms ", Runs(text));
        Assert.Equal("42 ms \u2713\u2713", AutomationProperties.GetName(text));
        PictogramText.SetText(text, "\u2717");
        Assert.Equal(new[] { "error" }, Ids(text));
        Assert.Equal("\u2717", AutomationProperties.GetName(text));
    }

    [AvaloniaFact]
    public void PrefixOnlyPreservesUserSuppliedSymbols()
    {
        var text = new TextBlock();
        PictogramText.SetIcons(text, "warning,arrow-right");
        PictogramText.SetPrefixOnly(text, true);
        PictogramText.SetText(text, "\u26a0 Server A \u2192 B!");
        Assert.Equal(new[] { "warning" }, Ids(text));
        Assert.Equal(" Server A \u2192 B!", Runs(text));
        PictogramText.SetText(text, "Connected [split] \u2192 Server A \u2192 B");
        Assert.Equal(new[] { "arrow-right" }, Ids(text));
        Assert.Equal("Connected [split]  Server A \u2192 B", Runs(text));
    }

    [AvaloniaFact]
    public void PrefixOnlyStillDrawsTheVerifiedMarkInParentheses()
    {
        var text = new TextBlock();
        PictogramText.SetIcons(text, "verified,check,warning,error");
        PictogramText.SetPrefixOnly(text, true);
        PictogramText.SetText(text, "Wait for the config to be verified (\u2713\u2713) before connecting.");
        Assert.Equal(new[] { "verified" }, Ids(text));
        Assert.Equal("Wait for the config to be verified () before connecting.", Runs(text));
        PictogramText.SetText(text, "Done: 3 truly working found (\u2713\u2713)");
        Assert.Equal(new[] { "verified" }, Ids(text));
        PictogramText.SetText(text, "Server \u2713\u2713 ok");
        Assert.Empty(Ids(text));
    }

    [AvaloniaFact]
    public void UnmappedTextAndExplicitAccessibleNameRemainUntouched()
    {
        var text = new TextBlock();
        AutomationProperties.SetName(text, "Remove server");
        PictogramText.SetIcons(text, "delete");
        PictogramText.SetText(text, "A + B");
        Assert.Empty(Ids(text));
        Assert.Equal("A + B", Runs(text));
        Assert.Equal("Remove server", AutomationProperties.GetName(text));
        PictogramText.SetText(text, "\u2715");
        Assert.Equal(new[] { "delete" }, Ids(text));
        Assert.Equal("Remove server", AutomationProperties.GetName(text));
    }

    [AvaloniaFact]
    public void RepeatedTextUpdates_DoNotRetainAbandonedPictogramControls()
    {
        var text = new TextBlock { FontSize = 12, Foreground = Brushes.White };
        PictogramText.SetIcons(text, "arrow-down,arrow-up");

        var abandoned = UpdateAndCaptureAbandonedIcons(text, iterations: 200);
        AssertAbandonedIconsReleased(abandoned);
        GC.KeepAlive(text);
    }

    [AvaloniaFact]
    public void RenderedTextUpdates_DoNotRetainAbandonedPictogramControls()
    {
        var text = new TextBlock { FontSize = 12, Foreground = Brushes.White };
        PictogramText.SetIcons(text, "arrow-down,arrow-up");
        var window = new Window { Width = 240, Height = 120, Content = text };
        window.Show();
        try
        {
            var abandoned = UpdateAndCaptureAbandonedIcons(text, iterations: 8, render: () =>
            {
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
            });
            AssertAbandonedIconsReleased(abandoned);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertAbandonedIconsReleased(List<WeakReference<Pictogram>> abandoned)
    {
        for (var i = 0; i < 4; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.NotEmpty(abandoned);
        Assert.All(abandoned, weak => Assert.False(weak.TryGetTarget(out _)));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference<Pictogram>> UpdateAndCaptureAbandonedIcons(TextBlock text, int iterations,
        Action? render = null)
    {
        var abandoned = new List<WeakReference<Pictogram>>(iterations * 2);
        for (var i = 0; i < iterations; i++)
        {
            PictogramText.SetText(text, $"\u2193 {i} KB/s \u2191 {i + 1} KB/s");
            render?.Invoke();
            foreach (var inline in text.Inlines!)
            {
                if (inline is InlineUIContainer { Child: Pictogram icon })
                    abandoned.Add(new WeakReference<Pictogram>(icon));
            }
        }

        // Replace with a final value so every captured Pictogram above is abandoned.
        PictogramText.SetText(text, "\u2193 final \u2191 final");
        render?.Invoke();
        return abandoned;
    }

    [AvaloniaFact]
    public void ForegroundChange_DirectAndInherited_UpdatesInlinePictograms()
    {
        var direct = new TextBlock { Foreground = Brushes.Red };
        PictogramText.SetIcons(direct, "arrow-down,arrow-up");
        PictogramText.SetText(direct, "\u2193 1 \u2191 2");
        var directIcons = direct.Inlines!.OfType<InlineUIContainer>().Select(x => Assert.IsType<Pictogram>(x.Child)).ToArray();
        Assert.Equal(2, directIcons.Length);
        Assert.All(directIcons, icon => Assert.Same(Brushes.Red, icon.Foreground));

        direct.Foreground = Brushes.Blue;
        Assert.All(directIcons, icon => Assert.Same(Brushes.Blue, icon.Foreground));

        var child = new TextBlock();
        var host = new ContentControl { Content = child };
        var window = new Window { Content = host };
        window.Show();
        try
        {
            PictogramText.SetIcons(child, "check");
            PictogramText.SetText(child, "\u2713 ok");
            TextElement.SetForeground(host, Brushes.Green);
            var inheritedIcon = Assert.Single(child.Inlines!.OfType<InlineUIContainer>().Select(x => Assert.IsType<Pictogram>(x.Child)));
            Assert.Same(Brushes.Green, inheritedIcon.Foreground);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ContentAndTipUpdates_ReuseOwnedTextBlock()
    {
        var button = new Button();
        PictogramText.SetIcons(button, "play,stop");
        PictogramText.SetContent(button, "\u25b6 Start");
        var firstContent = Assert.IsType<TextBlock>(button.Content);
        Assert.Equal(new[] { "play" }, Ids(firstContent));

        PictogramText.SetContent(button, "\u23f9 Stop");
        Assert.Same(firstContent, button.Content);
        Assert.Equal(new[] { "stop" }, Ids(firstContent));
        Assert.Equal("\u23f9 Stop", AutomationProperties.GetName(button));

        PictogramText.SetTip(button, "\u2713 ready");
        var firstTip = Assert.IsType<TextBlock>(ToolTip.GetTip(button));
        Assert.Equal(new[] { "check" }, Ids(firstTip));

        PictogramText.SetTip(button, "\u26a0 warn");
        Assert.Same(firstTip, ToolTip.GetTip(button));
        Assert.Equal(new[] { "warning" }, Ids(firstTip));
    }
}
