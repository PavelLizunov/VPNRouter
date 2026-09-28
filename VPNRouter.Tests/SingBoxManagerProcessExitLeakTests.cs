#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class SingBoxManagerProcessExitLeakTests
{
    private static readonly FieldInfo StopStateField =
        typeof(SingBoxManager).GetField("_stopState", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SingBoxManager._stopState field not found.");

    private static SingBoxSettings BuildIdleSettings() => new()
    {
        ExecutablePath = Path.Combine(Path.GetTempPath(), "nonexistent-sing-box-for-leak-test.exe"),
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndDisposeOne()
    {
        var manager = new SingBoxManager(BuildIdleSettings());
        var weakReference = new WeakReference(manager);

        StopStateField.SetValue(manager, 1);
        manager.Dispose();
        return weakReference;
    }

    private static List<WeakReference> CreateAndDispose(int count)
    {
        var refs = new List<WeakReference>(count);
        for (int i = 0; i < count; i++)
            refs.Add(CreateAndDisposeOne());
        return refs;
    }

    [Fact]
    public void DisposedManagers_AreNotRetainedByProcessExitHook()
    {
        var refs = CreateAndDispose(25);

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        int alive = refs.Count(r => r.IsAlive);

        Assert.True(alive == 0,
            $"{alive}/25 disposed SingBoxManager instances are still alive after a full GC. " +
            "The AppDomain.ProcessExit subscription is likely retaining them — Dispose() must " +
            "unsubscribe OnAppDomainProcessExit.");
    }



    private static string FindRepoFile(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, segments.Last());
    }
}
