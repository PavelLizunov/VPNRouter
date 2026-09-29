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
    public async Task Behavioural_AfterSuppress_SignalExit_DoesNotFireExited()
    {
        var handle = new FakeProcessHandle(pid: 99001);
        var exitedFired = 0;
        handle.Exited += (_, _) => Interlocked.Increment(ref exitedFired);

        handle.SuppressExitedEvent();
        Assert.Equal(1, handle.SuppressExitedEventCallCount);

        handle.SignalExit(exitCode: 0);

        await Task.Delay(50, TestContext.Current.CancellationToken);

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

        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitedFired);
        Assert.True(handle.HasExited);
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
