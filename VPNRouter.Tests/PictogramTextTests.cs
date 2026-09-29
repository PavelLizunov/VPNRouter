using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Controls;

namespace VPNRouter.Tests;

public class PictogramTextTests
{
    private static string[] Ids(TextBlock text) => text.Inlines!
        .OfType<InlineUIContainer>().Select(x => Assert.IsType<Pictogram>(x.Child).Id).ToArray();
    private static string Runs(TextBlock text) => string.Concat(text.Inlines!.OfType<Run>().Select(x => x.Text));

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
}
