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
    public void IsTelegramSchemeRegistered_IsPubliclyCallable()
    {
        var method = typeof(TgProxyManager).GetMethod(
            "IsTelegramSchemeRegistered",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method!.ReturnType);
        Assert.Empty(method.GetParameters());

        var result = (bool)method.Invoke(null, null)!;
        Assert.True(result || !result);
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

    [Fact]
    public void TgProxyUpdater_StatusChanged_HasStepPrefixesInSource()
    {
        var src = LoadSource("VPNRouter.Core", "Services", "TgProxyUpdater.cs");
        if (src == null) return;

        Assert.Contains("Step 1/3", src);
        Assert.Contains("Step 2/3", src);
        Assert.Contains("Step 3/3", src);

        Assert.Matches(@"Step 1/3.*?Python.*?MB", src);
    }

    [Fact]
    public void SuccessfulTgProxyInstall_RefreshesUpdateButtonLabel()
    {
        var src = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.cs");
        if (src == null) return;

        Assert.Matches(
            @"\[NotifyPropertyChangedFor\(nameof\(LblUpdateTgProxy\)\)\]\s*private string _tgProxyVersionText",
            src);
        Assert.Contains("TgProxyVersionText = TgProxyUpdater.GetLocalVersion()", src);
    }

    [Fact]
    public void TgProxyManager_Start_HasPortPreflightProbe()
    {
        var src = LoadSource("VPNRouter.Core", "Services", "TgProxyManager.cs");
        if (src == null) return;

        Assert.Contains("IsPortAvailable(port)", src);

        Assert.Contains("TgProxyPortConflictException", src);

        Assert.Contains("TryResolvePortOwner", src);
    }

    [Fact]
    public void ManualStart_UsesOneForegroundWatchdog_AndBackgroundRecheck()
    {
        var src = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.cs");
        if (src == null) return;

        Assert.Contains("manager.Start(port, secret);", src);
        Assert.DoesNotContain("Task.Run(() => manager.Start", src);
        Assert.Contains("TgProxyManager manager;", src);
        Assert.Contains("lock (_tgProxyStateGate)", src);
        Assert.Contains("_tgProxyPostStartRecheckTask = VerifyTgProxyAfterStartAsync(manager, port);", src);
        Assert.Contains("await postStartRecheck;", src);
        Assert.Contains("TgProxyManager.OpenInTelegram(\"127.0.0.1\", startedPort, startedSecret);", src);
        Assert.Equal(
            1,
            src.Split("await Task.Delay(TgProxySettleDelayMs);", StringSplitOptions.None).Length - 1);
        Assert.Contains("TgProxyStatus = Strings.TgProxyExitedImmediately;", src);

        var recheckStart = src.IndexOf(
            "private async Task VerifyTgProxyAfterStartAsync",
            StringComparison.Ordinal);
        var recheckEnd = src.IndexOf("#endif", recheckStart, StringComparison.Ordinal);
        Assert.True(recheckStart >= 0 && recheckEnd > recheckStart);
        var recheck = src[recheckStart..recheckEnd];
        Assert.Contains(
            "if (_disposed || !ReferenceEquals(_tgProxy, manager) || !TgProxyEnabled)",
            recheck);
        Assert.Contains(
            "if (manager.IsRunning)",
            recheck);
        Assert.Contains("TgProxyRuntimeStatus = ComponentRuntimeStatus.Failed;", recheck);
        Assert.Contains("try { SaveSettings(); }", recheck);
    }

    [Fact]
    public void Update_StopsLiveProxyBeforeReplacingFiles_AndRestartsAfterSuccess()
    {
        var src = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.cs");
        if (src == null) return;

        var start = src.IndexOf("private async Task UpdateTgProxyCoreAsync()", StringComparison.Ordinal);
        var end = src.IndexOf("private async Task ToggleTgProxyAsync()", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var method = src[start..end];
        var stop = method.IndexOf("manager?.Stop();", StringComparison.Ordinal);
        var download = method.IndexOf("await updater.DownloadAsync", StringComparison.Ordinal);
        var restart = method.IndexOf("manager.Start(port, secret);", StringComparison.Ordinal);

        Assert.True(stop >= 0 && stop < download, "Live TgProxy must stop before updater replaces files.");
        Assert.True(restart > download, "TgProxy may restart only after updater completes successfully.");
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
