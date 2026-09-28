#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class SingBoxManagerSuppressExitedEventTests
{
    [Fact]
    public void Source_StopInternal_CallsSuppressExitedEventBeforeKill_WindowsPath()
    {
        var src = ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.cs");

        var winBranch = src.IndexOf("var winStopped = false;", StringComparison.Ordinal);
        Assert.True(winBranch >= 0, "Windows graceful path landmark missing");

        var nextCatch = src.IndexOf("catch (Exception ex)", winBranch, StringComparison.Ordinal);
        Assert.True(nextCatch > winBranch, "Windows branch catch-block not found");
        var winWindow = src.Substring(winBranch, nextCatch - winBranch);

        var suppressIdx = winWindow.IndexOf("winTargetHandle.SuppressExitedEvent()", StringComparison.Ordinal);
        var killIdx = winWindow.IndexOf("winTargetHandle.Kill(entireProcessTree: true)", StringComparison.Ordinal);

        Assert.True(suppressIdx >= 0, "Expected `winTargetHandle.SuppressExitedEvent()` in SingBoxManager.cs (Windows graceful path)");
        Assert.True(killIdx >= 0, "Expected `winTargetHandle.Kill(entireProcessTree: true)` after SuppressExitedEvent in SingBoxManager.cs (Windows graceful path)");
        Assert.True(suppressIdx < killIdx,
            "SuppressExitedEvent must be called BEFORE Kill in the Windows graceful Stop path. " +
            $"suppressIdx={suppressIdx}, killIdx={killIdx} — wrong order would re-introduce brat's false-Crashed regression.");
    }

    [Fact]
    public void Source_StopInternal_CallsSuppressExitedEventBeforeKill_LinuxPath()
    {
        var src = ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.cs");

        var linuxBranch = src.IndexOf("v2.28.0: Linux capability-mode path", StringComparison.Ordinal);
        Assert.True(linuxBranch >= 0, "Linux capability path landmark missing");

        var nextCatch = src.IndexOf("catch (Exception ex)", linuxBranch, StringComparison.Ordinal);
        Assert.True(nextCatch > linuxBranch, "Linux branch catch-block not found");
        var linuxWindow = src.Substring(linuxBranch, nextCatch - linuxBranch);

        var suppressIdx = linuxWindow.IndexOf("targetHandle.SuppressExitedEvent()", StringComparison.Ordinal);
        var killIdx = linuxWindow.IndexOf("targetHandle.Kill(entireProcessTree: true)", StringComparison.Ordinal);

        Assert.True(suppressIdx >= 0, "Expected exact targetHandle.SuppressExitedEvent() in Linux capability-mode path");
        Assert.True(killIdx >= 0, "Expected exact targetHandle.Kill(entireProcessTree: true) in Linux capability-mode path");
        Assert.True(suppressIdx < killIdx,
            $"SuppressExitedEvent must be called BEFORE Kill in the Linux capability-mode Stop path. " +
            $"suppressIdx={suppressIdx}, killIdx={killIdx}.");
    }

    [Fact]
    public void IProcessHandle_HasSuppressExitedEventMethod()
    {
        var method = typeof(IProcessHandle).GetMethod(nameof(IProcessHandle.SuppressExitedEvent));
        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);
        Assert.Empty(method.GetParameters());
    }

    [Fact]
    public async Task Behavioural_AfterSuppress_SignalExit_DoesNotFireExited()
    {
        var handle = new FakeProcessHandle(pid: 99001);
        var exitedFired = 0;
        handle.Exited += (_, _) => Interlocked.Increment(ref exitedFired);

        handle.SuppressExitedEvent();
        Assert.Equal(1, handle.SuppressExitedEventCallCount);

        handle.SignalExit(exitCode: 0);

        await Task.Delay(50);

        Assert.Equal(0, exitedFired);
        Assert.True(handle.HasExited);
    }

    [Fact]
    public async Task Behavioural_WithoutSuppress_SignalExit_DoesFireExited()
    {
        var handle = new FakeProcessHandle(pid: 99002);
        var exitedFired = 0;
        handle.Exited += (_, _) => Interlocked.Increment(ref exitedFired);

        handle.SignalExit(exitCode: 0);

        await Task.Delay(50);

        Assert.Equal(1, exitedFired);
        Assert.True(handle.HasExited);
    }

    [Fact]
    public void Source_TgProxyManager_StopCallsSuppressExitedEventBeforeKill()
    {
        var src = ReadSourceFile("VPNRouter.Core", "Services", "TgProxyManager.cs");

        var suppressIdx = src.IndexOf("handle.SuppressExitedEvent()", StringComparison.Ordinal);
        var killIdx = src.IndexOf("handle.Kill(entireProcessTree: true)", suppressIdx + 1, StringComparison.Ordinal);

        Assert.True(suppressIdx >= 0, "Expected `handle.SuppressExitedEvent()` in TgProxyManager.cs");
        Assert.True(killIdx >= 0, "Expected `handle.Kill(entireProcessTree: true)` after Suppress in TgProxyManager.cs");
    }

    [Fact]
    public void Source_ZapretManager_StopCallsSuppressExitedEventBeforeKill()
    {
        var src = ReadSourceFile("VPNRouter.Core", "Services", "ZapretManager.cs");

        var suppressIdx = src.IndexOf("handle.SuppressExitedEvent()", StringComparison.Ordinal);
        var killIdx = src.IndexOf("handle.Kill(entireProcessTree: true)", suppressIdx + 1, StringComparison.Ordinal);

        Assert.True(suppressIdx >= 0, "Expected `handle.SuppressExitedEvent()` in ZapretManager.cs");
        Assert.True(killIdx >= 0, "Expected `handle.Kill(entireProcessTree: true)` after Suppress in ZapretManager.cs");
    }

    private static string ReadSourceFile(params string[] segments)
    {
        var thisAssembly = typeof(SingBoxManager).Assembly;
        var binDir = Path.GetDirectoryName(thisAssembly.Location)!;
        var dir = new DirectoryInfo(binDir);
        while (dir != null)
        {
            var candidate = Path.Combine((new[] { dir.FullName }).Concat(segments).ToArray());
            if (File.Exists(candidate)) return ReadAllParts(candidate);
            dir = dir.Parent;
        }
        var fallback = Path.Combine((new[] { Environment.CurrentDirectory }).Concat(segments).ToArray());
        if (!File.Exists(fallback))
            throw new FileNotFoundException($"Source file not found: {string.Join("/", segments)}");
        return ReadAllParts(fallback);
    }

    private static string ReadAllParts(string primaryPath)
    {
        var dir = Path.GetDirectoryName(primaryPath)!;
        var stem = Path.GetFileNameWithoutExtension(primaryPath);
        var parts = Directory.GetFiles(dir, stem + "*.cs")
            .Where(p =>
            {
                var fn = Path.GetFileName(p);
                return fn == stem + ".cs" || fn.StartsWith(stem + ".", StringComparison.Ordinal);
            })
            .OrderBy(p => p, StringComparer.Ordinal);
        return string.Join("\n", parts.Select(File.ReadAllText));
    }
}
