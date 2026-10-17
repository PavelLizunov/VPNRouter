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
    private static readonly TimeSpan ProblemScanInterval = TimeSpan.FromMinutes(3);

    private IReadOnlyList<DetectedProblem> _homeProblems = Array.Empty<DetectedProblem>();
    private readonly object _pathMtuLock = new();
    private HealthCheck.PathMtuProbeResult? _sessionPathMtuValue;

    private HealthCheck.PathMtuProbeResult? _sessionPathMtu
    {
        get { lock (_pathMtuLock) return _sessionPathMtuValue; }
        set { lock (_pathMtuLock) _sessionPathMtuValue = value; }
    }
    private CancellationTokenSource? _problemScanCts;
    private int _connectionGeneration;
    private ProblemFixSnapshot? _problemFixSnapshot;
    private bool _suppressRoutingAutoApply;

    /// <param name="ProfileAfterFix">The active profile the fix left; Undo restores <c>ActiveProfile</c> only while it is
    /// unchanged, so app selections edited after the fix survive.</param>
    private sealed record ProblemFixSnapshot(bool SplitTunnel, string RoutingMode, string ActiveProfile, int Mtu)
    {
        public string? ProfileAfterFix { get; init; }
    }

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
        Interlocked.Increment(ref _connectionGeneration);
        _problemScanCts?.Cancel();
        _problemScanCts = null;
        if (!connected)
        {
            // The TUN adapter is removed a few seconds after a stop; measure the network once it is gone.
            ScheduleMtuMeasurement(TimeSpan.FromSeconds(10));
            return;
        }
        if (IsFixingProblems) return;

        ProblemsDismissed = false;
        var cts = new CancellationTokenSource();
        _problemScanCts = cts;
        _ = ScanProblemsAfterDelayAsync(cts.Token);
    }

    private async Task ScanProblemsAfterDelayAsync(CancellationToken token)
    {
        try
        {
            // First look shortly after the connect, then again while connected: a mode change mid-session
            // restarts the core without a new connect.
            await Task.Delay(ProblemScanDelay, token).ConfigureAwait(false);
            while (true)
            {
                if (!IsFixingProblems)
                {
                    try { await ScanProblemsAsync(token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Warning(ex, "[Problems] scan failed");
                    }
                }
                await Task.Delay(ProblemScanInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.Warning(ex, "[Problems] scan failed"); }
    }

    private async Task ScanProblemsAsync(CancellationToken token)
    {
        var fullTunnel = !IsSplitTunnel;
        var mtu = TunMtu;
        var pathMtu = _sessionPathMtu;
        var problems = await Task.Run(() =>
        {
            var (json, log) = ReadProblemInputs();
            return ProblemDetector.Detect(fullTunnel, mtu, pathMtu, json, log);
        }, token).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (problems.Count > 0)
                _logger.Information("[Problems] detected: {Kinds}", string.Join(", ", problems.Select(p => p.Kind)));
            SetHomeProblems(problems);
        });
    }

    /// <summary>
    /// Measures the network path for the MTU check while the tunnel is down (at startup and after a disconnect).
    /// A ping with the tunnel up measures the tunnel itself, so such a result is never stored.
    /// </summary>
    private void ScheduleMtuMeasurement(TimeSpan delay)
    {
        if (!OperatingSystem.IsWindows()) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var generation = Volatile.Read(ref _connectionGeneration);
                await Task.Delay(delay).ConfigureAwait(false);
                if (IsConnected || IsConnecting) return;
                var probe = HealthCheck.ProbePathMtuPayload();
                // Any connect or disconnect during the pings may have put them through the tunnel: drop the result.
                if (probe.SkippedTunnelUp || IsConnected || IsConnecting
                    || generation != Volatile.Read(ref _connectionGeneration)) return;
                _sessionPathMtu = probe;
                _logger.Information("[Problems] MTU path measured: payload={Payload} blocked={Blocked}",
                    probe.BestPayload?.ToString() ?? "-", probe.PlainPingBlocked);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[Problems] MTU path measurement failed");
            }
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
            var status = plan.Reconnect ? CoreStrings.ProblemsFixed : CoreStrings.ProblemsFixedOffline;
            if (plan.Reconnect && !await ApplyPendingChangesInternalAsync(forceRestart: true))
            {
                if (plan.NewMtu == null)
                    throw new InvalidOperationException("tunnel restart after the fix failed");

                // The tunnel did not come back with the new MTU: keep the routing fix, return the old MTU.
                _logger.Warning("[Problems] restart with MTU {Mtu} failed, restoring MTU {Old}", plan.NewMtu, snapshot.Mtu);
                ApplyProblemSettings(null, snapshot.Mtu);
                if (!await ApplyPendingChangesInternalAsync(forceRestart: true))
                    throw new InvalidOperationException("tunnel restart with the previous MTU failed");
                status = CoreStrings.ProblemsMtuReverted;
            }

            _problemFixSnapshot = snapshot with { ProfileAfterFix = _settings.ActiveProfile ?? string.Empty };
            CanUndoProblemFix = true;
            SetHomeProblems(Array.Empty<DetectedProblem>());
            ProblemFixStatus = status;
            _logger.Information("[Problems] fixed: split={Split} mtu={Mtu} reconnect={Reconnect}",
                plan.SwitchToSplit, TunMtu, plan.Reconnect);
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

    public const string HelpVideosUrl = "https://vpn.ninitux.com/help/";

    public string L_ProblemsHowTo => CoreStrings.ProblemsHowTo;
    public string L_HelpVideosTitle => CoreStrings.HelpVideosTitle;
    public string L_HelpVideosSubtitle => CoreStrings.HelpVideosSubtitle;

    /// <summary>The help page opens in the app's language, scrolled to the guide of the given case.</summary>
    internal static string HelpVideoUrl(string? anchor, bool russian)
        => HelpVideosUrl + (russian ? string.Empty : "?lang=en") + (string.IsNullOrEmpty(anchor) ? string.Empty : "#" + anchor);

    internal static string HelpAnchor(DetectedProblemKind kind) => kind switch
    {
        DetectedProblemKind.GameTrafficThroughFullTunnel => "games",
        DetectedProblemKind.MtuAbovePath => "fix",
        _ => "fix"
    };

    [RelayCommand]
    private void OpenHelpVideos() => OpenUrl(HelpVideoUrl(null, IsRussian));

    [RelayCommand]
    private void OpenProblemHelp()
        => OpenUrl(HelpVideoUrl(_homeProblems.Count > 0 ? HelpAnchor(_homeProblems[0].Kind) : "fix", IsRussian));

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
            if (string.Equals(_settings.ActiveProfile ?? string.Empty, snapshot.ProfileAfterFix, StringComparison.Ordinal))
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
