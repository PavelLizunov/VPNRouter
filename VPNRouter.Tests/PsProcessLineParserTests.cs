using VPNRouter.Core.Platform.Unix;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Headless regression coverage for the macOS/Linux `ps -eo pid,ppid,comm` line
/// parser (Fix #3, macOS deep-audit 2026-06-04). The bug: the comm column is a full
/// executable path on macOS that routinely contains spaces, and the old naive
/// whitespace-split truncated "/Applications/Google Chrome.app/.../Google Chrome"
/// to "Google" — which never matched sing-box's exact process_name "Google Chrome",
/// silently breaking split-tunnel routing for every space-named app.
///
/// These run on the Windows test build precisely because the parser was lifted out
/// of the #if !PLATFORM_WINDOWS guard into VPNRouter.Core.Platform.Unix.
/// </summary>
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
        // The headline bug: this used to yield "Google", which broke routing.
        var ok = PsProcessLineParser.TryParseLine(
            "1234 5678 /Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            out var pid, out var ppid, out var comm);

        Assert.True(ok);
        Assert.Equal(1234, pid);
        Assert.Equal(5678, ppid);
        Assert.Equal("Google Chrome", comm);
    }

    [Fact]
    public void Preserves_spaces_in_helper_process_basename()
    {
        // Electron/Chromium do their network I/O under the child "Helper" names,
        // not the parent — these are the names that must match process_name.
        const string line =
            "4321 1234 /Applications/Google Chrome.app/Contents/Frameworks/" +
            "Google Chrome Framework.framework/Versions/120.0/Helpers/" +
            "Google Chrome Helper.app/Contents/MacOS/Google Chrome Helper";

        var ok = PsProcessLineParser.TryParseLine(line, out _, out _, out var comm);

        Assert.True(ok);
        Assert.Equal("Google Chrome Helper", comm);
    }

    [Theory]
    // Right-aligned multi-space column padding (real ps output shape).
    [InlineData("   42    1 /sbin/launchd", 42, 1, "launchd")]
    // Tab-delimited columns.
    [InlineData("100\t1\t/usr/sbin/cfprefsd", 100, 1, "cfprefsd")]
    // Bare command with no path separator (kernel thread style).
    [InlineData("0 0 kernel_task", 0, 0, "kernel_task")]
    // Trailing whitespace is tolerated.
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
    // Header row — non-numeric, must be rejected (defensive; caller also Skip(1)s).
    [InlineData("  PID  PPID COMM")]
    // Only two columns, no comm.
    [InlineData("123 456")]
    // pid present but ppid missing.
    [InlineData("123")]
    // Non-integer pid.
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
