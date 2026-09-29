using VPNRouter.Core.Platform.Unix;
using Xunit;

namespace VPNRouter.Tests;

public class PsProcessLineParserTests
{
    [Fact]
    public void Parses_simple_path_to_basename()
    {
        var ok = PsProcessLineParser.TryParseLine("  501     1 /usr/libexec/timed",
            out var pid, out var ppid, out var comm);

        Assert.True(ok);
        Assert.Equal(501, pid);
        Assert.Equal(1, ppid);
        Assert.Equal("timed", comm);
    }

    [Fact]
    public void Preserves_spaces_in_app_path_basename()
    {
        var ok = PsProcessLineParser.TryParseLine(
            "1234 5678 /Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            out var pid, out var ppid, out var comm);

        Assert.True(ok);
        Assert.Equal(1234, pid);
        Assert.Equal(5678, ppid);
        Assert.Equal("Google Chrome", comm);
    }

    [Theory]
    [InlineData("   42    1 /sbin/launchd", 42, 1, "launchd")]
    [InlineData("100\t1\t/usr/sbin/cfprefsd", 100, 1, "cfprefsd")]
    [InlineData("0 0 kernel_task", 0, 0, "kernel_task")]
    [InlineData("7 1 /usr/libexec/UserEventAgent   ", 7, 1, "UserEventAgent")]
    public void Parses_well_formed_rows(string line, int expectedPid, int expectedPpid, string expectedComm)
    {
        var ok = PsProcessLineParser.TryParseLine(line, out var pid, out var ppid, out var comm);

        Assert.True(ok);
        Assert.Equal(expectedPid, pid);
        Assert.Equal(expectedPpid, ppid);
        Assert.Equal(expectedComm, comm);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("  PID  PPID COMM")]
    [InlineData("123 456")]
    [InlineData("123")]
    [InlineData("abc 1 /sbin/launchd")]
    public void Rejects_malformed_rows(string? line)
    {
        var ok = PsProcessLineParser.TryParseLine(line, out var pid, out var ppid, out var comm);

        Assert.False(ok);
        Assert.Equal(0, pid);
        Assert.Equal(0, ppid);
        Assert.Equal(string.Empty, comm);
    }
}
