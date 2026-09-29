using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public class FirewallManagerResolveProcessPathTests
{
    [Fact]
    public void CreateBlockRules_ForRunningProcess_ResolvesPathAndEmitsAddRule()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FirewallManager/netsh is Windows-only");

        using var self = Process.GetCurrentProcess();
        var procName = self.ProcessName + ".exe";

        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true, new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(2), false));

        using var fw = new FirewallManager(logger: null, runner: fake);
        fw.CreateBlockRules(new[] { procName });

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
        fake.OnRun(_ => true, new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(2), false));

        using var fw = new FirewallManager(logger: null, runner: fake);
        fw.CreateBlockRules(new[] { "VPNRouter_no_such_proc_zzq.exe" });

        Assert.DoesNotContain(fake.RunCalls, c =>
            c.Arguments.Contains("add") &&
            c.Arguments.Any(a => a.StartsWith("program=", StringComparison.OrdinalIgnoreCase)));
    }
}
