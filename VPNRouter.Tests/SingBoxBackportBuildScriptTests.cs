#nullable enable
using System.Text.RegularExpressions;

namespace VPNRouter.Tests;

/// <summary>
/// Source-invariant guard: both sing-box-lx build scripts (.ps1/.sh) must apply the two
/// targeted upstream backports (TUN TCP NAT collision + DNS single-flight deadlock) from
/// the exact immutable SagerNet SHAs and fail closed on source assertions, without
/// rotating the pinned fork. Assertions target executable fragments, never comments.
/// </summary>
public sealed class SingBoxBackportBuildScriptTests
{
    private const string UpstreamRepo = "https://github.com/SagerNet/sing-box.git";
    private const string TunBackport = "0b7ffbaafa5f060dd8c762dfbc751d592cba1fea";
    private const string DnsBackport = "72a8723e13b9574664f4c78e588069fa4aca6fc9";

    [Fact]
    public void BothBuildScripts_ApplyBothBackportsFromImmutableUpstream()
    {
        // Test output is <repo>/VPNRouter.Tests/bin/<Config>/net10.0/ — four parents up.
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var ps1 = Normalize(File.ReadAllText(Path.Combine(repoRoot, "tools", "build-singbox-lx.ps1")));
        var sh = Normalize(File.ReadAllText(Path.Combine(repoRoot, "tools", "build-singbox-lx.sh")));

        // PowerShell: assignments, fetch, cherry-picks and fail-closed gates.
        Assert.Contains($"$UPSTREAM_REPO = '{UpstreamRepo}'", ps1);
        Assert.Contains($"$TUN_BACKPORT = '{TunBackport}'", ps1);
        Assert.Contains($"$DNS_BACKPORT = '{DnsBackport}'", ps1);
        Assert.Contains("Invoke-Git @('-C', $src, 'fetch', '--quiet', $UPSTREAM_REPO, $TUN_BACKPORT, $DNS_BACKPORT)", ps1);
        Assert.Contains("Invoke-Git @('-C', $src, 'cherry-pick', '--no-commit', $TUN_BACKPORT)", ps1);
        Assert.Contains("Invoke-Git @('-C', $src, 'cherry-pick', '--no-commit', $DNS_BACKPORT)", ps1);
        Assert.Contains("-not $goMod.Contains('github.com/sagernet/sing-tun v0.8.11')", ps1);
        Assert.Contains("if ($goMod.Contains('github.com/sagernet/sing-tun v0.8.10'))", ps1);
        Assert.Contains("-not $dnsClient.Contains('compatible.Map[transportCacheKey, chan struct{}]')", ps1);
        Assert.Contains("-not $dnsClient.Contains('cacheKey := transportCacheKey{Question: question, transportTag: transport.Tag()}')", ps1);
        Assert.Contains("if ($dnsClient.Contains('compatible.Map[dns.Question, chan struct{}]'))", ps1);

        // Bash: same invariants in shell syntax (grep -Fq gates fail closed via set -e).
        Assert.Contains($"UPSTREAM_REPO=\"{UpstreamRepo}\"", sh);
        Assert.Contains($"TUN_BACKPORT=\"{TunBackport}\"", sh);
        Assert.Contains($"DNS_BACKPORT=\"{DnsBackport}\"", sh);
        Assert.Contains("git -C \"$SRC\" fetch --quiet \"$UPSTREAM_REPO\" \"$TUN_BACKPORT\" \"$DNS_BACKPORT\"", sh);
        Assert.Contains("git -C \"$SRC\" cherry-pick --no-commit \"$TUN_BACKPORT\"", sh);
        Assert.Contains("git -C \"$SRC\" cherry-pick --no-commit \"$DNS_BACKPORT\"", sh);
        Assert.Contains("grep -Fq \"github.com/sagernet/sing-tun v0.8.11\" \"$SRC/go.mod\"", sh);
        Assert.Contains("if grep -Fq \"github.com/sagernet/sing-tun v0.8.10\" \"$SRC/go.mod\"; then", sh);
        Assert.Contains("grep -Fq \"compatible.Map[transportCacheKey, chan struct{}]\" \"$SRC/dns/client.go\"", sh);
        Assert.Contains("grep -Fq \"cacheKey := transportCacheKey{Question: question, transportTag: transport.Tag()}\" \"$SRC/dns/client.go\"", sh);
        Assert.Contains("if grep -Fq \"compatible.Map[dns.Question, chan struct{}]\" \"$SRC/dns/client.go\"; then", sh);
    }

    /// <summary>
    /// Strips PowerShell block comments and full-line # comments before collapsing
    /// whitespace, so commented-out commands cannot satisfy assertions.
    /// </summary>
    private static string Normalize(string script)
    {
        script = Regex.Replace(script, @"(?s)<#.*?#>", "");
        script = Regex.Replace(script, @"(?m)^\s*#.*$", "");
        return Regex.Replace(script, @"\s+", " ");
    }
}
