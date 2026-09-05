using VPNRouter.App.Services;

namespace VPNRouter.Tests;

public sealed class UrlLauncherTests
{
    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com")]
    [InlineData("https://github.com/PavelLizunov/VPNRouter")]
    [InlineData("https://ipleak.net/")]
    [InlineData("http://localhost:8080/path?query=1")]
    public void IsValidWebUrl_ValidHttpAndHttps_ReturnsTrue(string url)
    {
        Assert.True(UrlLauncher.IsValidWebUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://example.com")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("cmd.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("relative/path")]
    [InlineData("://invalid-uri")]
    public void IsValidWebUrl_NonHttpOrInvalid_ReturnsFalse(string? url)
    {
        Assert.False(UrlLauncher.IsValidWebUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ms-settings:privacy")]
    public void TryOpenUrl_InvalidOrDisallowedScheme_ReturnsFalse(string? url)
    {
        Assert.False(UrlLauncher.TryOpenUrl(url));
    }
}
