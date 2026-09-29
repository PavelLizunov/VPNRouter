using System;
using System.IO;
using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class RuntimeStatusAdoptionTests
{
    [Fact]
    public void DetectServiceManagedVpn_UsesOwnershipFilteredDetector_NotBareProcessName()
    {
        var body = LoadMethodBody(
            new[] { "VPNRouter.App", "ViewModels", "MainWindowViewModel.cs" },
            "DetectServiceManagedVpn");
        if (body == null) return;

        var stripped = StripLineComments(body);

        Assert.Contains("RuntimeStatusDetector.IsVpnRunning", stripped);

        Assert.DoesNotContain("ProcessQuery.AnyAlive(\"sing-box\")", stripped);
        Assert.DoesNotContain("GetProcessesByName(\"sing-box\")", stripped);
    }

    [Fact]
    public void CandidateProcessNames_RenamedCustomExecutable_IsDiscoveredWithoutHardcodingSingBox()
    {
        var root = Path.Combine(Path.GetTempPath(), "vpnrouter-name-tests");
        var names = ProcessOwnership.CandidateProcessNames(
            Path.Combine(root, "default", "sing-box.exe"),
            Path.Combine(root, "runtime", "sing-box-lx.exe"),
            Path.Combine(root, "candidate", "sing-box-preview.exe"));

        Assert.Contains("sing-box", names);
        Assert.Contains("sing-box-lx", names);
        Assert.Contains("sing-box-preview", names);
    }

    [Fact]
    public void PersistedCliState_DelayedLegitimateWrite_HasNoSymmetricFiveSecondWindow()
    {
        const long childStart = 638900000000000000;
        var child = new OwnedProcessIdentity(4201, childStart, @"C:\runtime\sing-box-lx.exe");
        var owner = CurrentOwner(child);
        var delayedWrite = new DateTime(childStart, DateTimeKind.Utc).AddHours(3);

        Assert.True(ProcessOwnership.PersistedCliStateMatches(
            child.Pid,
            delayedWrite,
            identityReader: _ => child,
            ownerOverride: owner,
            commandLineReader: _ => throw new InvalidOperationException("v2 must not read command lines")));
    }

    [Fact]
    public void LegacyV1_LiveRecordedChild_IsCompatibleButUnrelatedVerifierCannotSatisfyIt()
    {
        var path = @"C:\ProgramData\VPNRouter\bin\sing-box-lx.exe";
        var currentConfig = @"C:\ProgramData\VPNRouter\config\current.json";
        var record = new RuntimeOwnerRecordRead(
            RuntimeOwnerRecordKind.LegacyV1,
            new RuntimeOwnerRecord(1, path, 0, 0, 5001, 0));
        var recordedChild = new OwnedProcessIdentity(5001, 10, path);
        var verifier = new OwnedProcessIdentity(5002, 20, path);

        var found = ProcessOwnership.FindOwnedSingBox(
            record,
            new[] { verifier, recordedChild },
            pid => pid == recordedChild.Pid
                ? $"\"{path}\" run -c \"{currentConfig}\""
                : $"\"{path}\" check -c verifier.json",
            @"C:\ProgramData\VPNRouter\bin",
            currentConfig);
        Assert.NotNull(found);
        Assert.Equal(recordedChild, found.Value);

        var verifierOnly = ProcessOwnership.FindOwnedSingBox(
            record,
            new[] { verifier },
            _ => $"\"{path}\" run -c verifier.json",
            @"C:\ProgramData\VPNRouter\bin",
            currentConfig);
        Assert.Null(verifierOnly);
    }

    [Fact]
    public void TrustedBinSubdirectory_IsEligibleForOrphanCleanup_ExternalConfigCandidateIsNot()
    {
        var bin = Path.Combine(Path.GetTempPath(), "vpnrouter", "bin");
        var nested = Path.Combine(bin, "custom", "sing-box-lx.exe");
        var externalCandidate = Path.Combine(Path.GetTempPath(), "external", "sing-box-lx.exe");

        Assert.True(ProcessOwnership.IsTrustedRuntimePath(nested, bin, null));
        Assert.False(ProcessOwnership.IsTrustedRuntimePath(externalCandidate, bin, null));
        Assert.True(ProcessOwnership.IsTrustedRuntimePath(
            externalCandidate,
            bin,
            externalCandidate));
    }

    [Fact]
    public void CurrentV2Polling_UsesExactIdentity_WithoutCommandLineOrWmiDiscovery()
    {
        var child = new OwnedProcessIdentity(6101, 7001, @"D:\runtime\sing-box-lx.exe");
        var owner = new OwnedProcessIdentity(6100, 7000, @"D:\runtime\VPNRouter.App.exe");
        var verifier = new OwnedProcessIdentity(6102, 7002, child.ExecutablePath);
        var commandLineReads = 0;

        var found = ProcessOwnership.FindOwnedSingBox(
            CurrentOwner(child, owner),
            new[] { verifier, child },
            _ =>
            {
                commandLineReads++;
                throw new InvalidOperationException("current v2 polling queried WMI");
            },
            @"C:\ProgramData\VPNRouter\bin",
            @"C:\ProgramData\VPNRouter\config\current.json",
            _ => owner);

        Assert.Equal(child, found);
        Assert.Equal(0, commandLineReads);
    }

    [Fact]
    public void Reconnect_RefreshedV2Record_SelectsSecondChild_NotStaleFirstChild()
    {
        var path = @"D:\runtime\custom-sing-box.exe";
        var owner = new OwnedProcessIdentity(6200, 7000, @"D:\runtime\VPNRouter.App.exe");
        var firstChild = new OwnedProcessIdentity(6201, 7001, path);
        var secondChild = new OwnedProcessIdentity(6202, 7002, path);
        var firstFound = ProcessOwnership.FindOwnedSingBox(
            CurrentOwner(firstChild, owner),
            new[] { firstChild },
            _ => throw new InvalidOperationException("v2 must not inspect command lines"),
            @"C:\ProgramData\VPNRouter\bin",
            @"C:\ProgramData\VPNRouter\config\current.json",
            _ => owner);
        var staleFound = ProcessOwnership.FindOwnedSingBox(
            CurrentOwner(firstChild, owner),
            new[] { secondChild },
            _ => throw new InvalidOperationException("v2 must not inspect command lines"),
            @"C:\ProgramData\VPNRouter\bin",
            @"C:\ProgramData\VPNRouter\config\current.json",
            _ => owner);
        var refreshedFound = ProcessOwnership.FindOwnedSingBox(
            CurrentOwner(secondChild, owner),
            new[] { firstChild, secondChild },
            _ => throw new InvalidOperationException("v2 must not inspect command lines"),
            @"C:\ProgramData\VPNRouter\bin",
            @"C:\ProgramData\VPNRouter\config\current.json",
            _ => owner);

        Assert.Equal(firstChild, firstFound);
        Assert.Null(staleFound);
        Assert.Equal(secondChild, refreshedFound);
        Assert.NotEqual(firstChild, refreshedFound);
    }

    [Fact]
    public void DeadRecordedChild_RetainedLockAndAnotherVerifier_DoesNotReportTunnel()
    {
        var tunnel = new OwnedProcessIdentity(7101, 8001, @"C:\vpnrouter\bin\sing-box-lx.exe");
        var owner = new OwnedProcessIdentity(7100, 8000, @"C:\vpnrouter\VPNRouter.App.exe");
        var verifier = new OwnedProcessIdentity(7102, 8002, tunnel.ExecutablePath);

        var found = ProcessOwnership.FindOwnedSingBox(
            CurrentOwner(tunnel, owner),
            new[] { verifier },
            _ => throw new InvalidOperationException("v2 must not inspect verifier command lines"),
            @"C:\vpnrouter\bin",
            @"C:\vpnrouter\config\current.json",
            _ => owner);

        Assert.Null(found);
        Assert.False(RuntimeStatusDetector.IsTunnelPresent(
            liveTunnelChild: found is not null,
            ownership: TunOwnershipStatus.Owned));
    }

    [Fact]
    public void UnavailableSemaphore_PreservesProcessOnlyFailOpen()
        => Assert.True(RuntimeStatusDetector.IsTunnelPresent(
            liveTunnelChild: true,
            ownership: TunOwnershipStatus.Unavailable));

    [Fact]
    public void PostCrashRestart_UpdatedPidAndStart_RetainsCliOwnedDetails()
    {
        const long restartedAt = 638900100000000000;
        var restarted = new OwnedProcessIdentity(8102, restartedAt, @"D:\runtime\sing-box-lx.exe");
        var owner = new OwnedProcessIdentity(8101, restartedAt - 1000, @"D:\runtime\VPNRouter.CLI.exe");
        var stateWrite = new DateTime(restartedAt, DateTimeKind.Utc).AddMinutes(2);

        Assert.True(ProcessOwnership.PersistedCliStateMatches(
            restarted.Pid,
            stateWrite,
            identityReader: pid => pid == owner.Pid ? owner : restarted,
            ownerOverride: CurrentOwner(restarted, owner)));
    }

    [Fact]
    public void ReusedPid_WithDifferentStartIdentity_DoesNotRetainCliDetails()
    {
        const int reusedPid = 9101;
        var recorded = new OwnedProcessIdentity(reusedPid, 1000, @"D:\runtime\sing-box-lx.exe");
        var reused = recorded with { StartedAtUtcTicks = 1001 };
        var owner = new OwnedProcessIdentity(9100, 900, @"D:\runtime\VPNRouter.CLI.exe");

        Assert.False(ProcessOwnership.PersistedCliStateMatches(
            reusedPid,
            new DateTime(2000, DateTimeKind.Utc),
            identityReader: pid => pid == owner.Pid ? owner : reused,
            ownerOverride: CurrentOwner(recorded, owner)));
    }

    [Fact]
    public void ReusedOwnerPid_WithDifferentStartIdentity_DoesNotRetainCliDetails()
    {
        var child = new OwnedProcessIdentity(9201, 2000, @"D:\runtime\sing-box-lx.exe");
        var owner = new OwnedProcessIdentity(9200, 1000, @"D:\runtime\VPNRouter.CLI.exe");
        var reusedOwner = owner with { StartedAtUtcTicks = 1001 };

        Assert.False(ProcessOwnership.PersistedCliStateMatches(
            child.Pid,
            new DateTime(3000, DateTimeKind.Utc),
            identityReader: pid => pid == owner.Pid ? reusedOwner : child,
            ownerOverride: CurrentOwner(child, owner)));
    }

    [Fact]
    public void FreshProcess_ReadsDurableExecutableA_IndependentlyOfConfiguredCandidateB()
    {
        using var temp = new TempDirectory();
        var ownerPath = Path.Combine(temp.Path, "runtime-owner.json");
        var durableA = new OwnedProcessIdentity(
            10101,
            638900200000000000,
            Path.Combine(temp.Path, "runtime-a", "sing-box-lx.exe"));
        ProcessOwnership.WriteRuntimeOwnerRecord(ownerPath, durableA);

        var loaded = ProcessOwnership.ReadRuntimeOwnerRecord(ownerPath);
        var configuredB = Path.Combine(temp.Path, "runtime-b", "sing-box-lx.exe");

        Assert.Equal(RuntimeOwnerRecordKind.CurrentV2, loaded.Kind);
        Assert.Equal(durableA.ExecutablePath, loaded.Record?.ExecutablePath);
        Assert.NotEqual(configuredB, loaded.Record?.ExecutablePath);
        Assert.False(ProcessOwnership.IsTrustedRuntimePath(
            configuredB,
            Path.Combine(temp.Path, "trusted-bin"),
            loaded.Record?.ExecutablePath));
    }

    [Fact]
    public void ConfigReader_MissingOrMalformedYaml_ContributesNoCandidate()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "missing.yaml");
        var malformed = Path.Combine(temp.Path, "malformed.yaml");
        File.WriteAllText(malformed, "singbox: [unterminated");

        Assert.Null(ProcessOwnership.ReadConfiguredExecutablePath(missing));
        Assert.Null(ProcessOwnership.ReadConfiguredExecutablePath(malformed));
    }

    [Fact]
    public void ConfigReader_EqualLengthSameTimestampRewrite_IsReadFreshEveryCall()
    {
        using var temp = new TempDirectory();
        var config = Path.Combine(temp.Path, "config.yaml");
        var pathA = Path.Combine(temp.Path, "aa", "sing-box-lx.exe");
        var pathB = Path.Combine(temp.Path, "bb", "sing-box-lx.exe");
        Assert.Equal(pathA.Length, pathB.Length);

        var yamlA = $"singbox:\n  executable_path: '{pathA}'\n";
        var yamlB = $"singbox:\n  executable_path: '{pathB}'\n";
        Assert.Equal(yamlA.Length, yamlB.Length);
        var timestamp = new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc);

        File.WriteAllText(config, yamlA);
        File.SetLastWriteTimeUtc(config, timestamp);
        Assert.Equal(pathA, ProcessOwnership.ReadConfiguredExecutablePath(config));

        File.WriteAllText(config, yamlB);
        File.SetLastWriteTimeUtc(config, timestamp);
        Assert.Equal(pathB, ProcessOwnership.ReadConfiguredExecutablePath(config));
    }

    [Fact]
    public void StaleCliState_DifferentDurableChild_DoesNotExposeCliDetails()
    {
        var live = new OwnedProcessIdentity(11102, 5000, @"C:\vpnrouter\bin\sing-box-lx.exe");

        Assert.False(ProcessOwnership.PersistedCliStateMatches(
            statePid: 11101,
            stateWrittenAtUtc: new DateTime(6000, DateTimeKind.Utc),
            identityReader: _ => live,
            ownerOverride: CurrentOwner(live)));
    }

    [Fact]
    public void SyncConnectedWithVpnRuntime_ShortCircuitsWhileConnecting()
    {
        var body = LoadMethodBody(
            new[] { "VPNRouter.App", "ViewModels", "MainWindowViewModel.RuntimeStatus.cs" },
            "SyncConnectedWithVpnRuntime");
        if (body == null) return;

        var stripped = StripLineComments(body);

        Assert.Matches(@"if\s*\(\s*IsConnecting\s*\)\s*return", stripped);

        Assert.DoesNotContain("ProcessQuery.AnyAlive(\"sing-box\")", stripped);
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

    private static string? LoadMethodBody(string[] relativeParts, string methodName)
    {
        var src = LoadSource(relativeParts);
        if (src == null) return null;

        var needle = methodName + "(";
        for (var from = 0; ; )
        {
            var sigIdx = src.IndexOf(needle, from, StringComparison.Ordinal);
            if (sigIdx < 0) return null;
            from = sigIdx + needle.Length;

            var paren = 0;
            var close = -1;
            for (var i = sigIdx + methodName.Length; i < src.Length; i++)
            {
                if (src[i] == '(') paren++;
                else if (src[i] == ')') { if (--paren == 0) { close = i; break; } }
            }
            if (close < 0) return null;

            var j = close + 1;
            while (j < src.Length && char.IsWhiteSpace(src[j])) j++;
            if (j >= src.Length || src[j] != '{') continue;

            var depth = 0;
            for (var i = j; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}')
                {
                    depth--;
                    if (depth == 0) return src.Substring(j, i - j + 1);
                }
            }
            return null;
        }
    }

    private static string StripLineComments(string src)
        => string.Join('\n',
            src.Split('\n').Select(l => l.Contains("//") ? l[..l.IndexOf("//", StringComparison.Ordinal)] : l));

    private static RuntimeOwnerRecordRead CurrentOwner(
        OwnedProcessIdentity child,
        OwnedProcessIdentity? owner = null)
    {
        var ownerIdentity = owner ?? child;
        return new(
            RuntimeOwnerRecordKind.CurrentV2,
            new RuntimeOwnerRecord(
                2,
                child.ExecutablePath,
                ownerIdentity.Pid,
                ownerIdentity.StartedAtUtcTicks,
                child.Pid,
                child.StartedAtUtcTicks));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "vpnrouter-status-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
