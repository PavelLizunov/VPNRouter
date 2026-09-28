namespace VPNRouter.Tests;

public sealed class NetworkPageLayoutTests
{
    [Fact]
    public void ReadModeRuleRow_NarrowVariant_KeepsDeleteReachable()
    {
        var src = ReadModeSection();

        Assert.Equal(3, Count(src, "ColumnDefinitions=\"20,70,140,*,Auto\""));
        Assert.Equal(3, Count(src, "ColumnDefinitions=\"*,Auto\""));
        Assert.Equal(3, Count(src, "IsVisible=\"{Binding !$parent[UserControl].((vm:MainWindowViewModel)DataContext).IsRulesNarrow}\""));
        Assert.Equal(3, Count(src, "IsVisible=\"{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).IsRulesNarrow}\""));
        Assert.Equal(6, Count(src, "Command=\"{Binding RemoveCommand}\""));
        Assert.Equal(6, Count(src, "icons:PictogramText.Icons=\"delete\""));
        Assert.Equal(3, Count(src, "Grid.Column=\"1\" icons:PictogramText.Icons=\"delete\""));
        Assert.DoesNotContain("HorizontalScrollBarVisibility=\"Auto\"", src);
        Assert.DoesNotContain("HorizontalScrollBarVisibility=\"Visible\"", src);
    }

    private static string ReadModeSection()
    {
        var src = File.ReadAllText(FindNetworkPage());
        var start = src.IndexOf("Read view (read-only grouped monospace)", StringComparison.Ordinal);
        var end = src.IndexOf("Edit view (Text mode)", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "NetworkPage.axaml read-mode section markers not found.");
        return src.Substring(start, end - start);
    }

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = 0; (i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0; i += needle.Length)
            n++;
        return n;
    }

    private static string FindNetworkPage()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "VPNRouter.App", "Views", "Pages", "NetworkPage.axaml");
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException("Could not locate VPNRouter.App/Views/Pages/NetworkPage.axaml.");
    }
}
