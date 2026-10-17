using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.Diagnostics;
using CoreStrings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.ViewModels;

public sealed record HomeProblemItem(string Title, string Body);

/// <summary>
/// The problems card of the home screen: detected problems in plain words and one Fix button with Undo.
/// Detection runs in the background after a connect; the fix restarts the tunnel once through the normal apply path.
/// </summary>
public partial class MainWindowViewModel
{
    private static readonly TimeSpan ProblemScanDelay = TimeSpan.FromSeconds(40);

    private IReadOnlyList<DetectedProblem> _homeProblems = Array.Empty<DetectedProblem>();
    private HealthCheck.PathMtuProbeResult? _sessionPathMtu;
    private CancellationTokenSource? _problemScanCts;
    private ProblemFixSnapshot? _problemFixSnapshot;
    private bool _suppressRoutingAutoApply;

    private sealed record ProblemFixSnapshot(bool SplitTunnel, string RoutingMode, string ActiveProfile, int Mtu);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProblemsCardVisible))]
    private bool _problemsDismissed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProblemsCardVisible))]
    [NotifyPropertyChangedFor(nameof(HasProblemFixStatus))]
    private string _problemFixStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFixProblems))]
    private bool _isFixingProblems;

    [ObservableProperty] private bool _canUndoProblemFix;

    public IReadOnlyList<HomeProblemItem> HomeProblemItems => _homeProblems.Select(ToItem).ToList();
    public bool HasHomeProblems => _homeProblems.Count > 0;
    public bool IsProblemsCardVisible => !ProblemsDismissed && (HasHomeProblems || HasProblemFixStatus);
    public bool HasProblemFixStatus => ProblemFixStatus.Length > 0;
    public bool CanFixProblems => HasHomeProblems && !IsFixingProblems;
    public string ProblemsTitleText => CoreStrings.ProblemsTitle(_homeProblems.Count);
    public string L_ProblemsFix => CoreStrings.ProblemsFix;
    public string L_ProblemsHide => CoreStrings.ProblemsHide;
    public string L_ProblemsUndo => CoreStrings.ProblemsUndo;

    private static HomeProblemItem ToItem(DetectedProblem problem) => problem.Kind switch
    {
        DetectedProblemKind.GameTrafficThroughFullTunnel =>
            new(CoreStrings.ProblemGameTrafficTitle, CoreStrings.ProblemGameTrafficBody),
        DetectedProblemKind.MtuAbovePath =>
            new(CoreStrings.ProblemMtuTitle, CoreStrings.ProblemMtuBody(problem.SuggestedMtu ?? TunSettings.DefaultMtu)),
        _ => new(CoreStrings.ProblemTimeoutsTitle, CoreStrings.ProblemTimeoutsBody)
    };

    private void SetHomeProblems(IReadOnlyList<DetectedProblem> problems)
    {
        _homeProblems = problems;
        OnPropertyChanged(nameof(HomeProblemItems));
        OnPropertyChanged(nameof(HasHomeProblems));
        OnPropertyChanged(nameof(IsProblemsCardVisible));
        OnPropertyChanged(nameof(CanFixProblems));
        OnPropertyChanged(nameof(ProblemsTitleText));
    }

    /// <summary>Called from <c>OnIsConnectedChanged</c>: a new connect starts a fresh delayed scan.</summary>
    private void OnConnectedForProblems(bool connected)
    {
        _problemScanCts?.Cancel();
        _problemScanCts = null;
        if (!connected || IsFixingProblems) return;

        ProblemsDismissed = false;
        var cts = new CancellationTokenSource();
        _problemScanCts = cts;
        _ = ScanProblemsAfterDelayAsync(cts.Token);
    }

    private async Task ScanProblemsAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(ProblemScanDelay, token).ConfigureAwait(false);
            await ScanProblemsAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.Warning(ex, "[Problems] scan failed"); }
    }

    private async Task ScanProblemsAsync(CancellationToken token)
    {
        var fullTunnel = !IsSplitTunnel;
        var mtu = TunMtu;
        var problems = await Task.Run(() =>
        {
            if (OperatingSystem.IsWindows() && _sessionPathMtu == null)
                _sessionPathMtu = HealthCheck.ProbePathMtuPayload();
            var (json, log) = ReadProblemInputs();
            return ProblemDetector.Detect(fullTunnel, mtu, _sessionPathMtu, json, log);
        }, token).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (problems.Count > 0)
                _logger.Information("[Problems] detected: {Kinds}", string.Join(", ", problems.Select(p => p.Kind)));
            SetHomeProblems(problems);
        });
    }

    private static (string? Json, string? Log) ReadProblemInputs()
    {
        string? json = null, log = null;
        try
        {
            if (File.Exists(AppPaths.CurrentConfigPath))
                json = File.ReadAllText(AppPaths.CurrentConfigPath);
            if (File.Exists(AppPaths.SingBoxLogPath))
                log = DiagnosticsExporter.TailLines(AppPaths.SingBoxLogPath, 4_000);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return (json, log);
    }

    [RelayCommand]
    private async Task FixProblemsAsync()
    {
        if (!CanFixProblems) return;
        var plan = ProblemDetector.PlanFix(_homeProblems, IsConnected && !IsConnecting);

        IsFixingProblems = true;
        ProblemFixStatus = CoreStrings.ProblemsFixing;
        _problemScanCts?.Cancel();
        var snapshot = new ProblemFixSnapshot(IsSplitTunnel, _settings.App.RoutingMode ?? string.Empty,
            _settings.ActiveProfile ?? string.Empty, TunMtu);
        try
        {
            ApplyProblemSettings(plan.SwitchToSplit ? true : null, plan.NewMtu);
            if (plan.Reconnect)
                await ApplyPendingChangesInternalAsync(forceRestart: true);

            _problemFixSnapshot = snapshot;
            CanUndoProblemFix = true;
            SetHomeProblems(Array.Empty<DetectedProblem>());
            ProblemFixStatus = plan.Reconnect ? CoreStrings.ProblemsFixed : CoreStrings.ProblemsFixedOffline;
            _logger.Information("[Problems] fixed: split={Split} mtu={Mtu} reconnect={Reconnect}",
                plan.SwitchToSplit, plan.NewMtu?.ToString() ?? "-", plan.Reconnect);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[Problems] fix failed");
            ProblemFixStatus = CoreStrings.ProblemsFixFailed;
        }
        finally
        {
            IsFixingProblems = false;
        }
    }

    [RelayCommand]
    private async Task UndoProblemFixAsync()
    {
        if (_problemFixSnapshot is not { } snapshot || IsFixingProblems) return;

        IsFixingProblems = true;
        try
        {
            RestoreProblemSnapshot(snapshot);
            if (IsConnected && !IsConnecting)
                await ApplyPendingChangesInternalAsync(forceRestart: true);
            _problemFixSnapshot = null;
            CanUndoProblemFix = false;
            ProblemFixStatus = CoreStrings.ProblemsUndone;
            _logger.Information("[Problems] fix undone");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[Problems] undo failed");
            ProblemFixStatus = CoreStrings.ProblemsFixFailed;
        }
        finally
        {
            IsFixingProblems = false;
        }
    }

    [RelayCommand]
    private void HideProblems()
    {
        ProblemsDismissed = true;
        ProblemFixStatus = string.Empty;
    }

    /// <summary>Saves the new routing mode and MTU without the setter-triggered restart; the caller restarts once.</summary>
    private void ApplyProblemSettings(bool? splitTunnel, int? mtu)
    {
        _suppressRoutingAutoApply = true;
        try
        {
            if (mtu is { } newMtu && newMtu != TunMtu)
                TunMtu = newMtu;
            if (splitTunnel is { } split && IsSplitTunnel != split)
                IsSplitTunnel = split;
            SaveSettings();
        }
        finally
        {
            _suppressRoutingAutoApply = false;
        }
    }

    private void RestoreProblemSnapshot(ProblemFixSnapshot snapshot)
    {
        _suppressRoutingAutoApply = true;
        try
        {
            if (TunMtu != snapshot.Mtu)
                TunMtu = snapshot.Mtu;
            if (IsSplitTunnel != snapshot.SplitTunnel)
                IsSplitTunnel = snapshot.SplitTunnel;
            _settings.App.RoutingMode = snapshot.RoutingMode;
            _settings.ActiveProfile = snapshot.ActiveProfile;
            _settingsStore.Save(_settings, AppPaths.ConfigYamlPath);
            MarkRoutingSettingsChanged();
        }
        finally
        {
            _suppressRoutingAutoApply = false;
        }
    }
}
