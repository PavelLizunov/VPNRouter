using System.Text;

namespace VPNRouter.Tests;

/// <summary>
/// The worker-local post-ship tooling (plans/phase-p1-post-ship-local-2026-10-01.md): the shim must shadow every remoting
/// command the verifier scripts use, the driver must run the real scripts through it, and the verifier script that holds
/// Cyrillic literals must carry a BOM (Windows PowerShell 5.1 reads a file without one as ANSI).
/// </summary>
[Trait("Category", "Unit")]
[Trait("Layer", "Core")]
public sealed class PostShipLocalContractTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VPNRouter.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Root }.Concat(parts).ToArray()));

    [Fact]
    public void Shim_ShadowsEveryRemotingCommandTheVerifierScriptsUse()
    {
        var shim = Read("tools", "brat-local-shim.ps1");
        foreach (var command in new[] { "New-PSSession", "Remove-PSSession", "Invoke-Command", "Copy-Item" })
            Assert.Contains($"function {command}", shim);

        var used = string.Join('\n',
            Read("tools", "brat-verify.ps1"), Read("tools", "brat-stability.ps1"), Read("deploy-to-testpc.ps1"));
        Assert.DoesNotContain("Enter-PSSession", used);
        Assert.DoesNotContain("-FromSession", used);
        Assert.DoesNotContain("$using:", used);
    }

    [Fact]
    public void Driver_RunsTheRealVerifierScriptsAndSaysWhatItDoesNotCover()
    {
        var driver = Read("tools", "post-ship-local.ps1");
        Assert.Contains(". $Shim", driver);
        foreach (var step in new[] { "-Action identity", "-Action deploy", "-Mode ColdCycles", "-Action lifecycle" })
            Assert.Contains(step, driver);
        Assert.Contains("git ls-remote", driver);
        Assert.Contains("does not match its sidecar", driver);
        Assert.Contains("POSTSHIP-LOCAL: PASS", driver);
        Assert.Contains("does NOT cover", driver);
    }

    [Fact]
    public void BratVerify_HasAByteOrderMarkBecauseItHoldsNonAsciiLiterals()
    {
        var path = Path.Combine(Root, "tools", "brat-verify.ps1");
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "tools/brat-verify.ps1 holds Cyrillic literals and needs a UTF-8 BOM for Windows PowerShell 5.1.");
        Assert.True(Encoding.UTF8.GetString(bytes).Any(c => c > 0x7F));
    }
}
