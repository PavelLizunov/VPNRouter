#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// The cleanup command removes what VPNRouter leaves in Windows. Every system call here goes through a fake process
/// runner or a fake registry: no test touches the real firewall, services, DNS settings or registry.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Layer", "Core")]
public sealed class SystemCleanupTests
{
    private const string OwnDriver = @"\??\C:\Program Files\VPNRouter\app\driver\mullvad-split-tunnel.sys";
    private const string MullvadDriver = @"\??\C:\Program Files\Mullvad VPN\resources\driver\mullvad-split-tunnel.sys";
    private const string OwnWinDivert = @"""C:\ProgramData\VPNRouter\zapret\bin\WinDivert64.sys""";
    private const string OtherWinDivert = @"C:\Tools\SomeOtherApp\WinDivert64.sys";

    private sealed class FakeRegistry : ICleanupRegistry
    {
        private readonly List<string> _events;

        public FakeRegistry(List<string> events) => _events = events;

        public bool DnsState { get; set; }
        public bool RunValue { get; set; }
        public int RestoreCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public bool ThrowOnRestore { get; set; }

        public bool DnsHardeningStatePresent() => DnsState;

        public void RestoreDnsHardening()
        {
            RestoreCalls++;
            _events.Add("dns-restore");
            if (ThrowOnRestore) throw new InvalidOperationException("registry is locked");
            DnsState = false;
        }

        public bool AutostartValuePresent() => RunValue;

        public void RemoveAutostartValue()
        {
            RemoveCalls++;
            _events.Add("run-remove");
            RunValue = false;
        }
    }

    private sealed class Harness
    {
        public readonly List<string> Events = new();
        public readonly FakeProcessRunner Runner = new();
        public readonly FakeRegistry Registry;
        public readonly List<string> Rules = new();
        public readonly Dictionary<string, string> Services = new(StringComparer.OrdinalIgnoreCase);
        public int DeleteExitCode { get; set; }

        public Harness()
        {
            Registry = new FakeRegistry(Events);

            Runner.OnRun(r => IsNetsh(r) && r.Arguments.Contains("show"), _ =>
                Task.FromResult(new ProcessResult(0, BuildNetshOutput(Rules), string.Empty, TimeSpan.Zero, false)));

            Runner.OnRun(r => IsNetsh(r) && r.Arguments.Contains("delete"), r =>
            {
                var name = r.Arguments.First(a => a.StartsWith("name=", StringComparison.Ordinal))["name=".Length..];
                Events.Add("netsh-delete:" + name);
                Rules.Remove(name);
                return Task.FromResult(new ProcessResult(0, "Ok.", string.Empty, TimeSpan.Zero, false));
            });

            Runner.OnRun(r => IsSc(r, "query"), r =>
            {
                var name = r.Arguments[1];
                return Task.FromResult(Services.ContainsKey(name)
                    ? new ProcessResult(0, $"SERVICE_NAME: {name}\r\n        TYPE               : 1  KERNEL_DRIVER\r\n        STATE              : 1  STOPPED\r\n", string.Empty, TimeSpan.Zero, false)
                    : new ProcessResult(1060, "[SC] EnumQueryServicesStatus:OpenService FAILED 1060:\r\n\r\nThe specified service does not exist as an installed service.", string.Empty, TimeSpan.Zero, false));
            });

            Runner.OnRun(r => IsSc(r, "qc"), r =>
            {
                var name = r.Arguments[1];
                var bin = Services.TryGetValue(name, out var b) ? b : string.Empty;
                var stdout = string.IsNullOrEmpty(bin)
                    ? $"[SC] QueryServiceConfig SUCCESS\r\n\r\nSERVICE_NAME: {name}\r\n        TYPE               : 1  KERNEL_DRIVER\r\n"
                    : $"[SC] QueryServiceConfig SUCCESS\r\n\r\nSERVICE_NAME: {name}\r\n        TYPE               : 1  KERNEL_DRIVER\r\n        BINARY_PATH_NAME   : {bin}\r\n";
                return Task.FromResult(new ProcessResult(0, stdout, string.Empty, TimeSpan.Zero, false));
            });

            Runner.OnRun(r => IsSc(r, "stop"), r =>
            {
                Events.Add("sc-stop:" + r.Arguments[1]);
                return Task.FromResult(new ProcessResult(0, "STATE : 1  STOPPED", string.Empty, TimeSpan.Zero, false));
            });

            Runner.OnRun(r => IsSc(r, "delete"), r =>
            {
                var name = r.Arguments[1];
                Events.Add("sc-delete:" + name);
                if (DeleteExitCode == 0) Services.Remove(name);
                return Task.FromResult(new ProcessResult(DeleteExitCode, DeleteExitCode == 0 ? "[SC] DeleteService SUCCESS" : "[SC] DeleteService FAILED", string.Empty, TimeSpan.Zero, false));
            });
        }

        public SystemCleanup Create() => new(Runner, Registry, logger: null, scPath: "sc.exe");

        private static bool IsNetsh(ProcessRequest r) => r.ExecutablePath == "netsh.exe";

        private static bool IsSc(ProcessRequest r, string verb) =>
            r.ExecutablePath == "sc.exe" && r.Arguments.Count > 0 && r.Arguments[0] == verb;

        private static string BuildNetshOutput(IEnumerable<string> names)
        {
            var sb = new StringBuilder();
            foreach (var n in names)
            {
                sb.AppendLine($"Rule Name:                            {n}");
                sb.AppendLine("----------------------------------------------------------------------");
                sb.AppendLine("Enabled:                              Yes");
                sb.AppendLine("Direction:                            Out");
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    private static Harness WithEverything()
    {
        var h = new Harness();
        h.Rules.AddRange(new[]
        {
            "VPNRouter_Block_discord",
            "VPNRouter-DnsLockdown-UDP53",
            "0_VPNRouter-DnsLockdown-TunAllow",
            "Microsoft Edge (mDNS-Out)",
        });
        h.Registry.DnsState = true;
        h.Registry.RunValue = true;
        h.Services["mullvad-split-tunnel"] = OwnDriver;
        h.Services["WinDivert"] = OwnWinDivert;
        return h;
    }

    [Fact]
    public void DryRun_ReportsEverythingAndChangesNothing()
    {
        var h = WithEverything();

        var report = h.Create().Run(dryRun: true);

        Assert.True(report.DryRun);
        Assert.False(report.HasFailures);
        Assert.Equal(7, report.Count(CleanupOutcome.WouldRemove));
        Assert.Empty(h.Events);
        Assert.Equal(0, h.Registry.RestoreCalls);
        Assert.Equal(0, h.Registry.RemoveCalls);
        Assert.Equal(4, h.Rules.Count);
        Assert.Equal(2, h.Services.Count);
        Assert.DoesNotContain(h.Runner.RunCalls, c => c.Arguments.Contains("delete") || (c.Arguments.Count > 0 && c.Arguments[0] is "stop" or "delete"));
        Assert.Contains("nothing was changed", report.Format());
    }

    [Fact]
    public void Run_RemovesOnlyTheRulesWithProjectPrefixes()
    {
        var h = WithEverything();

        var report = h.Create().Run(dryRun: false);

        Assert.Equal(new[] { "Microsoft Edge (mDNS-Out)" }, h.Rules);
        Assert.Equal(3, report.Actions.Count(a => a.Area == "firewall" && a.Outcome == CleanupOutcome.Removed));
        Assert.DoesNotContain(h.Events, e => e.Contains("Microsoft Edge"));
        Assert.Equal(1, h.Events.Count(e => e == "netsh-delete:VPNRouter_Block_discord"));
    }

    [Fact]
    public void Run_RestoresDnsOnlyAfterTheFirewallRulesAreGone()
    {
        var h = WithEverything();

        h.Create().Run(dryRun: false);

        var lastFirewall = h.Events.FindLastIndex(e => e.StartsWith("netsh-delete:", StringComparison.Ordinal));
        var dns = h.Events.IndexOf("dns-restore");
        Assert.True(lastFirewall >= 0 && dns > lastFirewall, string.Join(", ", h.Events));
        Assert.Equal(1, h.Registry.RestoreCalls);
    }

    [Fact]
    public void Run_StopsThenDeletesTheServicesThatAreTheirs()
    {
        var h = WithEverything();

        var report = h.Create().Run(dryRun: false);

        Assert.Empty(h.Services);
        var events = h.Events.Where(e => e.StartsWith("sc-", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[]
        {
            "sc-stop:mullvad-split-tunnel", "sc-delete:mullvad-split-tunnel",
            "sc-stop:WinDivert", "sc-delete:WinDivert",
        }, events);
        Assert.Equal(2, report.Actions.Count(a => a.Area is "driver" or "zapret" && a.Outcome == CleanupOutcome.Removed));
    }

    [Fact]
    public void Run_LeavesAnotherVpnsSplitTunnelDriverAlone()
    {
        var h = WithEverything();
        h.Services["mullvad-split-tunnel"] = MullvadDriver;

        var report = h.Create().Run(dryRun: false);

        Assert.True(h.Services.ContainsKey("mullvad-split-tunnel"));
        Assert.DoesNotContain(h.Events, e => e.EndsWith("mullvad-split-tunnel", StringComparison.Ordinal));
        var action = Assert.Single(report.Actions, a => a.Area == "driver");
        Assert.Equal(CleanupOutcome.Skipped, action.Outcome);
        Assert.Contains("another program", action.Detail);
        Assert.False(report.HasFailures);
    }

    [Theory]
    [InlineData(OtherWinDivert, "another program")]
    [InlineData("", "cannot read")]
    public void Run_LeavesAWinDivertServiceAloneWhenItIsNotVpnrouters(string binPath, string expectedDetail)
    {
        var h = new Harness();
        h.Services["WinDivert"] = binPath;

        var report = h.Create().Run(dryRun: false);

        Assert.True(h.Services.ContainsKey("WinDivert"));
        Assert.DoesNotContain(h.Events, e => e.EndsWith("WinDivert", StringComparison.Ordinal));
        var action = Assert.Single(report.Actions, a => a.Target == "WinDivert");
        Assert.Equal(CleanupOutcome.Skipped, action.Outcome);
        Assert.Contains(expectedDetail, action.Detail);
    }

    [Fact]
    public void Run_CountsAServiceMarkedForDeletionAsRemoved()
    {
        var h = new Harness { DeleteExitCode = 1072 };
        h.Services["mullvad-split-tunnel"] = OwnDriver;

        var report = h.Create().Run(dryRun: false);

        var action = Assert.Single(report.Actions, a => a.Area == "driver");
        Assert.Equal(CleanupOutcome.Removed, action.Outcome);
        Assert.Contains("marked for deletion", action.Detail);
        Assert.False(report.HasFailures);
    }

    [Fact]
    public void Run_ReportsAFailedDeleteAndKeepsGoing()
    {
        var h = WithEverything();
        h.DeleteExitCode = 5;

        var report = h.Create().Run(dryRun: false);

        Assert.True(report.HasFailures);
        Assert.Equal(2, report.Count(CleanupOutcome.Failed));
        Assert.Equal(1, h.Registry.RemoveCalls);
        Assert.False(h.Registry.RunValue);
        Assert.Contains("FAILED", report.Format());
    }

    [Fact]
    public void Run_IsIdempotent()
    {
        var h = WithEverything();
        var cleanup = h.Create();

        cleanup.Run(dryRun: false);
        var eventsAfterFirst = h.Events.Count;
        var second = cleanup.Run(dryRun: false);

        Assert.Equal(eventsAfterFirst, h.Events.Count);
        Assert.False(second.HasFailures);
        Assert.Equal(0, second.Count(CleanupOutcome.Removed));
        Assert.All(second.Actions, a => Assert.Equal(CleanupOutcome.NotPresent, a.Outcome));
        Assert.Equal(1, h.Registry.RestoreCalls);
        Assert.Equal(1, h.Registry.RemoveCalls);
    }

    [Fact]
    public void Run_AFailingStepDoesNotStopTheOthers()
    {
        var h = WithEverything();
        h.Registry.ThrowOnRestore = true;

        var report = h.Create().Run(dryRun: false);

        var dns = Assert.Single(report.Actions, a => a.Area == "dns");
        Assert.Equal(CleanupOutcome.Failed, dns.Outcome);
        Assert.Contains("registry is locked", dns.Detail);
        Assert.Empty(h.Services);
        Assert.False(h.Registry.RunValue);
        Assert.Single(h.Rules);
        Assert.True(report.HasFailures);
    }

    [Fact]
    public void Run_RemovesTheAutostartValueOnlyWhenItIsThere()
    {
        var h = new Harness();
        var report = h.Create().Run(dryRun: false);
        Assert.Equal(0, h.Registry.RemoveCalls);
        Assert.Equal(CleanupOutcome.NotPresent, Assert.Single(report.Actions, a => a.Area == "autostart").Outcome);

        h.Registry.RunValue = true;
        report = h.Create().Run(dryRun: false);
        Assert.Equal(1, h.Registry.RemoveCalls);
        Assert.Equal(CleanupOutcome.Removed, Assert.Single(report.Actions, a => a.Area == "autostart").Outcome);
    }

    [Fact]
    public void Run_OnACleanMachineReportsNothingToDo()
    {
        var h = new Harness();

        var report = h.Create().Run(dryRun: false);

        Assert.False(report.HasFailures);
        Assert.All(report.Actions, a => Assert.Equal(CleanupOutcome.NotPresent, a.Outcome));
        Assert.Empty(h.Events);
    }

    [Theory]
    [InlineData("        BINARY_PATH_NAME   : \\??\\C:\\x\\mullvad-split-tunnel.sys\r\n", "\\??\\C:\\x\\mullvad-split-tunnel.sys")]
    [InlineData("BINARY_PATH_NAME   : \"C:\\Program Files\\VPNRouter\\app\\VPNRouter.Service.exe\"", "\"C:\\Program Files\\VPNRouter\\app\\VPNRouter.Service.exe\"")]
    [InlineData("SERVICE_NAME: x\r\n", null)]
    [InlineData("BINARY_PATH_NAME   :   \r\n", null)]
    public void ParseBinaryPath_ReadsTheValueAfterTheColon(string output, string? expected)
    {
        Assert.Equal(expected, SystemCleanup.ParseBinaryPath(output));
    }

    [Theory]
    [InlineData(0, "SERVICE_NAME: x\r\nSTATE : 4  RUNNING", false, true)]
    [InlineData(1060, "[SC] EnumQueryServicesStatus:OpenService FAILED 1060:", false, false)]
    [InlineData(0, "SERVICE_NAME: x", true, false)]
    [InlineData(1, "anything", false, false)]
    public void ServiceExists_TreatsErrorOneThousandSixtyAndTimeoutsAsMissing(int exit, string stdout, bool timedOut, bool expected)
    {
        Assert.Equal(expected, SystemCleanup.ServiceExists(new ProcessResult(exit, stdout, string.Empty, TimeSpan.Zero, timedOut)));
    }

    [Theory]
    [InlineData(OwnDriver, true, true)]
    [InlineData(MullvadDriver, true, false)]
    [InlineData(@"C:\Program Files\VPNRouter\app\driver\other.sys", true, false)]
    [InlineData(OwnWinDivert, false, true)]
    [InlineData(OtherWinDivert, false, false)]
    [InlineData(null, false, false)]
    [InlineData("   ", true, false)]
    public void IsOwnBinaryPath_OnlyAcceptsFilesInAVpnrouterFolder(string? binPath, bool driver, bool expected)
    {
        Assert.Equal(expected, SystemCleanup.IsOwnBinaryPath(binPath, driver));
    }

    [Fact]
    public void Format_ListsEveryActionAndASummaryLine()
    {
        var report = new CleanupReport(false, new[]
        {
            new CleanupAction("firewall", "VPNRouter_Block_x", CleanupOutcome.Removed),
            new CleanupAction("service", "WinDivert", CleanupOutcome.Skipped, "belongs to another program"),
            new CleanupAction("dns", "saved DNS settings", CleanupOutcome.NotPresent),
        });

        var text = report.Format();

        Assert.Contains("[removed] firewall: VPNRouter_Block_x", text);
        Assert.Contains("[skipped] service: WinDivert (belongs to another program)", text);
        Assert.Contains("[not present] dns: saved DNS settings", text);
        Assert.EndsWith("Removed 1, already clean 1, skipped 1, failed 0.", text);
    }

    [Fact]
    public void ManagedRulePrefixes_AreTheThreeProjectPrefixes()
    {
        Assert.Equal(
            new[] { "VPNRouter_Block_", "VPNRouter-DnsLockdown-", "0_VPNRouter-DnsLockdown-" },
            FirewallManager.ManagedRulePrefixes.ToArray());
    }
}
