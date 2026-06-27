using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Integration pin for the kill-switch path-resolution fix. With a REAL running
/// process and netsh faked, <see cref="FirewallManager.CreateBlockRules"/> must
/// RESOLVE the process's exe path (now via
/// <see cref="ProcessImagePath"/> / QueryFullProcessImageName) and emit a netsh
/// <c>add rule program=&lt;path&gt;</c> — i.e. it actually arms the block rule
/// instead of skipping it.
///
/// <para>Before the fix, a session-0 / Windows Service reader resolved the path
/// to null via <c>Process.MainModule.FileName</c> and created ZERO rules, so the
/// <c>block_on_vpn_fail</c> kill-switch failed OPEN. This test exercises the
/// resolver against the live test-host process (always in-process-resolvable) so
/// it runs on the Windows dev box; the actual cross-session (session-0 → user)
/// property is proven by the windows-brat live gate, which a unit test can't
/// stand up.</para>
/// </summary>
public class FirewallManagerResolveProcessPathTests
{
    [Fact]
    public void CreateBlockRules_ForRunningProcess_ResolvesPathAndEmitsAddRule()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FirewallManager/netsh is Windows-only");

        using var self = Process.GetCurrentProcess();
        var procName = self.ProcessName + ".exe"; // a process that IS running

        var fake = new FakeProcessRunner();
        // netsh cleanup "show rule" → empty (no orphans); every add → ok.
        fake.OnRun(_ => true, new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(2), false));

        using var fw = new FirewallManager(logger: null, runner: fake);
        fw.CreateBlockRules(new[] { procName });

        // A block rule for the running process must have been created: an
        // `add rule` netsh call whose program= token points at a real file.
        var addCall = fake.RunCalls.FirstOrDefault(c =>
            c.ExecutablePath.Equals("netsh.exe", StringComparison.OrdinalIgnoreCase) &&
            c.Arguments.Contains("add") &&
            c.Arguments.Any(a => a.StartsWith("program=", StringComparison.OrdinalIgnoreCase)));

        Assert.NotNull(addCall);
        var programArg = addCall!.Arguments.First(a =>
            a.StartsWith("program=", StringComparison.OrdinalIgnoreCase));
        var resolvedPath = programArg.Substring("program=".Length);
        Assert.True(File.Exists(resolvedPath),
            $"resolved program path should exist on disk, got '{resolvedPath}'");
        Assert.Equal(self.ProcessName,
            Path.GetFileNameWithoutExtension(resolvedPath), ignoreCase: true);
    }

    [Fact]
    public void CreateBlockRules_ForNonRunningProcess_SkipsRuleWithoutCrash()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FirewallManager/netsh is Windows-only");

        var fake = new FakeProcessRunner();
        // where.exe fallback also routes through the runner → empty stdout =
        // "not found on PATH", so the name stays unresolved.
        fake.OnRun(_ => true, new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(2), false));

        using var fw = new FirewallManager(logger: null, runner: fake);
        fw.CreateBlockRules(new[] { "VPNRouter_no_such_proc_zzq.exe" });

        // Unresolved → rule skipped → no `add rule program=` emitted, no throw.
        Assert.DoesNotContain(fake.RunCalls, c =>
            c.Arguments.Contains("add") &&
            c.Arguments.Any(a => a.StartsWith("program=", StringComparison.OrdinalIgnoreCase)));
    }
}
