using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class HealthMonitorControlledReloadTests
{
    private sealed class StubScanner : IProcessScanner
    {
        private readonly string[] _names;
        public StubScanner(params string[] names) => _names = names;
        public ScanResult ScanForProfile(Profile profile) =>
            new() { ProcessNames = new List<string>(_names) };
    }

    private sealed class StubFirewall : IFirewallManager
    {
        public int EnableCount { get; private set; }
        public int DisableCount { get; private set; }
        public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true) { }
        public void EnableBlockRules() => EnableCount++;
        public void DisableBlockRules() => DisableCount++;
        public void DeleteAllRules() { }
        public void Dispose() { }
    }

    private static AppSettings Settings()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                RoutingMode = "full",
                BypassRussianTraffic = false,
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = "main", Server = "1.2.3.4", Port = 443, Uuid = "u",
                        Security = "reality",
                        Reality = new VlessRealityConfig { PublicKey = "k", ShortId = "ab" },
                    },
                },
            },
        };
        settings.Vless.ActiveServer = "main";
        return settings;
    }

    private static HealthMonitor Build(string[] scannerNames, out StubFirewall firewall,
        AppSettings? settings = null, string? clashApiSecret = null,
        Action<SingBoxManager>? configureSingBox = null, FakeHttpClient? http = null)
    {
        var singBox = new SingBoxManager(
            new SingBoxSettings { ClashApi = "127.0.0.1:65535" }, http: http ?? new FakeHttpClient());
        configureSingBox?.Invoke(singBox);
        firewall = new StubFirewall();
        var monitor = new HealthMonitor(singBox, new StubScanner(scannerNames), firewall,
            new MonitoringSettings { HealthCheckInterval = 3600, MaxRestartAttempts = 5, RestartOnFailure = true },
            api: new FakeSingBoxApi(), clashApiSecret: clashApiSecret);
        monitor.Start(new Profile { Name = "test" }, settings ?? Settings());
        return monitor;
    }

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static T GetField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void Rescan(HealthMonitor monitor) =>
        typeof(HealthMonitor).GetMethod("RescanProcesses", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(monitor, null);

    private static void RunScheduledRestart(HealthMonitor monitor) =>
        typeof(HealthMonitor).GetMethod("RunScheduledRestart", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(monitor, new object?[] { CancellationToken.None });

    private static string GenerateRecoveryConfig(HealthMonitor monitor) =>
        (string)typeof(HealthMonitor)
            .GetMethod("GenerateConfigJson", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(monitor, new object?[] { new List<string>(), null })!;

    private static int DeferredKillSwitchLift(HealthMonitor monitor) =>
        GetField<int>(monitor, "_deferredBlockRuleDisable");

    [Fact]
    public void FailedRestart_DoesNotAdvanceLastScan()
    {
        using var monitor = Build(new[] { "new.exe" }, out _);
        var previous = new ScanResult { ProcessNames = new List<string> { "old.exe" } };
        SetField(monitor, "_lastScan", previous);
        monitor.ReloadHookForTests = _ => false;

        Rescan(monitor);

        var current = GetField<ScanResult>(monitor, "_lastScan");
        Assert.Same(previous, current);
        Assert.Equal(new[] { "old.exe" }, current.ProcessNames);
    }

    [Fact]
    public void ConfirmedRestart_AdvancesLastScan()
    {
        using var monitor = Build(new[] { "new.exe" }, out _);
        SetField(monitor, "_lastScan", new ScanResult { ProcessNames = new List<string> { "old.exe" } });
        monitor.ReloadHookForTests = _ => true;

        Rescan(monitor);

        var current = GetField<ScanResult>(monitor, "_lastScan");
        Assert.Equal(new[] { "new.exe" }, current.ProcessNames);
    }

    [Fact]
    public void RecoveryStartedButUnconfirmed_DefersKillSwitchLift()
    {
        using var monitor = Build(new[] { "new.exe" }, out var firewall);
        monitor.ReloadOutcomeHookForTests = _ => ConfigApplyState.StartedUnconfirmed;

        RunScheduledRestart(monitor);

        Assert.Equal(1, firewall.EnableCount);
        Assert.Equal(0, firewall.DisableCount);
        Assert.Equal(1, DeferredKillSwitchLift(monitor));
        Assert.NotNull(GetField<string?>(monitor, "_pendingConfigConfirmation"));
        Assert.True(GetField<DateTime>(monitor, "_lastFullRestart") > DateTime.MinValue);
        Assert.Equal(new[] { "new.exe" }, GetField<ScanResult>(monitor, "_lastScan").ProcessNames);
    }

    [Fact]
    public void UnstartedProcessRescan_KeepsKillSwitchArmedWhenPriorRuntimeIsNotConfirmed()
    {
        using var monitor = Build(new[] { "new.exe" }, out var firewall);
        var previous = new ScanResult { ProcessNames = new List<string> { "old.exe" } };
        SetField(monitor, "_lastScan", previous);
        monitor.ReloadOutcomeHookForTests = _ => ConfigApplyState.NotStarted;

        Rescan(monitor);

        Assert.Same(previous, GetField<ScanResult>(monitor, "_lastScan"));
        Assert.Equal(1, firewall.EnableCount);
        Assert.Equal(0, firewall.DisableCount);
        Assert.Equal(0, DeferredKillSwitchLift(monitor));
    }

    [Fact]
    public void UnstartedProcessRescan_ReleasesKillSwitchWhenPriorRuntimeIsConfirmedLive()
    {
        var http = new FakeHttpClient().Setup("/configs", "{}");
        using var monitor = Build(new[] { "new.exe" }, out var firewall, http: http,
            configureSingBox: manager =>
            {
                var handle = new FakeProcessHandle(pid: 4242);
                SetField(manager, "_handle", handle);
                SetField(manager, "_macPrivilegeLaunchId", "fixture-launch");
                typeof(SingBoxManager).GetProperty(nameof(SingBoxManager.State))!
                    .GetSetMethod(nonPublic: true)!
                    .Invoke(manager, new object[] { SingBoxState.Running });
            });
        SetField(monitor, "_lastScan", new ScanResult { ProcessNames = new List<string> { "old.exe" } });
        monitor.ReloadOutcomeHookForTests = _ => ConfigApplyState.NotStarted;

        Rescan(monitor);

        Assert.Equal(1, firewall.EnableCount);
        Assert.Equal(1, firewall.DisableCount);
        Assert.Equal(0, DeferredKillSwitchLift(monitor));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public void RecoveryConfig_ReusesActiveControllerSecret(string configuredSecret)
    {
        const string activeSecret = "0123456789abcdef0123456789abcdef";
        var settings = Settings();
        settings.SingBox.ClashApiSecret = configuredSecret;
        using var monitor = Build(new[] { "new.exe" }, out _, settings: settings,
            clashApiSecret: activeSecret);

        var json = GenerateRecoveryConfig(monitor);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(activeSecret, document.RootElement
            .GetProperty("experimental").GetProperty("clash_api").GetProperty("secret").GetString());
    }

    [Fact]
    public void RecoveryConfigPin_ReplacesBlankControllerSecretInRecoveredCustomDocument()
    {
        const string activeSecret = "0123456789abcdef0123456789abcdef";
        using var monitor = Build(new[] { "new.exe" }, out _, clashApiSecret: activeSecret);
        var pin = typeof(HealthMonitor)
            .GetMethod("PinActiveControllerSecret", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var output = (string)pin.Invoke(monitor,
            new object?[] { """{"experimental":{"clash_api":{"secret":""}},"outbounds":[]}""" })!;

        using var document = JsonDocument.Parse(output);
        Assert.Equal(activeSecret, document.RootElement
            .GetProperty("experimental").GetProperty("clash_api").GetProperty("secret").GetString());
    }

    [Fact]
    public void AttemptRestart_NeverLiftsKillSwitchBlockRules()
    {
        var source = ReadHealthMonitorSource();
        var start = source.IndexOf("private void AttemptRestart()", StringComparison.Ordinal);
        Assert.True(start >= 0, "AttemptRestart is missing");
        var end = source.IndexOf("internal bool RunTunOrphanRecoveryCleanup", start, StringComparison.Ordinal);
        Assert.True(end > start, "AttemptRestart end marker is missing");
        var body = source[start..end];

        Assert.Contains("ApplyConfigWithProtection", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DisableBlockRules", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ReloadConfigAsync", body, StringComparison.Ordinal);
    }

    private static string ReadHealthMonitorSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory != null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "VPNRouter.Core", "Services", "HealthMonitor.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException("HealthMonitor.cs");
    }
}
