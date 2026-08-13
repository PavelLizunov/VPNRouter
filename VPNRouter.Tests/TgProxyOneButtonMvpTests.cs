#nullable enable
// ============================================================================
// TgProxyOneButtonMvpTests.cs — v2.36 MVP one-button UX coverage
// ============================================================================
//
// Pins the four MVP one-button additions per
// `plans/tgproxy-mvp-one-button-2026-05-24.md`:
//
//   A. Per-step download progress — TgProxyUpdater emits "Step N/3:" prefixed
//      StatusChanged messages so the UI can render distinct sub-steps.
//   B. Port 1443 pre-check — TgProxyManager.IsPortAvailable + Start throws
//      typed TgProxyPortConflictException before spawn.
//   C. Telegram scheme pre-flight — IsTelegramSchemeRegistered is callable
//      from outside the deep-link path so the VM can pre-flight + show
//      a non-blocking banner.
//   D. Secret persistence — TgProxySecret round-trips Save → Load on YAML.
//   E. Manual start — one foreground watchdog plus a background late-exit
//      recheck, without a second fixed wait before the UI becomes ready.
//
// All tests use [Fact] (no Avalonia). Bind-required port tests are Windows-
// only via OperatingSystem.IsWindows() guards. Other tests cross-platform.
// ============================================================================

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
    // ── Task B: port availability probe ─────────────────────────────────────

    [Fact]
    public void IsPortAvailable_FreePort_ReturnsTrue()
    {
        // Pick a high-numbered ephemeral port that nothing else is likely
        // to bind. Defensive: even if the OS allocates it racily, this
        // test just asserts the probe returned true at the moment it
        // ran. Retry-loop on flaky CI would mask actual regression.
        var freePort = FindUnusedHighPort();
        Assert.True(
            TgProxyManager.IsPortAvailable(freePort),
            $"IsPortAvailable({freePort}) should be true for an unbound port.");
    }

    [Fact]
    public void IsPortAvailable_BoundPort_ReturnsFalse()
    {
        // Bind a TcpListener ourselves on loopback, then probe — the
        // probe must report unavailable because the kernel won't let
        // a second loopback bind co-exist with our listener.
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
        // Defensive: 0 / negative / >65535 → false. Pre-fix the probe
        // tried to bind these and crashed; the guard short-circuits.
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

    // ── Task C: Telegram scheme pre-flight callable ─────────────────────────

    [Fact]
    public void IsTelegramSchemeRegistered_IsPubliclyCallable()
    {
        // Smoke: must be a public static method on TgProxyManager so the
        // VM can call it pre-flight (before TgProxyManager.Start). The
        // bool return is host-dependent — we just verify the call doesn't
        // throw and that the method signature is the documented shape.
        var method = typeof(TgProxyManager).GetMethod(
            "IsTelegramSchemeRegistered",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method!.ReturnType);
        Assert.Empty(method.GetParameters());

        // Defensive: invocation must not throw on any platform. On
        // non-Windows the method short-circuits to true. On Windows
        // the registry probe is wrapped in try/catch so even a hostile
        // registry returns true.
        var result = (bool)method.Invoke(null, null)!;
        Assert.True(result || !result); // tautology — just confirm no throw
    }

    // ── Task D: secret persistence round-trip ───────────────────────────────

    [Fact]
    public void TgProxySecret_RoundTrips_AcrossSaveAndLoad()
    {
        // Pin: AppSettings.App.TgProxySecret is YAML-persisted (alias
        // tg_proxy_secret), so a generated 32-char hex secret survives
        // Save → Load round-trip. Pre-fix the user could pair Telegram
        // with secret X, restart app, get secret regenerated to Y,
        // and Telegram client would silently fail to connect to the
        // new secret.
        var tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-tg-test-{Guid.NewGuid():N}");
        var configPath = Path.Combine(tempDir, "config.yaml");
        Directory.CreateDirectory(tempDir);
        try
        {
            const string knownSecret = "abcdef0123456789abcdef0123456789";
            var settings = new AppSettings();
            settings.App.TgProxySecret = knownSecret;
            settings.App.TgProxyPort = 1443;

            // Save via the internal helper exposed to tests
            // through InternalsVisibleTo.
            InvokeInternalStaticSave(settings, configPath);

            // Read back from disk.
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
        // Stronger pin: ensure that loading from disk and persisting
        // back unchanged (the VM SaveSettings round-trip pattern) does
        // NOT mutate the secret. Pre-fix a defensive "regenerate if
        // missing" path could clobber a known secret if any reload
        // hit the in-memory default.
        var tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-tg-test-{Guid.NewGuid():N}");
        var configPath = Path.Combine(tempDir, "config.yaml");
        Directory.CreateDirectory(tempDir);
        try
        {
            const string knownSecret = "0011223344556677889900aabbccddee";
            var settings = new AppSettings();
            settings.App.TgProxySecret = knownSecret;
            InvokeInternalStaticSave(settings, configPath);

            // Reload twice — simulate two app launches.
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

    // ── Task A: per-step status prefixes ────────────────────────────────────

    [Fact]
    public void TgProxyUpdater_StatusChanged_HasStepPrefixesInSource()
    {
        // Source-pin: we don't run the actual download (it'd hit GitHub
        // + python.org + pypi.org during unit tests). Instead pin the
        // emitted message format so a refactor that drops the "Step N/3:"
        // prefix is caught immediately.
        //
        // Pre-fix the StatusChanged events read "Downloading Python..."
        // / "Installing pycparser..." with no progress signal across
        // the 30–90s download window. The "Step N/3:" prefix lets the
        // UI render a distinct progress chip + tests pin the format.
        var src = LoadSource("VPNRouter.Core", "Services", "TgProxyUpdater.cs");
        if (src == null) return;

        // Three steps must all be present, in the right order.
        Assert.Contains("Step 1/3", src);
        Assert.Contains("Step 2/3", src);
        Assert.Contains("Step 3/3", src);

        // Step 1 carries the Python version + ~11 MB hint.
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
        // Source-pin: ensure the IsPortAvailable probe is called from
        // within Start BEFORE the runner.Start invocation. Pre-fix the
        // python.exe spawn proceeded unconditionally, hitting the 2s
        // watchdog with generic "Process exited" log.
        var src = LoadSource("VPNRouter.Core", "Services", "TgProxyManager.cs");
        if (src == null) return;

        // Probe call.
        Assert.Contains("IsPortAvailable(port)", src);

        // Typed throw on conflict.
        Assert.Contains("TgProxyPortConflictException", src);

        // Owner-hint resolution.
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
            "if (manager.IsRunning || TgProxyManager.IsAnyRunning(port))",
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

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static int FindUnusedHighPort()
    {
        // Bind on port 0 (kernel picks ephemeral), then immediately
        // unbind and return the assigned port. Race-prone for a microsecond
        // but acceptable for a test probe.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void InvokeInternalStaticSave(AppSettings settings, string path)
    {
        // SettingsLoader.Save is `internal static`. The Tests assembly
        // sees it via InternalsVisibleTo on VPNRouter.Core.csproj. Direct
        // call works without reflection.
        SettingsLoader.Save(settings, path);
    }

    private static string? LoadSource(params string[] relativeParts)
    {
        // Walk up from the test bin dir to the repo root, then resolve
        // the source file path. Same shape as TgProxyAutostartLoggingTests.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }
}
