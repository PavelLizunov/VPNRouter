namespace VPNRouter.Tests;

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
    [InlineData("MainWindowViewModel.cs",
        "_logger.Warning(ex, \"Failed to parse server URI: {Line}\", CrashReporter.ScrubSecrets(line));",
        "\"Failed to parse server URI: {Line}\", line);")]
    [InlineData("MainWindowViewModel.cs",
        "Serilog.Log.Logger.Debug(ex, \"[VM] OpenUrl failed: {Url}\", CanaryPolicy.RedactUrl(url));",
        "\"[VM] OpenUrl failed: {Url}\", url);")]
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
