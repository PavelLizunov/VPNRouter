using VPNRouter.App.ViewModels;
using Xunit;

namespace VPNRouter.Tests;

public sealed class TryOpenUrlSecurityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("cmd://c+calc.exe")]
    [InlineData("powershell://script")]
    [InlineData("ftp://example.com/file.txt")]
    [InlineData("relative/path/to/file")]
    [InlineData("javascript:alert(1)")]
    public void TryOpenUrl_InvalidAndNonHttpSchemes_AreBlocked(string? url)
    {
        var result = MainWindowViewModel.TryOpenUrl(url);
        Assert.False(result);
    }
}
