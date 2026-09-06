using Xunit;
using VPNRouter.App.ViewModels;

namespace VPNRouter.Tests;

public sealed class AppUrlLauncherSecurityTests
{
    [Theory]
    [InlineData("file://C:/Windows/System32/cmd.exe")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("cmd.exe")]
    [InlineData("calc.exe")]
    [InlineData("ftp://example.com/file")]
    [InlineData("ssh://root@example.com")]
    [InlineData("/relative/path")]
    [InlineData("relative/path")]
    public void TryOpenUrl_RejectsNonHttpOrHttpsSchemes(string unsafeUrl)
    {
        var result = MainWindowViewModel.TryOpenUrl(unsafeUrl);
        Assert.False(result, $"Expected TryOpenUrl to reject non-HTTP/HTTPS URL: {unsafeUrl}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryOpenUrl_RejectsNullOrWhitespace(string? emptyUrl)
    {
        var result = MainWindowViewModel.TryOpenUrl(emptyUrl!);
        Assert.False(result);
    }
}
