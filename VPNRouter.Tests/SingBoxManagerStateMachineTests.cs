#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class SingBoxManagerStateMachineTests
{
    private static SingBoxSettings DefaultSettings() => new()
    {
        ExecutablePath = @"C:\nonexistent\sing-box.exe",
        ClashApi = "127.0.0.1:9090"
    };

    private static SingBoxManager BuildManager(IHttpClient http) =>
        new SingBoxManager(DefaultSettings(), logger: null, http: http);

    private static object? GetField(SingBoxManager m, string fieldName)
    {
        var f = typeof(SingBoxManager).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"SingBoxManager has no field '{fieldName}'");
        return f.GetValue(m);
    }

    private static void SetField(SingBoxManager m, string fieldName, object? value)
    {
        var f = typeof(SingBoxManager).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"SingBoxManager has no field '{fieldName}'");
        f.SetValue(m, value);
    }

    private static T InvokePrivate<T>(SingBoxManager m, string method, params object?[] args)
    {
        var mi = typeof(SingBoxManager).GetMethod(method,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"SingBoxManager has no method '{method}'");
        try
        {
            return (T)mi.Invoke(m, args)!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    private static (Process p, IDisposable cleanup) SpawnLongLivedChild()
    {
        ProcessStartInfo psi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 60 > NUL")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 60\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to spawn test child process");

        var cleanup = new ChildCleanup(p);
        return (p, cleanup);
    }

    private sealed class ChildCleanup : IDisposable
    {
        private readonly Process _p;
        private bool _disposed;
        public ChildCleanup(Process p) { _p = p; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_p.HasExited) _p.Kill(entireProcessTree: true);
            }
            catch { }
            try { _p.Dispose(); } catch { }
        }
    }

    [Fact]
    public void Construction_InitialState_IsStopped()
    {
        using var manager = BuildManager(new FakeHttpClient());

        Assert.Equal(SingBoxState.Stopped, manager.State);
    }

    [Fact]
    public void Stop_OnIdleManager_IsNoOp_StateStaysStopped()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var manager = BuildManager(new FakeHttpClient());

        manager.Stop();

        Assert.Equal(SingBoxState.Stopped, manager.State);
    }

    [Fact]
    public void Stop_IsIdempotent_SecondCallDoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var manager = BuildManager(new FakeHttpClient());

        manager.Stop();
        manager.Stop();

        Assert.Equal(SingBoxState.Stopped, manager.State);
    }

    [Fact]
    public void Dispose_OnIdleManager_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;

        var manager = BuildManager(new FakeHttpClient());
        manager.Dispose();
        Assert.Equal(SingBoxState.Stopped, manager.State);
    }

    [Fact]
    public void Dispose_IsIdempotent_SecondCallIsNoOp()
    {
        if (!OperatingSystem.IsWindows()) return;

        var manager = BuildManager(new FakeHttpClient());
        manager.Dispose();
        manager.Dispose();

        Assert.Equal(SingBoxState.Stopped, manager.State);
    }

    [Fact]
    public void IsClashApiAlive_HttpStub200_ReturnsTrue()
    {
        var http = new FakeHttpClient()
            .Setup("127.0.0.1:9090/configs", "{}");
        using var manager = BuildManager(http);

        var result = InvokePrivate<bool>(manager, "IsClashApiAlive");

        Assert.True(result);
        Assert.Single(http.SentRequests);
        var req = http.SentRequests[0];
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("http://127.0.0.1:9090/configs", req.Uri.ToString());
    }

    [Fact]
    public void IsClashApiAlive_HttpStub500_ReturnsFalse()
    {
        var http = new FakeHttpClient()
            .Setup("127.0.0.1:9090/configs",
                new HttpResponse(
                    StatusCode: 500,
                    Headers: new Dictionary<string, string>(),
                    Body: Encoding.UTF8.GetBytes("internal error"),
                    Duration: TimeSpan.FromMilliseconds(1)));
        using var manager = BuildManager(http);

        var result = InvokePrivate<bool>(manager, "IsClashApiAlive");

        Assert.False(result);
    }

    [Fact]
    public void IsClashApiAlive_TransportException_ReturnsFalse()
    {
        var http = new FakeHttpClient()
            .ThrowOn("127.0.0.1:9090/configs",
                new HttpRequestException("Connection refused"));
        using var manager = BuildManager(http);

        var result = InvokePrivate<bool>(manager, "IsClashApiAlive");

        Assert.False(result);
    }

    [Fact]
    public void TryHotReload_ProcessNull_ReturnsFalseWithoutHttpCall()
    {
        var http = new FakeHttpClient();
        using var manager = BuildManager(http);

        var result = InvokePrivate<bool>(manager, "TryHotReload");

        Assert.False(result);
        Assert.Empty(http.SentRequests);
    }

    [Fact]
    public void TryReloadConfigJson_NullProcess_ReturnsFalse()
    {
        var http = new FakeHttpClient();
        using var manager = BuildManager(http);

        try
        {
            Directory.CreateDirectory(VPNRouter.Core.AppPaths.ConfigDir);
        }
        catch { }

        var result = manager.TryReloadConfigJson("{\"log\":{\"level\":\"info\"}}");

        Assert.False(result);
        Assert.Empty(http.SentRequests);
    }

    [Fact]
    public void TryHotReload_PutShape_PreservedAfter_3G2_Migration()
    {
        var http = new FakeHttpClient()
            .Setup("127.0.0.1:9090/configs",
                new HttpResponse(
                    StatusCode: 200,
                    Headers: new Dictionary<string, string>(),
                    Body: Array.Empty<byte>(),
                    Duration: TimeSpan.FromMilliseconds(1)));
        using var manager = BuildManager(http);

        var fakeHandle = new FakeProcessHandle(pid: 42424);
        try
        {
            SetField(manager, "_handle", fakeHandle);
            SetField(manager, "_currentConfigPath", @"C:\fake\current.json");

            var result = InvokePrivate<bool>(manager, "TryHotReload");

            Assert.True(result);
            Assert.Single(http.SentRequests);

            var req = http.SentRequests[0];
            Assert.Equal(HttpMethod.Put, req.Method);
            Assert.Equal("http://127.0.0.1:9090/configs?force=true",
                req.Uri.ToString());
            Assert.Equal("application/json", req.BodyContentType);
            Assert.NotNull(req.Body);
            var bodyJson = Encoding.UTF8.GetString(req.Body!);
            Assert.Contains("\"path\":", bodyJson);
            Assert.Contains("C:\\\\fake\\\\current.json", bodyJson);
        }
        finally
        {
            SetField(manager, "_handle", null);
            fakeHandle.Dispose();
        }
    }

    private static string? LoadSingBoxManagerSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "VPNRouter.Core", "Services", "SingBoxManager.cs");
            if (File.Exists(candidate)) return SingBoxSourceText.ReadAll(candidate);
        }
        return null;
    }

    private static string StripLineComments(string src)
    {
        return string.Join('\n',
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));
    }
}
