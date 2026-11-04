using System.ComponentModel;
using Avalonia.Threading;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels.Internals;

// All ownership/state transitions happen on the dispatcher. Network work never
// changes selection, writes settings, records health history or uploads telemetry.
internal sealed class BusinessServerHealthMonitor : IDisposable
{
    private readonly MainWindowViewModel _vm;
    private readonly Func<ServerViewModel, CancellationToken, Task<ServerProbeResult>> _probe;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private Round? _round;
    private bool _visible, _enabled, _disposed;
    private bool CanProbe => !_disposed && _visible && !_vm.IsConnecting && !_vm.IsConnected;

    internal IReadOnlyList<BusinessServerChoiceViewModel> Choices { get; }

    internal BusinessServerHealthMonitor(MainWindowViewModel vm,
        Func<ServerViewModel, CancellationToken, Task<ServerProbeResult>> probe)
    {
        _vm = vm;
        _probe = probe;
        Choices = vm.GetBusinessServerChoices().Select(s => new BusinessServerChoiceViewModel(s)).ToArray();
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
        if (e.PropertyName is nameof(MainWindowViewModel.IsConnecting) or nameof(MainWindowViewModel.IsConnected))
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
            foreach (var choice in Choices) choice.Update(ServerProbeResult.Unknown);
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
        foreach (var choice in Choices) choice.Update(ServerProbeResult.Unknown, checking: true);
        _ = RunRoundAsync(round);
        return round.Completion.Task;
    }

    private async Task RunRoundAsync(Round round)
    {
        try
        {
            await Task.WhenAll(Choices.Select(choice => ProbeOneAsync(choice, round))).ConfigureAwait(false);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var cancelled = round.Cancellation.IsCancellationRequested;
                _round = null;
                round.Cancellation.Dispose();
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
            result = await _probe(choice.Server, round.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (round.Cancellation.IsCancellationRequested) { return; }
        catch { result = new ServerProbeResult(ServerProbeStatus.Unreachable, 0, null); }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (CanProbe && _enabled && !round.Cancellation.IsCancellationRequested)
                choice.Update(result);
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
