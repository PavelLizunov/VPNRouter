using VPNRouter.Core.Platform.macOS;
using Xunit;

namespace VPNRouter.Tests;

public class MacPrivilegedSetupContractTests
{
    [Fact]
    public void Installer_command_is_managed_and_has_no_temp_script_path()
    {
        var command = MacPrivilegedSetup.BuildInstallerCommand(
            "/Applications/VPNRouter.app/Contents/MacOS/install-privileged.sh",
            "/Applications/VPNRouter.app/Contents/MacOS/sudoers.template",
            "/Applications/VPNRouter.app/Contents/MacOS/vpnrouter-privileged",
            "/Applications/VPNRouter.app/Contents/MacOS/sing-box",
            new string('a', 64),
            new string('b', 64),
            "2.50.6-r4",
            "alice");

        Assert.StartsWith("/bin/bash ", command);
        Assert.Contains("--policy-template ", command);
        Assert.Contains("--helper ", command);
        Assert.Contains("--singbox-sha256 ", command);
        Assert.Contains("--app-version '2.50.6-r4'", command);
        Assert.Contains("--user 'alice'", command);
        Assert.DoesNotContain("/tmp/", command);
        Assert.DoesNotContain("/etc/sudoers", command);
        Assert.DoesNotContain("NOPASSWD", command);
    }

    [Fact]
    public void Installer_command_rejects_unsafe_user_name()
    {
        Assert.Throws<ArgumentException>(() => MacPrivilegedSetup.BuildInstallerCommand(
            "/a/install-privileged.sh", "/a/sudoers.template", "/a/helper", "/a/sing-box",
            new string('a', 64), new string('b', 64), "2.50.6", "alice bob"));
    }

    [Fact]
    public void AppleScript_escapes_backslashes_and_quotes()
    {
        var script = MacPrivilegedSetup.BuildAppleScript("echo \"hello\" \\ world");

        Assert.Equal("do shell script \"echo \\\"hello\\\" \\\\ world\" with administrator privileges",
            script);
    }

    [Fact]
    public void Shell_quote_handles_single_quotes()
    {
        Assert.Equal("'plain'", MacPrivilegedSetup.Quote("plain"));
        Assert.Equal("'a'\\''b'", MacPrivilegedSetup.Quote("a'b"));
    }

    [Fact]
    public void Unavailable_probe_is_not_current()
    {
        Assert.False(MacPrivilegedSetup.IsCurrent(
            MacPrivilegeState.Unavailable("legacy_policy"), out var helper, out var singbox));
        Assert.Empty(helper);
        Assert.Empty(singbox);
    }
}
