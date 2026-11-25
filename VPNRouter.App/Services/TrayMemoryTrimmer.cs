using System;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using VPNRouter.App.Views;

namespace VPNRouter.App.Services;

internal sealed class TrayMemoryTrimmer : IDisposable
{
    internal static readonly TimeSpan DefaultTrimDelay = TimeSpan.FromSeconds(15);

    private readonly MainWindow _window;
    private readonly Action _onRestored;
    private readonly Action<int>? _onTrimCompleted;
    private readonly TimeSpan _trimDelay;
    private readonly Action _runTrimActions;
    private DispatcherTimer? _timer;
    private bool _wasHiddenOrMinimized;
    private bool _disposed;
    private int _gcInFlight;

    public TrayMemoryTrimmer(
        MainWindow window,
        Action onRestored,
        TimeSpan? trimDelay = null,
        Action? customTrimActions = null,
        Action<int>? onTrimCompleted = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _onRestored = onRestored ?? throw new ArgumentNullException(nameof(onRestored));
        _trimDelay = trimDelay ?? DefaultTrimDelay;
        _onTrimCompleted = onTrimCompleted;
        _runTrimActions = customTrimActions ?? ExecuteDefaultTrim;
        _wasHiddenOrMinimized = IsHiddenOrMinimized(window);

        _window.PropertyChanged += OnWindowPropertyChanged;
        _window.Closed += OnWindowClosed;

        if (_wasHiddenOrMinimized)
            ScheduleTrim();
    }

    internal bool HasPendingTrim => _timer is { IsEnabled: true };

    internal static bool IsHiddenOrMinimized(Window window) =>
        !window.IsVisible || window.WindowState == WindowState.Minimized;

    internal void ScheduleTrim()
    {
        if (_disposed) return;

        CancelPendingTrim();

        if (!IsHiddenOrMinimized(_window)) return;

        _wasHiddenOrMinimized = true;
        var timer = new DispatcherTimer { Interval = _trimDelay };
        timer.Tick += OnTrimTimerTick;
        _timer = timer;
        timer.Start();
    }

    internal void CancelPendingTrim()
    {
        var timer = _timer;
        _timer = null;
        if (timer != null)
        {
            timer.Tick -= OnTrimTimerTick;
            timer.Stop();
        }
    }

    internal void RunTrimIfStillHidden()
    {
        CancelPendingTrim();
        if (_disposed || !IsHiddenOrMinimized(_window))
            return;

        _runTrimActions();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.Property != Visual.IsVisibleProperty && e.Property != Window.WindowStateProperty)
            return;

        var isHidden = IsHiddenOrMinimized(_window);
        if (isHidden)
        {
            if (!_wasHiddenOrMinimized)
            {
                _wasHiddenOrMinimized = true;
                ScheduleTrim();
            }
        }
        else
        {
            var wasHidden = _wasHiddenOrMinimized;
            _wasHiddenOrMinimized = false;
            CancelPendingTrim();
            if (wasHidden)
            {
                try { _onRestored(); }
                catch { }
            }
        }
    }

    private void OnTrimTimerTick(object? sender, EventArgs e) => RunTrimIfStillHidden();

    private void ExecuteDefaultTrim()
    {
        var released = 0;
        try
        {
            released = _window.ReleaseInactivePages();
        }
        catch (Exception ex)
        {
            Serilog.Log.Logger?.Debug(ex, "[TrayTrim] ReleaseInactivePages failed");
        }

        try
        {
            SkiaSharp.SKGraphics.PurgeAllCaches();
        }
        catch { }

        if (Interlocked.CompareExchange(ref _gcInFlight, 1, 0) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    // CoreCLR requires blocking: true for GCCollectionMode.Aggressive; running on a
                    // background thread keeps the Avalonia UI thread non-blocking while allowing
                    // Gen2 + LOH compaction and segment decommit back to the OS.
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref _gcInFlight, 0);
                }
            });
        }

        Serilog.Log.Logger?.Debug("[TrayTrim] Hidden-window memory trim executed (releasedPages={Released})", released);
        _onTrimCompleted?.Invoke(released);
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelPendingTrim();
        _window.PropertyChanged -= OnWindowPropertyChanged;
        _window.Closed -= OnWindowClosed;
    }
}
