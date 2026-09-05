#nullable enable

using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// NIGHT-07 source guards and readiness invariants for:
/// <list type="bullet">
///   <item><c>TwoPhaseStartCoordinator.cs</c> — Phase B and Phase A invariants.</item>
///   <item><c>MainWindowViewModel.Connection.cs</c> — UI status consumption and readiness guards.</item>
///   <item><c>MainWindowViewModel.RuntimeStatus.cs</c> — SyncConnectedWithVpnRuntime owned readiness guard and service adoption invariants.</item>
/// </list>
///
/// Strips comments to guard against comment-only bypass and ensures assertions
/// test actual executable logic without requiring real Avalonia VM instantiation or OS engine.
/// </summary>
public sealed class NightTypedReadinessTests
{
    private static string? LoadSourceFile(string relativePath)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var depth = 0; depth < 8 && directory != null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }
        return null;
    }

    private static string StripComments(string source)
    {
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var noLine = Regex.Replace(noBlock, @"//.*", "");
        return noLine;
    }

    [Fact]
    public void OnEngineStatus_LegacyConnectedStrings_CannotSetIsConnectedFromFalse_SourceGuard()
    {
        var source = LoadSourceFile(Path.Combine("VPNRouter.App", "ViewModels", "MainWindowViewModel.Connection.cs"));
        Assert.NotNull(source);

        var clean = StripComments(source);

        // Find OnEngineStatus method definition
        var methodIdx = clean.IndexOf("void OnEngineStatus(string status)", StringComparison.Ordinal);
        Assert.True(methodIdx >= 0, "OnEngineStatus method must exist");

        var methodBody = clean.Substring(methodIdx, Math.Min(1500, clean.Length - methodIdx));

        // 1. Must check status starts with Connected or VPN Router is running
        var statusBranchIdx = methodBody.IndexOf("status.StartsWith(\"Connected\")", StringComparison.Ordinal);
        Assert.True(statusBranchIdx >= 0, "Must inspect Connected string branch");

        // 2. Must guard: cannot SET IsConnected from false!
        // Must contain if (!IsConnected) return; before any display refresh
        var guardIdx = methodBody.IndexOf("if (!IsConnected) return;", statusBranchIdx, StringComparison.Ordinal);
        Assert.True(guardIdx >= 0, "OnEngineStatus must return early when !IsConnected, never promoting from false");

        // 3. Must NOT contain IsConnected = true inside this branch
        var branchEnd = methodBody.IndexOf("else if (status == \"Stopped\")", statusBranchIdx, StringComparison.Ordinal);
        Assert.True(branchEnd > statusBranchIdx);
        var branchText = methodBody.Substring(statusBranchIdx, branchEnd - statusBranchIdx);

        Assert.DoesNotContain("IsConnected = true", branchText, StringComparison.Ordinal);

        // 4. Must call RestoreConnectedStatus() to refresh display when already true
        Assert.Contains("RestoreConnectedStatus();", branchText, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoPhaseOutcomeConnected_CallsRestoreConnectedStatus_SourceGuard()
    {
        var source = LoadSourceFile(Path.Combine("VPNRouter.App", "ViewModels", "MainWindowViewModel.Connection.cs"));
        Assert.NotNull(source);

        var clean = StripComments(source);

        // In ToggleConnectionAsync, outcome == Connected must call RestoreConnectedStatus()
        // so the UI never stays stuck on the stale "Connecting..." label
        var outcomeConnectedIdx = clean.IndexOf("outcome == Internals.TwoPhaseStartOutcome.Connected", StringComparison.Ordinal);
        Assert.True(outcomeConnectedIdx >= 0, "outcome == Connected check must exist");

        var outcomeBody = clean.Substring(outcomeConnectedIdx, Math.Min(800, clean.Length - outcomeConnectedIdx));
        Assert.Contains("RestoreConnectedStatus();", outcomeBody, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericCatch_PreservesGreenOnlyIfAlreadyTypedReady_SourceGuard()
    {
        var source = LoadSourceFile(Path.Combine("VPNRouter.App", "ViewModels", "MainWindowViewModel.Connection.cs"));
        Assert.NotNull(source);

        var clean = StripComments(source);

        // Generic catch (Exception ex) in ToggleConnectionAsync
        var catchIdx = clean.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(catchIdx >= 0, "catch (Exception ex) must exist in ToggleConnectionAsync");

        var catchBody = clean.Substring(catchIdx, Math.Min(1200, clean.Length - catchIdx));

        // 1. Must check IsConnected && _engine.IsRunning (never _engine.IsRunning alone)
        var guardPattern = clean.IndexOf("if (IsConnected && _engine.IsRunning)", catchIdx, StringComparison.Ordinal);
        Assert.True(guardPattern >= 0, "Catch must only preserve green if already typed ready IsConnected");

        // 2. Must NOT contain unconditional IsConnected = true under engine.IsRunning
        Assert.DoesNotContain("if (_engine.IsRunning)\r\n                {\r\n                    IsConnected = true", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("if (_engine.IsRunning)\n                {\n                    IsConnected = true", clean, StringComparison.Ordinal);

        // 3. Stop/failed path in else branch must stop engine and set IsConnected = false
        Assert.Contains("_engine.Stop()", catchBody, StringComparison.Ordinal);
        Assert.Contains("IsConnected = false;", catchBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoPhaseStartCoordinator_PhaseBCtsCanceledOnlyAfterFinalOutcome_SourceGuard()
    {
        var source = LoadSourceFile(Path.Combine("VPNRouter.App", "ViewModels", "Internals", "TwoPhaseStartCoordinator.cs"));
        Assert.NotNull(source);

        var clean = StripComments(source);

        // Phase B WhenAny and second wait
        var phaseBIdx = clean.IndexOf("Task.WhenAny(connectedTcs.Task, startTask, phaseBDelay)", StringComparison.Ordinal);
        Assert.True(phaseBIdx >= 0, "Phase B WhenAny must exist");

        var secondWaitIdx = clean.IndexOf("Task.WhenAny(connectedTcs.Task, phaseBDelay)", phaseBIdx, StringComparison.Ordinal);
        Assert.True(secondWaitIdx > phaseBIdx, "Second wait on connected vs phaseBDelay must exist");

        // phaseBCts.Cancel() must be in finally AFTER the second wait
        var cancelIdx = clean.IndexOf("phaseBCts.Cancel()", secondWaitIdx, StringComparison.Ordinal);
        Assert.True(cancelIdx > secondWaitIdx, "phaseBCts.Cancel() must occur after second wait, not before");
    }

    [Fact]
    public void TwoPhaseStartCoordinator_PhaseA_CleanNoStartedWaitsUntilDeadline_SourceGuard()
    {
        var source = LoadSourceFile(Path.Combine("VPNRouter.App", "ViewModels", "Internals", "TwoPhaseStartCoordinator.cs"));
        Assert.NotNull(source);

        var clean = StripComments(source);

        var phaseAIdx = clean.IndexOf("Task.WhenAny(startedTcs.Task, connectedTcs.Task, startTask, phaseADelay)", StringComparison.Ordinal);
        Assert.True(phaseAIdx >= 0, "Phase A initial WhenAny must exist");

        // Clean completion branch must wait on secondAResult
        var secondWaitIdx = clean.IndexOf("Task.WhenAny(startedTcs.Task, connectedTcs.Task, phaseADelay)", phaseAIdx, StringComparison.Ordinal);
        Assert.True(secondWaitIdx > phaseAIdx, "Phase A clean-noStarted must enter second wait until deadline or started");
    }

    [Fact]
    public void SyncConnectedWithVpnRuntime_OwnedEngineReadinessGuardAndServicePath_SourceGuard()
    {
        var source = LoadSourceFile(Path.Combine("VPNRouter.App", "ViewModels", "MainWindowViewModel.RuntimeStatus.cs"));
        Assert.NotNull(source);

        var clean = StripComments(source);

        // Find SyncConnectedWithVpnRuntime method definition
        var methodIdx = clean.IndexOf("void SyncConnectedWithVpnRuntime(bool vpnRunning)", StringComparison.Ordinal);
        Assert.True(methodIdx >= 0, "SyncConnectedWithVpnRuntime method must exist");

        var methodBody = clean.Substring(methodIdx, Math.Min(2500, clean.Length - methodIdx));

        // 1. Locate the vpnRunning promotion branch
        var promoIdx = methodBody.IndexOf("if (vpnRunning &&", StringComparison.Ordinal);
        Assert.True(promoIdx >= 0, "Must inspect vpnRunning promotion branch");

        var promoBranch = methodBody.Substring(promoIdx);

        // 2. Narrow guard: check owned engine manager evidence (_engine.SingBoxPid != null OR _engine.IsRunning)
        Assert.Contains("_engine.SingBoxPid != null || _engine.IsRunning", promoBranch, StringComparison.Ordinal);

        // 3. If !IsConnected, return; process presence cannot establish owned readiness
        Assert.Contains("if (!IsConnected) return;", promoBranch, StringComparison.Ordinal);

        // 4. When IsConnected already true and owned engine alive, restore existing RestoreConnectedStatus (avoid relabel via service)
        Assert.Contains("RestoreConnectedStatus();", promoBranch, StringComparison.Ordinal);

        // 5. Must NOT call WindowsServiceHelper.IsRunning or new blocking service/lock queries every poll
        Assert.DoesNotContain("WindowsServiceHelper.IsRunning", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsServiceHelper", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceController", clean, StringComparison.Ordinal);

        // 6. Retain existing external service adoption path when no owned manager
        Assert.Contains("IsConnected = true;", promoBranch, StringComparison.Ordinal);
        Assert.Contains("ConnectButtonText = Strings.StopVPN;", promoBranch, StringComparison.Ordinal);
        Assert.Contains("Connected via service", promoBranch, StringComparison.Ordinal);
        Assert.Contains("MarkTrueSplitServiceManagedIfNeeded();", promoBranch, StringComparison.Ordinal);
    }
}
