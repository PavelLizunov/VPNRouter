#nullable enable

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Reflection;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class TgProxyOneButtonMvpTests
{
    [Fact]
    public void IsPortAvailable_FreePort_ReturnsTrue()
    {
        var freePort = FindUnusedHighPort();
        Assert.True(
            TgProxyManager.IsPortAvailable(freePort),
            $"IsPortAvailable({freePort}) should be true for an unbound port.");
    }

    [Fact]
    public void IsPortAvailable_BoundPort_ReturnsFalse()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.False(
                TgProxyManager.IsPortAvailable(boundPort),
                $"IsPortAvailable({boundPort}) should be false while we hold the listener.");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void IsPortAvailable_InvalidPort_ReturnsFalse()
    {
        Assert.False(TgProxyManager.IsPortAvailable(0));
        Assert.False(TgProxyManager.IsPortAvailable(-1));
        Assert.False(TgProxyManager.IsPortAvailable(65536));
        Assert.False(TgProxyManager.IsPortAvailable(99999));
    }

    [Fact]
    public void TgProxyPortConflictException_NoOwnerHint_BuildsCleanMessage()
    {
        var ex = new TgProxyPortConflictException(port: 1443, ownerProcessHint: null);
        Assert.Equal(1443, ex.Port);
        Assert.Null(ex.OwnerProcessHint);
        Assert.Contains("1443", ex.Message);
        Assert.Contains("already in use", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TgProxyPortConflictException_WithOwnerHint_IncludesHintInMessage()
    {
        var ex = new TgProxyPortConflictException(port: 1443, ownerProcessHint: "python.exe (PID 1234)");
        Assert.Equal(1443, ex.Port);
        Assert.Equal("python.exe (PID 1234)", ex.OwnerProcessHint);
        Assert.Contains("1443", ex.Message);
        Assert.Contains("python.exe", ex.Message);
        Assert.Contains("1234", ex.Message);
    }

    [Fact]
    public void TgProxySecret_RoundTrips_AcrossSaveAndLoad()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-tg-test-{Guid.NewGuid():N}");
        var configPath = Path.Combine(tempDir, "config.yaml");
        Directory.CreateDirectory(tempDir);
        try
        {
            const string knownSecret = "abcdef0123456789abcdef0123456789";
            var settings = new AppSettings();
            settings.App.TgProxySecret = knownSecret;
            settings.App.TgProxyPort = 1443;

            InvokeInternalStaticSave(settings, configPath);

            var reloaded = SettingsLoader.Load(configPath);
            Assert.Equal(knownSecret, reloaded.App.TgProxySecret);
            Assert.Equal(1443, reloaded.App.TgProxyPort);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TgProxySecret_PreservedAcrossReload_NoRegeneration()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-tg-test-{Guid.NewGuid():N}");
        var configPath = Path.Combine(tempDir, "config.yaml");
        Directory.CreateDirectory(tempDir);
        try
        {
            const string knownSecret = "0011223344556677889900aabbccddee";
            var settings = new AppSettings();
            settings.App.TgProxySecret = knownSecret;
            InvokeInternalStaticSave(settings, configPath);

            var reload1 = SettingsLoader.Load(configPath);
            InvokeInternalStaticSave(reload1, configPath);
            var reload2 = SettingsLoader.Load(configPath);

            Assert.Equal(knownSecret, reload1.App.TgProxySecret);
            Assert.Equal(knownSecret, reload2.App.TgProxySecret);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static int FindUnusedHighPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void InvokeInternalStaticSave(AppSettings settings, string path)
    {
        SettingsLoader.Save(settings, path);
    }

    private static string? LoadSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }
}
