using VPNRouter.Core.Platform.macOS;
using Xunit;

namespace VPNRouter.Tests;

public class MacPrivilegePolicyTests
{
    [Fact]
    public void Render_has_one_exact_helper_rule_and_no_wildcards()
    {
        var policy = MacPrivilegePolicy.Render("alice");

        Assert.Contains(MacPrivilegePaths.PolicyHeader, policy);
        Assert.Contains(
            "alice ALL=(root) NOPASSWD: /Library/PrivilegedHelperTools/com.vpnrouter.privileged",
            policy);
        Assert.DoesNotContain("*", policy);
        Assert.DoesNotContain("/Users/", policy);
        Assert.DoesNotContain("sing-box", policy);
        Assert.DoesNotContain("pfctl", policy);
        Assert.DoesNotContain("/bin/kill", policy);
        Assert.Single(policy.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#')));
        Assert.True(MacPrivilegePolicy.IsManagedPolicy(policy));
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("a.b-c_d")]
    [InlineData("user123")]
    public void Render_accepts_macos_short_names(string user)
    {
        Assert.True(MacPrivilegePolicy.IsManagedPolicy(MacPrivilegePolicy.Render(user)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("alice bob")]
    [InlineData("alice\nroot ALL=(root) NOPASSWD: ALL")]
    [InlineData("alice:root")]
    [InlineData("alice,root")]
    [InlineData("alice*")]
    public void Render_rejects_unsafe_principals(string user)
    {
        Assert.Throws<ArgumentException>(() => MacPrivilegePolicy.Render(user));
    }

    [Theory]
    [InlineData("# vpnrouter 2026-09-02 sudoers\nbob ALL=(root) NOPASSWD: /Users/bob/Library/Application Support/VPNRouter/bin/sing-box *")]
    [InlineData("bob ALL=(root) NOPASSWD: /usr/bin/pkill *")]
    [InlineData("bob ALL=(root) NOPASSWD: /sbin/pfctl *")]
    public void LooksLikeLegacyPolicy_detects_old_app_and_guide_shapes(string policy)
    {
        Assert.True(MacPrivilegePolicy.LooksLikeLegacyPolicy(policy));
        Assert.False(MacPrivilegePolicy.IsManagedPolicy(policy));
    }

    [Fact]
    public void PolicyPath_is_per_uid()
    {
        Assert.Equal("/etc/sudoers.d/vpnrouter-501", MacPrivilegePaths.PolicyPath(501));
        Assert.Equal("/Library/Application Support/VPNRouter/assets/501/geo.srs",
            MacPrivilegePaths.UserAssetPath(501, "geo.srs"));
        Assert.True(MacPrivilegePaths.IsRootAssetPath(
            "/Library/Application Support/VPNRouter/assets/501/geo.srs", 501));
        Assert.False(MacPrivilegePaths.IsRootAssetPath(
            "/Library/Application Support/VPNRouter/assets/501/../current.json", 501));
    }

    [Fact]
    public void ManagedPolicy_rejects_a_different_principal_or_an_extra_rule()
    {
        var alice = MacPrivilegePolicy.Render("alice");

        Assert.False(MacPrivilegePolicy.IsManagedPolicy(alice, "bob"));
        Assert.False(MacPrivilegePolicy.IsManagedPolicy(
            alice + "%admin ALL=(ALL) NOPASSWD: ALL\n", "alice"));
        Assert.True(MacPrivilegePolicy.IsManagedPolicy(alice, "alice"));
    }
}
