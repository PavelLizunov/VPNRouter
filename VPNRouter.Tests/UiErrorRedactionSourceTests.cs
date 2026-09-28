namespace VPNRouter.Tests;

public sealed class UiErrorRedactionSourceTests
{
    [Theory]
    [InlineData("MainWindowViewModel.Subscriptions.cs",
        "StatusText = Strings.SyncFailed(CrashReporter.ScrubSecrets(ex.Message));",
        "StatusText = Strings.SyncFailed(ex.Message);")]
    [InlineData("MainWindowViewModel.SimpleMode.cs",
        "$\"Не удалось получить подписку: {CrashReporter.ScrubSecrets(ex.Message)}\"",
        "$\"Не удалось получить подписку: {ex.Message}\"")]
    [InlineData("MainWindowViewModel.SimpleMode.cs",
        "$\"Couldn't fetch the subscription: {CrashReporter.ScrubSecrets(ex.Message)}\"",
        "$\"Couldn't fetch the subscription: {ex.Message}\"")]
    public void UiErrorSink_ScrubsExceptionMessage(string file, string wrapped, string raw)
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
