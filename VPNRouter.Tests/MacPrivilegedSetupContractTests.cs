using VPNRouter.Core.Platform.macOS;
using Xunit;

namespace VPNRouter.Tests;

public class MacPrivilegedSetupContractTests
{
    [Fact]
    public void Installer_command_captures_the_script_before_elevation()
    {
        var command = MacPrivilegedSetup.BuildInstallerCommand(
            "#!/bin/bash\nprintf 'fixture'\n",
            MacPrivilegePolicy.Render("alice"),
            "/Applications/VPNRouter.app/Contents/MacOS/vpnrouter-privileged",
            "/Applications/VPNRouter.app/Contents/MacOS/sing-box",
            new string('a', 64),
            new string('b', 64),
            "2.50.6-r4",
            "alice");

        Assert.StartsWith("/usr/bin/printf %s ", command);
        Assert.Contains("| /usr/bin/base64 -D | /usr/bin/env -i ", command);
        Assert.Contains("/bin/bash -s -- ", command);
        Assert.Contains(Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("#!/bin/bash\nprintf 'fixture'\n")), command);
        Assert.DoesNotContain("install-privileged.sh", command);
        Assert.Contains("--policy-base64 '" + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(MacPrivilegePolicy.Render("alice"))) + "'", command);
        Assert.DoesNotContain("sudoers.template", command);
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
            "#!/bin/bash\n", MacPrivilegePolicy.Render("alice"), "/a/helper", "/a/sing-box",
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

    [Theory]
    [InlineData(0, "install-privileged: managed policy=2 uid=501", "", MacPrivilegeSetupResult.Ready)]
    [InlineData(0, "", "", MacPrivilegeSetupResult.NotReady)]
    [InlineData(1, "", "user cancelled", MacPrivilegeSetupResult.NotReady)]
    [InlineData(1, "install-privileged: managed policy=2 uid=501", "", MacPrivilegeSetupResult.NotReady)]
    [InlineData(1, "", "execution error: vpnrouter-legacy-runtime-active (66)", MacPrivilegeSetupResult.LegacySessionActive)]
    [InlineData(1, "", "execution error: vpnrouter-legacy-runtime-active (78)", MacPrivilegeSetupResult.LegacySessionActive)]
    public void Installer_result_reports_a_legacy_session_without_claiming_readiness(
        int exitCode, string stdout, string stderr, MacPrivilegeSetupResult expected)
    {
        Assert.Equal(expected, MacPrivilegedSetup.ReadInstallerResult(exitCode, stdout, stderr));
    }
}
