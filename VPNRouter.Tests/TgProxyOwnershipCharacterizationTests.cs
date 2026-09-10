#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Characterization regression suite for TgProxy process ownership and lifecycle safety.
/// <para>
/// Invariants verified:
/// 1. Port occupancy is never identity: foreign listeners are unknown, not owned.
/// 2. Zero process kills on foreign listeners: Quit, toggle, and update never terminate
///    processes by port or unverified process name sweeps.
/// 3. Positive owned stop: an active owned manager cleanly stops and suppresses events
///    on its own process handle.
/// 4. Exited handle safety: an already-exited process handle is never killed or re-opened by PID.
/// 5. Non-blocking UI: runtime status polling and manager properties do not block UI execution.
/// 6. Safe compatibility: legacy static entry points are non-destructive no-ops.
/// </para>
/// </summary>
public sealed class TgProxyOwnershipCharacterizationTests
{
    // ─── 1. Static entry points are safe no-ops (compatibility) ───────────

    [Fact]
    public void TgProxyManager_KillAll_Structural_NoDestructiveCalls()
    {
        // Structural: KillAll method must be a safe no-op with zero process-kill operations.
        var src = LoadSource("VPNRouter.Core", "Services", "TgProxyManager.cs");
        var killAllBody = ExtractMethodBody(src, "KillAll");

        Assert.DoesNotContain("KillByPort", killAllBody);
        Assert.DoesNotContain("proc.Kill", killAllBody);
        Assert.DoesNotContain("Process.GetProcessesByName", killAllBody);
        Assert.DoesNotContain("Process.Start", killAllBody);
        Assert.DoesNotContain("Process.GetProcessById", killAllBody);
        Assert.DoesNotContain("taskkill", killAllBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("netstat", killAllBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lsof", killAllBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TgProxyManager_KillByPort_Structural_NoDestructiveCalls()
    {
        // Structural: KillByPort method must be a safe no-op with zero process-kill operations.
        var src = LoadSource("VPNRouter.Core", "Services", "TgProxyManager.cs");
        var killByPortBody = ExtractMethodBody(src, "KillByPort");

        Assert.DoesNotContain("KillByPortWindows", killByPortBody);
        Assert.DoesNotContain("KillByPortUnix", killByPortBody);
        Assert.DoesNotContain("proc.Kill", killByPortBody);
        Assert.DoesNotContain("Process.GetProcessesByName", killByPortBody);
        Assert.DoesNotContain("Process.Start", killByPortBody);
        Assert.DoesNotContain("Process.GetProcessById", killByPortBody);
        Assert.DoesNotContain("taskkill", killByPortBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("netstat", killByPortBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lsof", killByPortBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeStatusDetector_IsTgProxyRunning_NeverClaimsPortOnlyTruth()
    {
        // Port occupancy is never identity/truth of an owned TgProxy.
        Assert.False(RuntimeStatusDetector.IsTgProxyRunning(1443));
        Assert.False(RuntimeStatusDetector.IsTgProxyRunning(0));
        Assert.False(RuntimeStatusDetector.IsTgProxyRunning(-1));

        var src = LoadSource("VPNRouter.Core", "Services", "RuntimeStatusDetector.cs");
        var detectorBody = ExtractMethodBody(src, "IsTgProxyRunning");

        Assert.DoesNotContain("GetActiveTcpListeners", detectorBody);
        Assert.Contains("return false;", detectorBody);
    }

    // ─── 2. Owned positive stop & exited handle safety ────────────────────

    [Fact]
    public void OwnedManager_PositiveStop_CallsKillAndSuppressOnOwnedHandle()
    {
        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 99101);

        var sut = new TgProxyManager(logger: null, runner: fake);
        SetProcessHandle(sut, handle);

        try
        {
            Assert.True(sut.IsRunning);
            Assert.Equal(99101, sut.Pid);

            sut.Stop();

            Assert.False(sut.IsRunning);
            Assert.Null(sut.Pid);
            Assert.Equal(1, handle.SuppressExitedEventCallCount);
            Assert.Equal(1, handle.KillCallCount);
        }
        finally
        {
            sut.Dispose();
        }
    }

    [Fact]
    public void ExitedHandle_StopDoesNotKillProcess()
    {
        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 99102);
        handle.SignalExit(0);
        Assert.True(handle.HasExited);

        var sut = new TgProxyManager(logger: null, runner: fake);
        SetProcessHandle(sut, handle);

        try
        {
            sut.Stop();

            Assert.False(sut.IsRunning);
            Assert.Equal(0, handle.KillCallCount);
        }
        finally
        {
            sut.Dispose();
        }
    }

    [Fact]
    public void TgProxyManager_Stop_NeverReopensProcessByPid()
    {
        var src = LoadSource("VPNRouter.Core", "Services", "TgProxyManager.cs");
        var stopBody = ExtractMethodBody(src, "Stop");

        Assert.DoesNotContain("Process.GetProcessById", stopBody);
        Assert.DoesNotContain("Process.GetProcessesByName", stopBody);
        Assert.DoesNotContain("KillByPort", stopBody);
    }

    [Fact]
    public void TgProxyManager_IsRunning_And_Pid_AreLockFree()
    {
        var src = LoadSource("VPNRouter.Core", "Services", "TgProxyManager.cs");
        var isRunningBody = ExtractPropertyBody(src, "IsRunning");
        var pidBody = ExtractPropertyBody(src, "Pid");

        Assert.DoesNotContain("lock (_lifecycleGate)", isRunningBody);
        Assert.DoesNotContain("lock (_lifecycleGate)", pidBody);
    }

    [Fact]
    public void TgProxyManager_SourceAudit_NoUnsafeProcessSweepsOrPortKillers()
    {
        var src = LoadSource("VPNRouter.Core", "Services", "TgProxyManager.cs");
        var stripped = StripLineComments(src);

        Assert.DoesNotContain("KillByPortWindows", stripped);
        Assert.DoesNotContain("KillByPortUnix", stripped);
        Assert.DoesNotContain("Process.GetProcessesByName(\"tg-ws-proxy\")", stripped);
        Assert.DoesNotContain("Process.GetProcessesByName(\"TgWsProxy_windows\")", stripped);
    }

    // ─── 3. Source call-graph audits (no destructive port/name kill paths) ─

    [Fact]
    public void MainWindowViewModel_Quit_ZeroKillAllOrPortKillCalls()
    {
        var src = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.cs");
        var quitBody = ExtractMethodBody(src, "Quit");

        Assert.Contains("_tgProxy?.Stop()", quitBody);
        Assert.Contains("KillAllZapret()", quitBody);

        Assert.DoesNotContain("TgProxyManager.KillAll", quitBody);
        Assert.DoesNotContain("TgProxyManager.KillByPort", quitBody);
        Assert.DoesNotContain("KillAll(", quitBody);
        Assert.DoesNotContain("KillByPort", quitBody);
    }

    [Fact]
    public void MainWindowViewModel_Toggle_ZeroKillAllOrPortKillCalls()
    {
        var src = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.cs");
        var toggleBody = ExtractMethodBody(src, "ToggleTgProxyCoreAsync");

        Assert.DoesNotContain("TgProxyManager.KillAll", toggleBody);
        Assert.DoesNotContain("TgProxyManager.KillByPort", toggleBody);
        Assert.DoesNotContain("KillAll", toggleBody);
        Assert.DoesNotContain("KillByPort", toggleBody);

        Assert.DoesNotContain("TgProxyManager.IsAnyRunning(TgProxyPort)", toggleBody);
        Assert.Contains("shouldStop = TgProxyEnabled || currentManager?.IsRunning == true;", toggleBody);
        Assert.Contains("if (manager.IsRunning)", toggleBody);
    }

    [Fact]
    public void MainWindowViewModel_Update_ZeroKillAllOrPortKillCalls()
    {
        var src = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.cs");
        var updateBody = ExtractMethodBody(src, "UpdateTgProxyCoreAsync");

        Assert.DoesNotContain("TgProxyManager.KillAll", updateBody);
        Assert.DoesNotContain("TgProxyManager.KillByPort", updateBody);
        Assert.DoesNotContain("KillAll", updateBody);
        Assert.DoesNotContain("KillByPort", updateBody);

        Assert.Contains("wasRunning = manager?.IsRunning == true;", updateBody);
        Assert.Contains("manager?.Stop();", updateBody);

        // Source guard: confirmed stop check before download
        var stopIdx = updateBody.IndexOf("manager?.Stop();", StringComparison.Ordinal);
        var checkIdx = updateBody.IndexOf("if (manager?.IsRunning == true)", StringComparison.Ordinal);
        var downloadIdx = updateBody.IndexOf("DownloadAsync", StringComparison.Ordinal);

        Assert.True(stopIdx >= 0, "Expected manager?.Stop() in UpdateTgProxyCoreAsync");
        Assert.True(checkIdx > stopIdx, "Expected if (manager?.IsRunning == true) after manager?.Stop()");
        Assert.True(downloadIdx > checkIdx, "Expected DownloadAsync after stop check");
    }

    [Fact]
    public void MainWindowViewModel_LoadSettingsIntoUI_DoesNotAdoptForeignPortListener()
    {
        var src = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.cs");
        var match = Regex.Match(src, @"\b(?:(?:public|private|protected|internal)\s+)?void\s+LoadSettingsIntoUI\s*\(");
        var loadSettingsStart = match.Success
            ? match.Index
            : src.IndexOf("private void LoadSettingsIntoUI()", StringComparison.Ordinal);
        Assert.True(loadSettingsStart >= 0, "Expected LoadSettingsIntoUI in MainWindowViewModel.cs");

        var start = src.IndexOf("// Telegram proxy", loadSettingsStart, StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected '// Telegram proxy' marker inside LoadSettingsIntoUI");
        var end = src.IndexOf("// Update channel", start, StringComparison.Ordinal);
        Assert.True(end > start, "Expected '// Update channel' marker after '// Telegram proxy'");

        var section = StripLineComments(src[start..end]);

        Assert.DoesNotContain("TgProxyManager.IsAnyRunning(TgProxyPort)", section);
        Assert.Contains("if (_tgProxy?.IsRunning == true)", section);
    }

    [Fact]
    public void MainWindowViewModel_AutostartBootstrap_ForeignListenerFailsClosedWithoutOwnershipClaim()
    {
        var src = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.AutostartBootstrap.cs");
        var body = ExtractMethodBody(src, "TryAutostartTgProxyAsync");

        var isAnyRunningIdx = body.IndexOf("if (TgProxyManager.IsAnyRunning(TgProxyPort))", StringComparison.Ordinal);
        Assert.True(isAnyRunningIdx >= 0, "Expected port check in TryAutostartTgProxyAsync");

        var returnIdx = body.IndexOf("return;", isAnyRunningIdx, StringComparison.Ordinal);
        Assert.True(returnIdx > isAnyRunningIdx);

        var failClosedBlock = body[isAnyRunningIdx..returnIdx];
        Assert.DoesNotContain("TgProxyEnabled = true", failClosedBlock);

        Assert.Contains("if (manager.IsRunning)", body);
        Assert.DoesNotContain("if (manager.IsRunning || TgProxyManager.IsAnyRunning", body);
    }

    [Fact]
    public void MainWindowViewModel_RuntimeStatus_NonBlockingAndNoPortOnlyTruth()
    {
        var src = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.RuntimeStatus.cs");
        var body = ExtractMethodBody(src, "UpdateRuntimeStatus");

        Assert.DoesNotContain("RuntimeStatusDetector.IsTgProxyRunning", body);
        Assert.DoesNotContain("Monitor.TryEnter(_tgProxyStateGate)", body);
        Assert.Contains("Volatile.Read(ref _tgProxy)", body);
    }

    // ─── 4. Stubborn process handle safety (failed kill retains handle/identity) ───

    private sealed class StubbornProcessHandle : IProcessHandle
    {
        public int Pid { get; }
        public bool AllowExit { get; set; }
        public bool KillThrows { get; set; }
        public int KillCallCount { get; private set; }
        public int DisposeCallCount { get; private set; }
        public int SuppressExitedEventCallCount { get; private set; }

        public StubbornProcessHandle(int pid = 99201)
        {
            Pid = pid;
        }

        public bool HasExited => AllowExit;

        public Task<int> WaitForExitAsync(CancellationToken ct)
        {
            if (!AllowExit)
            {
                // Immediately canceled/fails: no wall-clock 3s wait
                return Task.FromException<int>(new OperationCanceledException(ct));
            }
            return Task.FromResult(0);
        }

        public void Kill(bool entireProcessTree = true)
        {
            KillCallCount++;
            if (KillThrows)
            {
                throw new System.ComponentModel.Win32Exception(5, "Access is denied");
            }
        }

        public void SuppressExitedEvent()
        {
            SuppressExitedEventCallCount++;
        }

        public ProcessSnapshot? TryGetSnapshot() => null;

        public void Dispose()
        {
            DisposeCallCount++;
        }

        public event EventHandler<string>? OutputLine;
        public event EventHandler<string>? ErrorLine;
        public event EventHandler<int>? Exited;
    }

    [Fact]
    public void Stop_StubbornHandle_KillReturns_RetainsHandleAndIsRunning_StartRejectsReplacement_RetrySucceeds()
    {
        var fake = new FakeProcessRunner();
        var handle = new StubbornProcessHandle(pid: 99201) { KillThrows = false, AllowExit = false };

        var sut = new TgProxyManager(logger: null, runner: fake);
        SetProcessHandle(sut, handle);

        try
        {
            Assert.True(sut.IsRunning);
            Assert.Equal(99201, sut.Pid);

            // Stop fails to confirm exit: Kill returns, HasExited false, WaitForExitAsync immediately canceled
            sut.Stop();

            // Handle retained, IsRunning remains true, not disposed
            Assert.True(sut.IsRunning);
            Assert.Equal(99201, sut.Pid);
            Assert.Equal(0, handle.DisposeCallCount);
            Assert.Equal(1, handle.SuppressExitedEventCallCount);
            Assert.Equal(1, handle.KillCallCount);

            // Start after Stop must reject replacement when prior _handle still present/live or unknown (do not overwrite)
            var ex = Assert.Throws<InvalidOperationException>(() => sut.Start(1443, "newsecret"));
            Assert.Contains("prior instance", ex.Message);
            Assert.True(sut.IsRunning);
            Assert.Equal(99201, sut.Pid);

            // Repeated Stop retry cleanup succeeds once exit is confirmed
            handle.AllowExit = true;
            sut.Stop();

            Assert.False(sut.IsRunning);
            Assert.Null(sut.Pid);
            Assert.Equal(1, handle.DisposeCallCount);
        }
        finally
        {
            handle.AllowExit = true;
            sut.Dispose();
        }
    }

    [Fact]
    public void Stop_StubbornHandle_KillThrows_RetainsHandleAndIsRunning_StartRejectsReplacement_RetrySucceeds()
    {
        var fake = new FakeProcessRunner();
        var handle = new StubbornProcessHandle(pid: 99202) { KillThrows = true, AllowExit = false };

        var sut = new TgProxyManager(logger: null, runner: fake);
        SetProcessHandle(sut, handle);

        try
        {
            Assert.True(sut.IsRunning);
            Assert.Equal(99202, sut.Pid);

            // Stop fails: Kill throws Win32Exception, HasExited false
            sut.Stop();

            // Handle retained, IsRunning remains true, not disposed
            Assert.True(sut.IsRunning);
            Assert.Equal(99202, sut.Pid);
            Assert.Equal(0, handle.DisposeCallCount);
            Assert.Equal(1, handle.SuppressExitedEventCallCount);
            Assert.Equal(1, handle.KillCallCount);

            // Start won't replace
            var ex = Assert.Throws<InvalidOperationException>(() => sut.Start(1443, "newsecret"));
            Assert.Contains("prior instance", ex.Message);
            Assert.True(sut.IsRunning);
            Assert.Equal(99202, sut.Pid);

            // Retry cleanup succeeds once exited
            handle.AllowExit = true;
            sut.Stop();

            Assert.False(sut.IsRunning);
            Assert.Null(sut.Pid);
            Assert.Equal(1, handle.DisposeCallCount);
        }
        finally
        {
            handle.AllowExit = true;
            sut.Dispose();
        }
    }

    [Fact]
    public void Dispose_StubbornHandle_PermitsRepeatedDisposalRetryUntilCleanedUp()
    {
        var fake = new FakeProcessRunner();
        var handle = new StubbornProcessHandle(pid: 99203) { KillThrows = false, AllowExit = false };

        var sut = new TgProxyManager(logger: null, runner: fake);
        SetProcessHandle(sut, handle);

        // First dispose fails cleanup — does not mark fully disposed
        sut.Dispose();
        Assert.True(sut.IsRunning);
        Assert.Equal(0, handle.DisposeCallCount);

        // Retry cleanup via Dispose succeeds once process exits
        handle.AllowExit = true;
        sut.Dispose();
        Assert.False(sut.IsRunning);
        Assert.Equal(1, handle.DisposeCallCount);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static void SetProcessHandle(TgProxyManager manager, IProcessHandle? handle)
    {
        typeof(TgProxyManager)
            .GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, handle);
    }

    private static string LoadSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        Assert.Fail($"Required source file not found: {Path.Combine(relativeParts)}");
        return string.Empty;
    }

    private static string StripLineComments(string src) =>
        string.Join("\n",
            src.Split('\n')
               .Select(l => l.Contains("//") ? l[..l.IndexOf("//")] : l));

    private static string ExtractMethodBody(string src, string methodName)
    {
        var stripped = StripLineComments(src);
        var pattern = @"\b(?:(?:public|private|protected|internal|static|async)\s+)+(?:[A-Za-z_][A-Za-z0-9_]*(?:<[^>]+>)?\??)\s+" +
                      Regex.Escape(methodName) + @"\s*\(";

        var m = Regex.Match(stripped, pattern);
        if (!m.Success)
        {
            // Fallback: type token + methodName without modifier
            pattern = @"\b(?!(?:await|return|throw)\b)(?:[A-Za-z_][A-Za-z0-9_]*(?:<[^>]+>)?\??)\s+" +
                      Regex.Escape(methodName) + @"\s*\(";
            m = Regex.Match(stripped, pattern);
        }

        if (!m.Success)
        {
            Assert.Fail($"Method '{methodName}' declaration not found.");
            return string.Empty;
        }

        var openBraceIdx = stripped.IndexOf('{', m.Index + m.Length);
        if (openBraceIdx < 0)
        {
            Assert.Fail($"Opening brace for method '{methodName}' not found.");
            return string.Empty;
        }

        int depth = 0;
        for (int i = openBraceIdx; i < stripped.Length; i++)
        {
            if (stripped[i] == '{') depth++;
            else if (stripped[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return stripped.Substring(openBraceIdx, i - openBraceIdx + 1);
            }
        }

        Assert.Fail($"Matching closing brace for method '{methodName}' not found.");
        return string.Empty;
    }

    private static string ExtractPropertyBody(string src, string propertyName)
    {
        var stripped = StripLineComments(src);
        var pattern = @"\b(?:[A-Za-z_][A-Za-z0-9_]*(?:<[^>]+>)?\??)\s+" +
                      Regex.Escape(propertyName) + @"\s*\{";

        var match = Regex.Match(stripped, pattern);
        if (!match.Success)
        {
            Assert.Fail($"Property '{propertyName}' declaration not found.");
            return string.Empty;
        }

        var openBraceIdx = stripped.IndexOf('{', match.Index);
        if (openBraceIdx < 0)
        {
            Assert.Fail($"Opening brace for property '{propertyName}' not found.");
            return string.Empty;
        }

        int depth = 0;
        for (int i = openBraceIdx; i < stripped.Length; i++)
        {
            if (stripped[i] == '{') depth++;
            else if (stripped[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return stripped.Substring(openBraceIdx, i - openBraceIdx + 1);
            }
        }

        Assert.Fail($"Matching closing brace for property '{propertyName}' not found.");
        return string.Empty;
    }
}
