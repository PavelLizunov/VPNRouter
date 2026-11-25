using System.ComponentModel;
using Avalonia.Threading;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels.Internals;

// All ownership/state transitions happen on the dispatcher. Network work never
// changes selection, writes settings, records health history or uploads telemetry.
internal sealed class BusinessServerHealthMonitor : IDisposable
{
    private readonly MainWindowViewModel _vm;
    private readonly Func<ServerViewModel, CancellationToken, Task<ServerProbeResult>>? _probe;
    private readonly BusinessServerChoiceViewModel[] _deepChoices;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private Round? _round;
    private bool _visible, _enabled, _disposed, _seededFromStartup;
    // A deep check spawns an independent sing-box outbound. It must not run
    // through, or contend with, an active or starting tunnel.
    private bool CanProbe => _deepChoices.Length > 0 && !_disposed && _visible && !_vm.IsConnecting &&
        !_vm.IsConnected && !_vm.IsBusinessRepairInProgress;

    internal IReadOnlyList<BusinessServerChoiceViewModel> Choices { get; }
    internal event Action? Updated;
    internal bool HasProbeTargets => _deepChoices.Length > 0;
    internal bool IsChecking => _enabled && _round != null;

    internal BusinessServerHealthMonitor(MainWindowViewModel vm,
        Func<ServerViewModel, CancellationToken, Task<ServerProbeResult>>? probe)
    {
        _vm = vm;
        _probe = probe;
        Choices = vm.GetBusinessServerChoices().Select(s => new BusinessServerChoiceViewModel(s)).ToArray();
        _deepChoices = Choices.Where(choice => choice.SupportsEndToEndProbe).ToArray();
        _vm.PropertyChanged += OnVmPropertyChanged;
        _timer.Tick += OnTick;
    }

    internal void SetVisible(bool visible)
    {
        Dispatcher.UIThread.VerifyAccess();
        _visible = visible;
        UpdateAvailability();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsConnecting) or nameof(MainWindowViewModel.IsConnected)
            or nameof(MainWindowViewModel.IsSplitTunnel) or nameof(MainWindowViewModel.IsBusinessRepairInProgress))
        {
            if (Dispatcher.UIThread.CheckAccess()) UpdateAvailability();
            else Dispatcher.UIThread.Post(UpdateAvailability);
        }
    }

    private void UpdateAvailability()
    {
        var enabled = CanProbe;
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (enabled)
        {
            _timer.Start();
            _ = RefreshAsync();
        }
        else
        {
            _timer.Stop();
            _round?.Cancellation.Cancel();
            foreach (var choice in _deepChoices) choice.Update(ServerProbeResult.Unknown);
            Updated?.Invoke();
        }
    }

    private void OnTick(object? sender, EventArgs e) => _ = RefreshAsync();

    internal Task RefreshAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_enabled || !CanProbe) return Task.CompletedTask;
        if (_round != null) return _round.Completion.Task;
        var round = new Round();
        _round = round;
        foreach (var choice in _deepChoices) choice.Update(ServerProbeResult.Unknown, checking: true);
        Updated?.Invoke();
        _ = RunRoundAsync(round);
        return round.Completion.Task;
    }

    private async Task RunRoundAsync(Round round)
    {
        try
        {
            // Serialize with the startup automatic check, then reuse its real
            // end-to-end results instead of spawning a duplicate probe wave.
            await _vm.WaitForBusinessAutoSelectionAsync(round.Cancellation.Token).ConfigureAwait(false);
            if (!_seededFromStartup)
            {
                _seededFromStartup = true;
                var seeded = _vm.GetBusinessAutoPickerResults();
                if (seeded is { Count: > 0 })
                {
                    var missing = new List<BusinessServerChoiceViewModel>();
                    foreach (var choice in _deepChoices)
                    {
                        if (seeded.TryGetValue(choice.Server.Name, out var result))
                        {
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                if (CanProbe && _enabled && !round.Cancellation.IsCancellationRequested)
                                {
                                    choice.Update(result);
                                    Updated?.Invoke();
                                }
                            });
                        }
                        else missing.Add(choice);
                    }
                    if (missing.Count == 0) return;
                    await Task.WhenAll(missing.Select(choice => ProbeOneAsync(choice, round)))
                        .ConfigureAwait(false);
                    return;
                }
            }

            await Task.WhenAll(_deepChoices.Select(choice => ProbeOneAsync(choice, round))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (round.Cancellation.IsCancellationRequested) { }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var cancelled = round.Cancellation.IsCancellationRequested;
                _round = null;
                round.Cancellation.Dispose();
                Updated?.Invoke();
                round.Completion.TrySetResult();
                // A disconnect/show can happen before a cancelled socket finishes.
                // Wait for that round, then refresh; never overlap old and new work.
                if (cancelled && _enabled && !_disposed) _ = RefreshAsync();
            });
        }
    }

    private async Task ProbeOneAsync(BusinessServerChoiceViewModel choice, Round round)
    {
        ServerProbeResult result;
        try
        {
            result = _probe != null
                ? await _probe(choice.Server, round.Cancellation.Token).ConfigureAwait(false)
                : await _vm.ProbeBusinessServerAsync(choice.Server, round.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (round.Cancellation.IsCancellationRequested) { return; }
        catch { result = new ServerProbeResult(ServerProbeStatus.Unreachable, 0, null); }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (CanProbe && _enabled && !round.Cancellation.IsCancellationRequested)
            {
                choice.Update(result);
                Updated?.Invoke();
            }
        });
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        UpdateAvailability();
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _timer.Tick -= OnTick;
    }

    private sealed class Round
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
