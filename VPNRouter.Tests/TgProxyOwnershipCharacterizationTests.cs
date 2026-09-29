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

public sealed class TgProxyOwnershipCharacterizationTests
{
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

            sut.Stop();

            Assert.True(sut.IsRunning);
            Assert.Equal(99201, sut.Pid);
            Assert.Equal(0, handle.DisposeCallCount);
            Assert.Equal(1, handle.SuppressExitedEventCallCount);
            Assert.Equal(1, handle.KillCallCount);

            var ex = Assert.Throws<InvalidOperationException>(() => sut.Start(1443, "newsecret"));
            Assert.Contains("prior instance", ex.Message);
            Assert.True(sut.IsRunning);
            Assert.Equal(99201, sut.Pid);

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

            sut.Stop();

            Assert.True(sut.IsRunning);
            Assert.Equal(99202, sut.Pid);
            Assert.Equal(0, handle.DisposeCallCount);
            Assert.Equal(1, handle.SuppressExitedEventCallCount);
            Assert.Equal(1, handle.KillCallCount);

            var ex = Assert.Throws<InvalidOperationException>(() => sut.Start(1443, "newsecret"));
            Assert.Contains("prior instance", ex.Message);
            Assert.True(sut.IsRunning);
            Assert.Equal(99202, sut.Pid);

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

        sut.Dispose();
        Assert.True(sut.IsRunning);
        Assert.Equal(0, handle.DisposeCallCount);

        handle.AllowExit = true;
        sut.Dispose();
        Assert.False(sut.IsRunning);
        Assert.Equal(1, handle.DisposeCallCount);
    }

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
