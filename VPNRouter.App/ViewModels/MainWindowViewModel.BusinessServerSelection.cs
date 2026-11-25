using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.App.ViewModels.Internals;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

// Business-only automatic selection. It chooses a transient fastest verified
// node for Connect; a manual choice stays persisted and is never overwritten by
// background work.
public partial class MainWindowViewModel
{
    private static readonly TimeSpan BusinessAutoResultTtl = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _businessVerifyGate = new(4, 4);
    private readonly CancellationTokenSource _businessAutoLifetimeCts = new();
    private CancellationTokenSource? _businessAutoRunCts;
    private Task<BusinessAutoSelectOutcome?>? _businessAutoTask;
    private BusinessAutoSelectOutcome? _businessAutoCachedOutcome;
    private DateTimeOffset _businessAutoCachedAt;
    private int _businessAutoGeneration;
    private VlessDeepVerifier? _businessDeepVerifier;
    private BusinessAutoState _businessAutoState;
    private string? _businessAutoWinnerName;
    private int _businessAutoWinnerLatencyMs;

    private enum BusinessAutoState
    {
        Idle,
        Checking,
        Winner,
        None,
        Unavailable
    }

    private bool _businessAutoSelect;
    private bool _businessRepairInProgress;
    private string? _businessConnectedServerName;

    internal bool BusinessAutoSelect
    {
        get => _businessAutoSelect;
        set
        {
            if (SetProperty(ref _businessAutoSelect, value))
                OnBusinessAutoSelectChanged(value);
        }
    }

    internal string? BusinessActiveServerName
    {
        get
        {
            if (!IsConnected) return null;
            if (DisplayedTunnel is { } tunnel)
                return DisplayedServer(tunnel)?.Name is { } name && !string.IsNullOrWhiteSpace(name) ? name : null;
            return _displayExternalTunnel ? null : _businessConnectedServerName;
        }
    }

    internal void SetBusinessConnectedServerName(string? name)
    {
        var normalized = string.IsNullOrWhiteSpace(name) ? null : name;
        if (string.Equals(_businessConnectedServerName, normalized, StringComparison.Ordinal)) return;
        _businessConnectedServerName = normalized;
        OnPropertyChanged(nameof(BusinessActiveServerName));
    }

    internal bool IsBusinessAutoWinnerFresh =>
        _businessAutoState == BusinessAutoState.Winner &&
        _businessAutoWinnerName != null &&
        _businessAutoCachedOutcome?.Winner?.Name == _businessAutoWinnerName &&
        DateTimeOffset.UtcNow - _businessAutoCachedAt < BusinessAutoResultTtl;

    internal (string Name, int LatencyMs)? BusinessFreshAutoWinner =>
        IsBusinessAutoWinnerFresh && _businessAutoWinnerName != null
            ? (_businessAutoWinnerName, _businessAutoWinnerLatencyMs)
            : null;

    internal bool IsBusinessAutoChecking => _businessAutoState == BusinessAutoState.Checking;
    internal bool IsBusinessAutoProblem => _businessAutoState is BusinessAutoState.None or BusinessAutoState.Unavailable;
    internal bool IsBusinessServiceManaged => IsServiceManagedVpn;
    internal string? BusinessActiveRoutingMode => DisplayedTunnel?.RoutingMode;

    internal string BusinessAutoSelectStatusText => _businessAutoState switch
    {
        BusinessAutoState.Checking => Core.Localization.Strings.BusinessAutoSelecting,
        BusinessAutoState.Winner when IsBusinessAutoWinnerFresh && _businessAutoWinnerName != null =>
            Core.Localization.Strings.BusinessAutoFastest(
                _businessAutoWinnerName, _businessAutoWinnerLatencyMs),
        BusinessAutoState.Winner => Core.Localization.Strings.BusinessServerAutoStale,
        BusinessAutoState.None => Core.Localization.Strings.BusinessAutoNoneWorking,
        BusinessAutoState.Unavailable => Core.Localization.Strings.BusinessAutoCheckUnavailable,
        _ => string.Empty
    };

    internal bool HasBusinessAutoSelectStatus => _businessAutoState != BusinessAutoState.Idle;

    internal void SetBusinessAutoSelect(bool value) => BusinessAutoSelect = value;

    internal bool IsBusinessSelectionConfigured =>
        BusinessServerSelectionMode.IsConfigured(_settings.App.BusinessServerSelectionMode);

    internal bool IsBusinessAutomaticSelection =>
        BusinessServerSelectionMode.IsAutomatic(_settings.App.BusinessServerSelectionMode) &&
        GetBusinessServerChoices().Count > 0;

    // Test seam: replace the real sing-box end-to-end verifier with a deterministic one.
    internal Func<VlessServerEntry, IReadOnlyList<VlessServerEntry>, CancellationToken,
        Task<DeepVerifyResult>>? BusinessServerVerifier { get; set; }

    // Test seam: replace the normal engine start only for a captured Business
    // submission produced by automatic selection or the verified-connect hook.
    internal Func<AppSettings, CancellationToken, bool,
        Task<(Task StartTask, Internals.TwoPhaseStartOutcome Outcome)>>? BusinessConnectStart { get; set; }

    internal Func<CancellationToken, Task>? RefreshBusinessSubscriptionBeforeConnect { get; set; }
    internal bool IsBusinessSubscriptionPreflight { get; private set; }

    internal bool IsBusinessRepairInProgress
    {
        get => _businessRepairInProgress;
        private set => SetProperty(ref _businessRepairInProgress, value);
    }

    internal bool CanRefreshBusinessServers =>
        !_disposed && !_isLoadingUI && !_isReconnecting && !IsConnected &&
        (!IsConnecting || IsBusinessSubscriptionPreflight) && !IsApplying &&
        !UpdateVm.IsDownloading && !_engine.HasActiveTunnel && !HasExternalTunnelOwned();

    private async Task<bool> PrepareBusinessSubscriptionAsync(CancellationToken ct)
    {
        if (RefreshBusinessSubscriptionBeforeConnect is not { } refresh)
            return !ct.IsCancellationRequested;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _businessAutoLifetimeCts.Token);
        if (linked.IsCancellationRequested) return false;

        IsBusinessSubscriptionPreflight = true;
        IsConnecting = true;
        try
        {
            await refresh(linked.Token);
            return !linked.IsCancellationRequested && !_disposed &&
                RefreshBusinessSubscriptionBeforeConnect == refresh;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            IsBusinessSubscriptionPreflight = false;
            IsConnecting = false;
        }
    }

    private void OnBusinessAutoSelectChanged(bool value)
    {
        if (_isLoadingUI || !IsBusinessSelectionConfigured) return;

        _settings.App.BusinessServerSelectionMode = value
            ? BusinessServerSelectionMode.Automatic
            : BusinessServerSelectionMode.Manual;
        try { SaveSettings(); }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Business] Automatic selection preference save failed");
            StatusText = Core.Localization.Strings.BusinessServerSaveFailed;
        }

        CancelBusinessAutoSelection();
        if (value) StartBusinessAutoSelectionIfConfigured();
    }

    // A manual pick is deliberate: persist it and leave automatic mode.
    internal void MarkBusinessSelectionManual()
    {
        var wasLoading = _isLoadingUI;
        _isLoadingUI = true;
        try { BusinessAutoSelect = false; }
        finally { _isLoadingUI = wasLoading; }
        CancelBusinessAutoSelection();
    }

    internal void StartBusinessAutoSelectionIfConfigured()
    {
        if (_disposed || !IsBusinessAutomaticSelection) return;
        if (IsConnected || IsConnecting || IsApplying || _isReconnecting || _engine.HasActiveTunnel) return;
        _ = RunBusinessAutoSelectionAsync(forceFresh: true, cacheWinner: true,
            updateStatus: true, _businessAutoLifetimeCts.Token);
    }

    internal void OnBusinessServersReplaced()
    {
        CancelBusinessAutoSelection();
        StartBusinessAutoSelectionIfConfigured();
    }

    // The picker uses the startup run instead of launching a duplicate wave of
    // sing-box processes. It falls back to its own checks if no startup result
    // exists or when the user asks for a refresh.
    internal async Task WaitForBusinessAutoSelectionAsync(CancellationToken ct)
    {
        var task = _businessAutoTask;
        if (task == null) return;
        try { await task.WaitAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    internal IReadOnlyDictionary<string, ServerProbeResult>? GetBusinessAutoPickerResults()
    {
        var outcome = _businessAutoCachedOutcome;
        if (outcome == null ||
            DateTimeOffset.UtcNow - _businessAutoCachedAt >= BusinessAutoResultTtl)
            return null;
        return outcome.Probes
            .GroupBy(probe => probe.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => BusinessServerAutoSelection.ToProbeResult(group.First().Result),
                StringComparer.OrdinalIgnoreCase);
    }

    internal async Task<ServerProbeResult> ProbeBusinessServerAsync(
        ServerViewModel server, CancellationToken ct)
    {
        var pool = _settings.Vless.GetEffectiveServers();
        var entry = server.SourceEntry;
        var cached = _businessAutoCachedOutcome;
        var checksWinner = cached?.Winner is { } winner && SubscriptionEntry.RowKey(winner) == SubscriptionEntry.RowKey(entry);
        var generation = Volatile.Read(ref _businessAutoGeneration);
        var result = await VerifyBusinessEntryAsync(entry, pool, ct, runCanaries: false)
            .ConfigureAwait(false);
        var probeResult = BusinessServerAutoSelection.ToProbeResult(result);

        // A current picker failure for the exact cached winner invalidates the
        // startup result before the next ordinary Connect. A stale/cancelled
        // probe, a replaced selection outcome, or another profile never does.
        if (checksWinner && !result.Ok && result.FailurePhase != DeepVerifyFailurePhase.Cancelled)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || ct.IsCancellationRequested) return;
                if (generation != Volatile.Read(ref _businessAutoGeneration)) return;
                if (!ReferenceEquals(_businessAutoCachedOutcome, cached)) return;
                if (!IsBusinessAutomaticSelection) return;
                InvalidateBusinessAutoOutcome();
            });
        }

        return probeResult;
    }

    internal async Task<bool> ConnectVerifiedBusinessServerAsync(CancellationToken ct)
    {
        if (IsBusinessRepairInProgress || !CanStartVerifiedBusinessConnect()) return false;
        IsBusinessRepairInProgress = true;
        try
        {
            return await PrepareBusinessSubscriptionAsync(ct) &&
                await ConnectVerifiedBusinessServerCoreAsync(ct);
        }
        finally { IsBusinessRepairInProgress = false; }
    }

    private async Task<bool> ConnectVerifiedBusinessServerCoreAsync(CancellationToken ct)
    {
        if (!CanStartVerifiedBusinessConnect()) return false;

        var selectedBefore = SelectedServer;
        var generation = Volatile.Read(ref _connectionGeneration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, _businessAutoLifetimeCts.Token);
        void OnStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SelectedServer) or nameof(IsConnecting)
                or nameof(IsConnected) or nameof(IsApplying) or nameof(BusinessAutoSelect))
                linked.Cancel();
        }

        PropertyChanged += OnStateChanged;
        try
        {
            var outcome = await RunBusinessAutoSelectionAsync(
                forceFresh: true, cacheWinner: false, updateStatus: true, linked.Token);
            if (outcome?.Winner is not { } winner)
            {
                if (!linked.IsCancellationRequested && !ct.IsCancellationRequested)
                    StatusText = HasBusinessAutoSelectStatus
                        ? BusinessAutoSelectStatusText
                        : Core.Localization.Strings.BusinessAutoNoneWorking;
                return false;
            }

            if (!CanStartVerifiedBusinessConnect() ||
                generation != Volatile.Read(ref _connectionGeneration) ||
                !ReferenceEquals(SelectedServer, selectedBefore))
                return false;

            var submitted = CapturePinnedBusinessSubmission(winner);
            if (submitted == null)
            {
                SetBusinessAutoState(BusinessAutoState.None);
                return false;
            }

            // From here the normal connection owns state transitions; our own
            // watcher must not cancel the start it is waiting for.
            PropertyChanged -= OnStateChanged;
            _pinnedStartOwnedByUs = false;
            await ToggleConnectionCoreAsync(submitted, useSimpleStart: false, linked.Token);
            // Success means this call started and confirmed its own tunnel; an
            // adopted service-owned tunnel is not a successful repair of B.
            return _pinnedStartOwnedByUs && IsConnected && !IsConnecting;
        }
        catch (OperationCanceledException) { return false; }
        finally { PropertyChanged -= OnStateChanged; }
    }

    private bool CanStartVerifiedBusinessConnect() =>
        !_disposed && IsBusinessSelectionConfigured && !IsConnected && !IsConnecting &&
        !IsApplying && !_isReconnecting && !_engine.HasActiveTunnel &&
        !HasExternalTunnelOwned();

    private async Task<AppSettings?> ResolveBusinessAutomaticSubmissionAsync(CancellationToken ct)
    {
        if (!IsBusinessAutomaticSelection) return null;
        var outcome = await GetBusinessAutoOutcomeAsync(forceFresh: false, ct);
        if (!IsBusinessAutomaticSelection || IsConnected) return null;
        return outcome?.Winner is { } winner ? CapturePinnedBusinessSubmission(winner) : null;
    }

    private async Task<BusinessAutoSelectOutcome?> GetBusinessAutoOutcomeAsync(
        bool forceFresh, CancellationToken ct)
    {
        if (!forceFresh && _businessAutoCachedOutcome is { HasWinner: true } cached &&
            DateTimeOffset.UtcNow - _businessAutoCachedAt < BusinessAutoResultTtl)
            return cached;

        if (!forceFresh && _businessAutoTask is { IsCompleted: false } running)
        {
            try { return await running.WaitAsync(ct); }
            catch (OperationCanceledException) { return null; }
        }

        return await RunBusinessAutoSelectionAsync(
            forceFresh, cacheWinner: true, updateStatus: true, ct);
    }

    private async Task<BusinessAutoSelectOutcome?> RunBusinessAutoSelectionAsync(
        bool forceFresh, bool cacheWinner, bool updateStatus, CancellationToken ct)
    {
        if (_disposed || !IsBusinessSelectionConfigured) return null;
        if (!forceFresh && _businessAutoCachedOutcome is { HasWinner: true } cached &&
            DateTimeOffset.UtcNow - _businessAutoCachedAt < BusinessAutoResultTtl)
            return cached;

        if (forceFresh) CancelBusinessAutoSelectionCore();

        var generation = Interlocked.Increment(ref _businessAutoGeneration);
        var pool = SnapshotBusinessVerificationPool();
        var candidates = GetBusinessAutoCandidates(pool);
        if (candidates.Count == 0)
        {
            if (updateStatus) SetBusinessAutoState(BusinessAutoState.Unavailable);
            return null;
        }

        if (updateStatus) SetBusinessAutoState(BusinessAutoState.Checking);

        var runCts = CancellationTokenSource.CreateLinkedTokenSource(
            _businessAutoLifetimeCts.Token, ct);
        var previous = Interlocked.Exchange(ref _businessAutoRunCts, runCts);
        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { }

        var task = ExecuteBusinessAutoSelectionAsync(
            pool, candidates, generation, cacheWinner, updateStatus, runCts);
        if (cacheWinner) _businessAutoTask = task;
        try { return await task; }
        finally
        {
            if (cacheWinner && ReferenceEquals(_businessAutoTask, task))
                _businessAutoTask = null;
        }
    }

    private async Task<BusinessAutoSelectOutcome?> ExecuteBusinessAutoSelectionAsync(
        IReadOnlyList<VlessServerEntry> pool,
        IReadOnlyList<VlessServerEntry> candidates,
        int generation,
        bool cacheWinner,
        bool updateStatus,
        CancellationTokenSource runCts)
    {
        var token = runCts.Token;
        try
        {
            var outcome = await BusinessServerAutoSelection.SelectFastestAsync(
                candidates,
                (entry, ct) => VerifyBusinessEntryAsync(entry, pool, ct, runCanaries: false),
                token,
                BusinessServerAutoSelection.DefaultMaxConcurrency,
                BusinessServerAutoSelection.DefaultBudget);

            token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _businessAutoGeneration) || _disposed)
                return null;

            if (cacheWinner && IsBusinessAutomaticSelection)
            {
                // Keep partial/no-winner results for the picker. Connect only
                // reuses a cached outcome when it has a verified winner.
                _businessAutoCachedOutcome = outcome;
                _businessAutoCachedAt = DateTimeOffset.UtcNow;
            }

            if (updateStatus)
            {
                if (IsBusinessAutomaticSelection || outcome.HasWinner == false)
                    SetBusinessAutoStateFromOutcome(outcome);
                else
                    SetBusinessAutoState(BusinessAutoState.Idle);
            }
            return outcome;
        }
        catch (OperationCanceledException)
        {
            if (generation == Volatile.Read(ref _businessAutoGeneration) && updateStatus && !_disposed)
                SetBusinessAutoState(BusinessAutoState.Idle);
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Business] End-to-end automatic selection failed");
            if (generation == Volatile.Read(ref _businessAutoGeneration) && updateStatus && !_disposed)
                SetBusinessAutoState(BusinessAutoState.Unavailable);
            return null;
        }
        finally
        {
            Interlocked.CompareExchange(ref _businessAutoRunCts, null, runCts);
            runCts.Dispose();
        }
    }

    private async Task<DeepVerifyResult> VerifyBusinessEntryAsync(
        VlessServerEntry entry,
        IReadOnlyList<VlessServerEntry> pool,
        CancellationToken ct,
        bool runCanaries)
    {
        await _businessVerifyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (BusinessServerVerifier is { } verifier)
                return await verifier(entry, pool, ct).ConfigureAwait(false);

            _businessDeepVerifier ??= new VlessDeepVerifier(_logger);
            if (!_businessDeepVerifier.IsAvailable)
                return DeepVerifyResult.Failed(
                    "sing-box runtime is unavailable", DeepVerifyFailurePhase.LocalSpawn);

            return await _businessDeepVerifier.VerifyAsync(
                entry, pool, measureBandwidth: false, ct, runCanaries).ConfigureAwait(false);
        }
        finally
        {
            try { _businessVerifyGate.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    internal static List<VlessServerEntry> GetBusinessAutoCandidates(
        IReadOnlyList<VlessServerEntry> pool)
    {
        // Chain targets stay out of the Business automatic pool: the Business
        // portal policy applies to BusinessDefaults.GetServerChoices, which
        // excludes DetourVia, so selecting one would produce a config the
        // policy path refuses. The verifier still supports an explicit chain
        // through VerifyAsync(entry, pool, ...).
        return BusinessDefaults.GetServerChoices(pool)
            .Where(BusinessServerAutoSelection.IsVerifiableCandidate)
            .DistinctBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<VlessServerEntry> SnapshotBusinessVerificationPool() =>
        VpnEngine.CopySessionSettings(_settings).Vless.GetEffectiveServers()
            .Where(server => !string.IsNullOrWhiteSpace(server.Server) &&
                !string.Equals(server.Server, "your.server.com", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private AppSettings? CapturePinnedBusinessSubmission(VlessServerEntry winner)
    {
        try
        {
            // The name must still resolve to exactly one current, verifiable
            // profile. Never substitute a same-labelled or inferred node.
            var currentPool = _settings.Vless.GetEffectiveServers();
            var current = currentPool.Where(server =>
                string.Equals(server.Name, winner.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (current.Count != 1 ||
                !BusinessServerAutoSelection.IsVerifiableCandidate(current[0]) ||
                (!string.IsNullOrEmpty(current[0].DetourVia) &&
                 VlessDeepVerifier.ResolveChainUpstream(current[0], currentPool) == null))
                return null;

            var submitted = CaptureBusinessConnectionSettings();
            var matches = submitted.Vless.Servers.Where(server =>
                string.Equals(server.Name, winner.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1 || SubscriptionEntry.RowKey(matches[0]) != SubscriptionEntry.RowKey(winner))
                return null;

            var selected = matches[0];
            if (!string.IsNullOrEmpty(selected.DetourVia))
            {
                // Business portal policy resolves only GetServerChoices, which
                // excludes a DetourVia target. Never pin a route that the policy
                // config would refuse.
                return null;
            }

            submitted.Vless.ActiveServer = selected.Name;
            // GetActiveServers otherwise groups same-host VLESS siblings and the
            // generator may pick an unverified flow sibling. This flag is set
            // only on the private connection copy; the saved row is untouched.
            selected.UseAsSingleOutbound = true;
            // This session uses the exact verified node, not a runtime URL test
            // that could substitute an unverified candidate.
            submitted.Vless.AutoSelectBestServer = false;
            submitted.Vless.IsTransientBusinessSelection = true;
            return submitted;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Business] Could not capture the verified connection settings");
            return null;
        }
    }

    private void SetBusinessAutoStateFromOutcome(BusinessAutoSelectOutcome? outcome)
    {
        if (outcome == null) { SetBusinessAutoState(BusinessAutoState.Idle); return; }
        if (outcome.Winner is { } winner)
        {
            SetBusinessAutoState(BusinessAutoState.Winner, winner.Name, outcome.WinnerLatencyMs);
            return;
        }

        var unavailable = outcome.Probes.Count > 0 && outcome.Probes.All(probe =>
            probe.Result.FailurePhase is DeepVerifyFailurePhase.LocalSpawn
                or DeepVerifyFailurePhase.UnsupportedByVerifier);
        SetBusinessAutoState(unavailable ? BusinessAutoState.Unavailable : BusinessAutoState.None);
    }

    private void SetBusinessAutoState(
        BusinessAutoState state, string? winnerName = null, int latencyMs = 0)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _businessAutoState = state;
            _businessAutoWinnerName = winnerName;
            _businessAutoWinnerLatencyMs = latencyMs;
            OnPropertyChanged(nameof(BusinessAutoSelectStatusText));
            OnPropertyChanged(nameof(HasBusinessAutoSelectStatus));
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            SetBusinessAutoState(state, winnerName, latencyMs);
        });
    }

    private void CancelBusinessAutoSelection()
    {
        CancelBusinessAutoSelectionCore();
        SetBusinessAutoState(BusinessAutoState.Idle);
    }

    internal void InvalidateBusinessAutoOutcome()
    {
        _businessAutoCachedOutcome = null;
        _businessAutoCachedAt = default;
        if (IsBusinessAutomaticSelection)
            SetBusinessAutoState(BusinessAutoState.Idle);
    }

    private void CancelBusinessAutoSelectionCore()
    {
        Interlocked.Increment(ref _businessAutoGeneration);
        try { Interlocked.Exchange(ref _businessAutoRunCts, null)?.Cancel(); }
        catch (ObjectDisposedException) { }
        _businessAutoCachedOutcome = null;
    }

    private void DisposeBusinessAutoSelection()
    {
        try { _businessAutoLifetimeCts.Cancel(); } catch (ObjectDisposedException) { }
        try { _businessAutoRunCts?.Cancel(); } catch (ObjectDisposedException) { }
        // The lifetime source and semaphore stay undisposed: an in-flight probe
        // may still hold a registration or release its permit during shutdown.
    }
}
