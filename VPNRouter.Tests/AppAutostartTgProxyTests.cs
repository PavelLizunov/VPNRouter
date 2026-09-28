using System.IO;
using System.Linq;

namespace VPNRouter.Tests;

public sealed class AppAutostartTgProxyTests
{
    [Fact]
    public void BootstrapFile_ExistsAndDeclaresPartialClass()
    {
        var src = LoadSource(
            "VPNRouter.App", "ViewModels", "MainWindowViewModel.AutostartBootstrap.cs");
        if (src == null) return;

        Assert.Contains("partial class MainWindowViewModel", src);
        Assert.Contains("namespace VPNRouter.App.ViewModels", src);

        Assert.Contains("BootstrapAutostartAsync", src);
        Assert.Contains("TryAutostartTgProxyAsync", src);
        Assert.Contains("TryAutostartZapretAsync", src);
    }

    [Fact]
    public void Bootstrap_ChecksAutostartTgProxyFlag_BeforeSpawning()
    {
        var src = LoadSource(
            "VPNRouter.App", "ViewModels", "MainWindowViewModel.AutostartBootstrap.cs");
        if (src == null) return;

        var stripped = StripLineComments(src);

        Assert.Contains("if (!AutostartTgProxy)", stripped);

        Assert.Contains("if (!AutostartZapret)", stripped);

        Assert.Matches(
            @"manager\.Start\s*\(\s*TgProxyPort\s*,\s*TgProxySecret\s*\)",
            stripped);
    }

    [Fact]
    public void Bootstrap_DefersToService_WhenServiceIsRunning()
    {
        var src = LoadSource(
            "VPNRouter.App", "ViewModels", "MainWindowViewModel.AutostartBootstrap.cs");
        if (src == null) return;

        var stripped = StripLineComments(src);

        Assert.Contains("ServiceVm.IsRunning", stripped);

        Assert.Matches(
            @"if\s*\(\s*ServiceVm\.IsRunning\s*\)[\s\S]{0,300}?return\s*;",
            stripped);
    }

    [Fact]
    public void Bootstrap_IsInvokedFromConstructor()
    {
        var src = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.cs");
        if (src == null) return;

        var ctorRegion = ExtractCtorRegion(src);

        Assert.Contains("BootstrapAutostartAsync", ctorRegion);

        Assert.Matches(
            @"_\s*=\s*BootstrapAutostartAsync\s*\(\s*\)",
            ctorRegion);
    }

    [Fact]
    public void Bootstrap_GeneratesSecret_WhenEmpty_MirroringManualPath()
    {
        var src = LoadSource(
            "VPNRouter.App", "ViewModels", "MainWindowViewModel.AutostartBootstrap.cs");
        if (src == null) return;

        var stripped = StripLineComments(src);

        Assert.Contains("IsNullOrWhiteSpace(TgProxySecret)", stripped);
        Assert.Contains("RandomNumberGenerator.GetBytes(16)", stripped);
        Assert.Contains("Convert.ToHexStringLower", stripped);
    }

    [Fact]
    public void Bootstrap_IsIdempotent_SkipsSpawnWhenAlreadyRunning()
    {
        var src = LoadSource(
            "VPNRouter.App", "ViewModels", "MainWindowViewModel.AutostartBootstrap.cs");
        if (src == null) return;

        var stripped = StripLineComments(src);

        Assert.Contains("TgProxyManager.IsAnyRunning(TgProxyPort)", stripped);
        Assert.Contains("ZapretManager.IsWinwsRunning()", stripped);
    }

    private static string? LoadSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }

    private static string StripLineComments(string src)
    {
        return string.Join('\n',
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));
    }

    private static string ExtractCtorRegion(string src)
    {
        var idx = src.IndexOf(
            "public MainWindowViewModel()",
            System.StringComparison.Ordinal);
        if (idx < 0) return src;
        var start = System.Math.Max(0, idx);
        var end = System.Math.Min(src.Length, idx + 9000);
        return src.Substring(start, end - start);
    }
}
