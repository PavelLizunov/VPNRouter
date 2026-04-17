using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VPNRouter.App.Localization;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.App.ViewModels.FreeConfigs;

/// <summary>
/// ViewModel for the "Free Configs" page.
/// Owns the aggregator, the displayed list, filters, and the Apply command.
/// </summary>
public partial class FreeConfigsPageViewModel : ObservableObject
{
    private readonly FreeConfigAggregator _aggregator;
    private readonly FreeConfigDeepVerifier _deepVerifier;
    private readonly ILogger _logger;
    private readonly Func<FreeConfigEntry, Task<bool>> _applyAsync;

    private List<FreeConfigEntry> _allConfigs = new();
    private CancellationTokenSource? _refreshCts;

    public FreeConfigsPageViewModel(ILogger logger, Func<FreeConfigEntry, Task<bool>> applyAsync)
    {
        _logger = logger;
        _applyAsync = applyAsync;
        _aggregator = new FreeConfigAggregator(logger);
        _aggregator.OnStageChanged += OnAggregatorStage;
        _aggregator.OnTestProgress  += OnAggregatorProgress;
        _deepVerifier = new FreeConfigDeepVerifier(logger);

        // Load cached snapshot if exists.
        var file = _aggregator.Cache.Load();
        _allConfigs = file.Configs;
        ApplyFiltersAndStats();

        if (file.LastAggregatedAt == DateTime.MinValue)
        {
            StatusText = Strings.FcStatusEmpty;
        }
        else
        {
            var age = DateTime.UtcNow - file.LastAggregatedAt;
            StatusText = Strings.FcStatusCacheAge(FormatAge(age));
        }
    }

    [ObservableProperty] private ObservableCollection<FreeConfigItemViewModel> _displayedConfigs = new();
    [ObservableProperty] private ObservableCollection<string> _countries = new();

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _workingCount;
    [ObservableProperty] private int _verifiedCount;
    [ObservableProperty] private int _tlsFailedCount;
    [ObservableProperty] private int _implausibleCount;
    [ObservableProperty] private int _timeoutCount;
    [ObservableProperty] private int _unreachableCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private FreeConfigItemViewModel? _selectedItem;

    public bool HasSelection => SelectedItem != null;

    [ObservableProperty] private string _selectedCountry = "All";
    partial void OnSelectedCountryChanged(string value) => ApplyFiltersAndStats();

    [ObservableProperty] private bool _onlyWorking = true;
    partial void OnOnlyWorkingChanged(bool value) => ApplyFiltersAndStats();

    /// <summary>True when no configs have been aggregated yet (cache is empty).</summary>
    public bool IsEmpty => _allConfigs.Count == 0;
    /// <summary>True when filters hide everything but cache isn't empty.</summary>
    public bool IsFilteredEmpty => _allConfigs.Count > 0 && DisplayedConfigs.Count == 0;
    /// <summary>True when there is data to show in the list (not empty and not filtered out).</summary>
    public bool IsListVisible => !IsEmpty && !IsFilteredEmpty;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _progressDone;
    [ObservableProperty] private int _progressTotal;
    public bool HasProgress => ProgressTotal > 0;
    partial void OnProgressTotalChanged(int value) => OnPropertyChanged(nameof(HasProgress));

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _refreshCts = new CancellationTokenSource();
        try
        {
            var fresh = await Task.Run(() => _aggregator.RefreshAsync(ct: _refreshCts.Token));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allConfigs = fresh;
                ApplyFiltersAndStats();
                StatusText = Strings.FcStatusRefreshed(fresh.Count);
            });
        }
        catch (OperationCanceledException)
        {
            StatusText = Strings.FcStatusCancelled;
        }
        catch (Exception ex)
        {
            _logger.Warning("FreeConfigs refresh failed: {err}", ex.Message);
            StatusText = Strings.FcStatusFailed(ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressTotal = 0;
            ProgressDone = 0;
        }
    }

    [RelayCommand]
    private async Task RetestAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _refreshCts = new CancellationTokenSource();
        try
        {
            var fresh = await Task.Run(() => _aggregator.RetestAsync(_refreshCts.Token));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allConfigs = fresh;
                ApplyFiltersAndStats();
                StatusText = Strings.FcStatusTested(fresh.Count);
            });
        }
        catch (OperationCanceledException)
        {
            StatusText = Strings.FcStatusCancelled;
        }
        catch (Exception ex)
        {
            _logger.Warning("FreeConfigs retest failed: {err}", ex.Message);
            StatusText = Strings.FcStatusFailed(ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressTotal = 0;
            ProgressDone = 0;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _refreshCts?.Cancel();
    }

    /// <summary>
    /// Deep-verify the top-N candidates (by latency among TCP+TLS-OK entries) using real sing-box + HTTP.
    /// This is the only test that proves a config actually carries traffic.
    /// </summary>
    [RelayCommand]
    private async Task DeepVerifyTopAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _refreshCts = new CancellationTokenSource();
        try
        {
            // Pick top-30 candidates: prefer Ok/Slow/Verified (TCP+TLS OK), then Implausible
            // (might be real but measurement was wrong via local intercept).
            var candidates = _allConfigs
                .Where(c => c.Status == FreeConfigStatus.Ok
                         || c.Status == FreeConfigStatus.Slow
                         || c.Status == FreeConfigStatus.Verified
                         || c.Status == FreeConfigStatus.Implausible)
                .OrderBy(c => c.Status == FreeConfigStatus.Verified ? 0
                            : c.Status == FreeConfigStatus.Ok       ? 1
                            : c.Status == FreeConfigStatus.Slow     ? 2
                                                                     : 3)
                .ThenBy(c => c.LatencyMs > 0 ? c.LatencyMs : int.MaxValue)
                .Take(30)
                .ToList();

            if (candidates.Count == 0)
            {
                StatusText = Strings.FcStatusNoDeepCandidates;
                return;
            }

            StatusText = Strings.FcStatusDeepVerifyStart(candidates.Count);
            ProgressTotal = candidates.Count;
            ProgressDone = 0;

            var progress = new Progress<(int done, int total)>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ProgressDone = p.done;
                    ProgressTotal = p.total;
                });
            });

            await Task.Run(() => _deepVerifier.VerifyBatchAsync(candidates, progress, _refreshCts.Token));

            // Save cache after deep verification (entries mutated in place).
            var cacheFile = _aggregator.Cache.Load();
            cacheFile.Configs = _allConfigs;
            _aggregator.Cache.Save(cacheFile);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyFiltersAndStats();
                StatusText = Strings.FcStatusDeepVerifyDone(VerifiedCount);
            });
        }
        catch (OperationCanceledException)
        {
            StatusText = Strings.FcStatusCancelled;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "DeepVerify failed");
            StatusText = Strings.FcStatusFailed(ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressTotal = 0;
            ProgressDone = 0;
        }
    }

    [RelayCommand]
    private async Task ApplySelectedAsync()
    {
        var sel = SelectedItem;
        if (sel == null) return;
        if (IsBusy) return;

        IsBusy = true;
        StatusText = Strings.FcStatusApplying(sel.Endpoint);
        try
        {
            var ok = await _applyAsync(sel.Entry);
            StatusText = ok
                ? Strings.FcStatusApplied(sel.Endpoint)
                : Strings.FcStatusApplyFailed;
        }
        catch (Exception ex)
        {
            _logger.Warning("FreeConfigs apply failed: {err}", ex.Message);
            StatusText = Strings.FcStatusFailed(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnAggregatorStage(string stage)
    {
        Dispatcher.UIThread.Post(() => StatusText = stage);
    }

    private void OnAggregatorProgress(int done, int total)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ProgressDone = done;
            ProgressTotal = total;
        });
    }

    private void ApplyFiltersAndStats()
    {
        try
        {
            TotalCount       = _allConfigs.Count;
            WorkingCount     = _allConfigs.Count(c => c.Status == FreeConfigStatus.Ok);
            VerifiedCount    = _allConfigs.Count(c => c.Status == FreeConfigStatus.Verified);
            TlsFailedCount   = _allConfigs.Count(c => c.Status == FreeConfigStatus.TlsFailed);
            ImplausibleCount = _allConfigs.Count(c => c.Status == FreeConfigStatus.Implausible);
            TimeoutCount     = _allConfigs.Count(c => c.Status == FreeConfigStatus.Timeout);
            UnreachableCount = _allConfigs.Count(c => c.Status == FreeConfigStatus.Unreachable);

            // Populate country filter dropdown.
            var cc = _allConfigs
                .Where(c => !string.IsNullOrEmpty(c.CountryCode))
                .Select(c => c.CountryCode!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Countries = new ObservableCollection<string>(new[] { "All" }.Concat(cc));
            if (!Countries.Contains(SelectedCountry))
                SelectedCountry = "All";

            // Apply filter + sort.
            IEnumerable<FreeConfigEntry> q = _allConfigs;
            if (OnlyWorking)
                q = q.Where(c => c.Status == FreeConfigStatus.Ok || c.Status == FreeConfigStatus.Verified);
            if (!string.Equals(SelectedCountry, "All", StringComparison.OrdinalIgnoreCase))
                q = q.Where(c => string.Equals(c.CountryCode, SelectedCountry, StringComparison.OrdinalIgnoreCase));

            var items = q
                .Select(c => new FreeConfigItemViewModel(c))
                .OrderBy(vm => vm.LatencySortKey)
                .Take(300) // cap at 300 visible to keep ListBox responsive with emoji flags
                .ToList();

            DisplayedConfigs = new ObservableCollection<FreeConfigItemViewModel>(items);

            // Auto-select first item so the Connect button is immediately actionable.
            if (SelectedItem == null || !DisplayedConfigs.Contains(SelectedItem))
                SelectedItem = DisplayedConfigs.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "ApplyFiltersAndStats failed");
            DisplayedConfigs = new ObservableCollection<FreeConfigItemViewModel>();
        }
        finally
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(IsFilteredEmpty));
            OnPropertyChanged(nameof(IsListVisible));
        }
    }

    private static string FormatAge(TimeSpan t)
    {
        if (t.TotalMinutes < 1)   return "just now";
        if (t.TotalMinutes < 60)  return $"{(int)t.TotalMinutes}m ago";
        if (t.TotalHours   < 24)  return $"{(int)t.TotalHours}h ago";
        return $"{(int)t.TotalDays}d ago";
    }
}
