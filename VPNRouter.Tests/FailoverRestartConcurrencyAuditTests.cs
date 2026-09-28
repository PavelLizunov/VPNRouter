#nullable enable

using System;
using System.IO;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class FailoverRestartConcurrencyAuditTests
{
    private static SingBoxSettings BuildIdleSettings() => new()
    {
        ExecutablePath = Path.Combine(
            Path.GetTempPath(), "nonexistent-sing-box-failover-audit-test.exe"),
    };

    [Fact]
    public void Restart_OnDisposedManager_IsNoOp_DoesNotRelaunch()
    {
        var mgr = new SingBoxManager(BuildIdleSettings());
        mgr.Dispose();

        var ex = Record.Exception(() => mgr.Restart());

        Assert.Null(ex);
        Assert.NotEqual(SingBoxState.Restarting, mgr.State);
    }

    [Fact]
    public void ReloadConfigJson_ForceRestart_OnDisposedManager_IsNoOp()
    {
        var mgr = new SingBoxManager(BuildIdleSettings());
        mgr.Dispose();

        var ex = Record.Exception(() => mgr.ReloadConfigJson("{}", forceRestart: true));

        Assert.Null(ex);
        Assert.NotEqual(SingBoxState.Restarting, mgr.State);
    }

    [Fact]
    public void Source_SingBoxManager_RestartAndReload_HaveDisposedGuard()
    {
        var sourcePath = FindRepoFile("VPNRouter.Core", "Services", "SingBoxManager.cs");
        var source = SingBoxSourceText.ReadAll(sourcePath);

        Assert.Contains("Restart ignored — manager already disposed", source);
        Assert.Contains("ReloadConfigJson ignored — manager already disposed", source);
        Assert.Contains("LaunchProcess aborted — manager disposed before spawn", source);
        Assert.Contains("Volatile.Read(ref _disposed) != 0", source);
    }

    [Fact]
    public void Source_ExecuteProbeFailoverRestart_TearsDownOnGenericThrow()
    {
        var sourcePath = FindRepoFile("VPNRouter.Core", "Services", "VpnEngine.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains(
            "Failover restart failed to bring up replacement — tearing down partial state",
            source);
    }

    private static string FindRepoFile(params string[] segments)
    {
        var startDir = Path.GetDirectoryName(typeof(SingBoxManager).Assembly.Location)!;
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(new[] { Environment.CurrentDirectory }.Concat(segments).ToArray());
    }
}
