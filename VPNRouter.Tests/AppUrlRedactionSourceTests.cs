namespace VPNRouter.Tests;

/// <summary>
/// R13-A: each of the four App ViewModel <c>{Url}</c> log sinks must wrap its
/// argument in the pinned <see cref="VPNRouter.Core.Services.CanaryPolicy.RedactUrl"/>.
/// Source-shape contract, not runtime: MainWindowViewModel builds its own
/// non-injectable Serilog logger and three sinks fire only on a network failure,
/// so a capturing-logger test would need a production seam + broad VM fixture.
/// Redaction output shape is pinned separately by CanaryPolicyTests.
/// </summary>
public sealed class AppUrlRedactionSourceTests
{
    [Theory]
    [InlineData("MainWindowViewModel.Subscriptions.cs",
        "_logger.Error(ex, \"[VM] RefreshSubscription failed for {Url}\", CanaryPolicy.RedactUrl(sub.Url));",
        "\"[VM] RefreshSubscription failed for {Url}\", sub.Url);")]
    [InlineData("MainWindowViewModel.Subscriptions.cs",
        "_logger.Warning(ex, \"[VM] Refresh of {Url} failed\", CanaryPolicy.RedactUrl(s.Url));",
        "\"[VM] Refresh of {Url} failed\", s.Url);")]
    [InlineData("MainWindowViewModel.Subscriptions.cs",
        "_logger.Warning(ex, \"[SubRefresh] Failed for {Url}\", CanaryPolicy.RedactUrl(s.Url));",
        "\"[SubRefresh] Failed for {Url}\", s.Url);")]
    [InlineData("MainWindowViewModel.Wgturn.cs",
        "_logger.Warning(\"[Wgturn] AddWgturnConfig: URL failed structural parse: {Url}\", CanaryPolicy.RedactUrl(rawUrl));",
        "\"[Wgturn] AddWgturnConfig: URL failed structural parse: {Url}\", rawUrl);")]
    public void LogSink_RedactsUrlArgument(string file, string wrapped, string raw)
    {
        var src = File.ReadAllText(FindRepoFile("VPNRouter.App", "ViewModels", file));

        Assert.Contains(wrapped, src);
        Assert.DoesNotContain(raw, src);
    }

    private static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
