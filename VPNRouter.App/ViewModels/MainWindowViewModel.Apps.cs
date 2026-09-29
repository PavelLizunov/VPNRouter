using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Platform;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels.FreeConfigs;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private string _newSubName = string.Empty;
    [ObservableProperty] private string _newSubUrl = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SimpleConfigModeSummary))]
    [NotifyPropertyChangedFor(nameof(IsFullTunnel))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitRetryVisible))]
    private bool _isSplitTunnel = true;

    public bool IsFullTunnel
    {
        get => !IsSplitTunnel;
        set
        {
            if (IsSplitTunnel != !value)
                IsSplitTunnel = !value;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRoutingAppsModeInclude))]
    [NotifyPropertyChangedFor(nameof(IsRoutingAppsModeExclude))]
    [NotifyPropertyChangedFor(nameof(L_CurrentAppsModeHint))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitRetryVisible))]
    private string _routingAppsMode = "include";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppsListEditorInclude))]
    [NotifyPropertyChangedFor(nameof(IsAppsListEditorExclude))]
    [NotifyPropertyChangedFor(nameof(ActiveAppGroups))]
    [NotifyPropertyChangedFor(nameof(SelectedActiveAppGroup))]
    private string _appsListEditorMode = "include";

    public bool IsAppsListEditorInclude
    {
        get => string.Equals(AppsListEditorMode, "include", StringComparison.OrdinalIgnoreCase);
        set { if (value) AppsListEditorMode = "include"; }
    }

    public bool IsAppsListEditorExclude
    {
        get => string.Equals(AppsListEditorMode, "exclude", StringComparison.OrdinalIgnoreCase);
        set { if (value) AppsListEditorMode = "exclude"; }
    }

    public ObservableCollection<AppGroupViewModel> ActiveAppGroups =>
        IsAppsListEditorExclude ? BypassAppGroups : AppGroups;

    public AppGroupViewModel? SelectedActiveAppGroup
    {
        get => IsAppsListEditorExclude ? SelectedBypassAppGroup : SelectedAppGroup;
        set
        {
            if (IsAppsListEditorExclude)
                SelectedBypassAppGroup = value;
            else
                SelectedAppGroup = value;
            OnPropertyChanged();
        }
    }

    partial void OnAppsListEditorModeChanged(string value)
    {
        if (value != "include" && value != "exclude")
        {
            AppsListEditorMode = "include";
            return;
        }
        OnPropertyChanged(nameof(ActiveAppGroups));
        OnPropertyChanged(nameof(SelectedActiveAppGroup));
    }

    public bool IsRoutingAppsModeInclude
    {
        get => string.Equals(RoutingAppsMode, "include", StringComparison.OrdinalIgnoreCase);
        set { if (value) RoutingAppsMode = "include"; }
    }

    public bool IsRoutingAppsModeExclude
    {
        get => string.Equals(RoutingAppsMode, "exclude", StringComparison.OrdinalIgnoreCase);
        set { if (value) RoutingAppsMode = "exclude"; }
    }

    partial void OnRoutingAppsModeChanged(string value)
    {
        if (_isLoadingUI) return;
        var canon = (value ?? "include").Trim().ToLowerInvariant();
        if (canon != "include" && canon != "exclude") canon = "include";
        _settings.App.RoutingAppsMode = canon;
        AppsListEditorMode = canon;

        RefreshAppCheckboxes();

        try { SaveSettings(); }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[VM] SaveSettings on apps mode change failed");
        }
        MarkRoutingSettingsChanged();
    }

    internal bool IsAppCheckedInCurrentMode(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        var list = GetActiveAppList();
        if (list == null) return false;
        return list.Any(p =>
            string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));
    }

    internal void SetAppCheckedInCurrentMode(string processName, bool isChecked)
    {
        if (string.IsNullOrEmpty(processName)) return;
        if (!string.Equals(_settings.App.RoutingAppsMode, "exclude", StringComparison.OrdinalIgnoreCase))
            _settings.App.RoutingAppsIncludeInitialized = true;
        var list = GetActiveAppList();
        if (list == null) return;

        var existing = list.FirstOrDefault(p =>
            string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));

        if (isChecked)
        {
            if (existing != null) return;
            list.Add(processName);
        }
        else
        {
            if (existing == null) return;
            list.Remove(existing);
        }

        if (_isLoadingUI || IsBatchUpdating) return;

        try { SaveSettings(); }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[VM] SaveSettings on per-app mode-aware toggle failed");
        }
        MarkRoutingSettingsChanged();
    }

    internal bool IsAppCheckedInIncludeList(string processName) =>
        IsAppCheckedInList(_settings.App.RoutingAppsInclude, processName);

    internal bool IsAppCheckedInExcludeList(string processName) =>
        IsAppCheckedInList(_settings.App.RoutingAppsExclude, processName);

    internal void SetAppCheckedInIncludeList(string processName, bool isChecked) =>
        SetAppCheckedInList(_settings.App.RoutingAppsInclude ??= new List<string>(), processName, isChecked, isIncludeList: true);

    internal void SetAppCheckedInExcludeList(string processName, bool isChecked) =>
        SetAppCheckedInList(_settings.App.RoutingAppsExclude ??= new List<string>(), processName, isChecked, isIncludeList: false);

    private static bool IsAppCheckedInList(List<string>? list, string processName)
    {
        if (string.IsNullOrEmpty(processName) || list == null) return false;
        return list.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));
    }

    private void SetAppCheckedInList(List<string> list, string processName, bool isChecked, bool isIncludeList)
    {
        if (string.IsNullOrEmpty(processName)) return;
        if (isIncludeList) _settings.App.RoutingAppsIncludeInitialized = true;
        var existing = list.FirstOrDefault(p =>
            string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));

        if (isChecked)
        {
            if (existing != null) return;
            list.Add(processName);
        }
        else
        {
            if (existing == null) return;
            list.Remove(existing);
        }

        if (_isLoadingUI || IsBatchUpdating) return;

        try { SaveSettings(); }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[VM] SaveSettings on per-app list toggle failed");
        }
        var editsActiveList = isIncludeList == !string.Equals(
            _settings.App.RoutingAppsMode, "exclude", StringComparison.OrdinalIgnoreCase);
        if (editsActiveList) MarkRoutingSettingsChanged();
    }

    private List<string>? GetActiveAppList()
    {
        if (_settings?.App == null) return null;
        var isExclude = string.Equals(
            _settings.App.RoutingAppsMode, "exclude",
            StringComparison.OrdinalIgnoreCase);

        if (isExclude)
        {
            return _settings.App.RoutingAppsExclude
                ??= new List<string>();
        }
        return _settings.App.RoutingAppsInclude
            ??= new List<string>();
    }

    private int _batchUpdateDepth;
    public bool IsBatchUpdating => _batchUpdateDepth > 0;

    public IDisposable BeginBatchUpdate()
    {
        System.Threading.Interlocked.Increment(ref _batchUpdateDepth);
        return new BatchUpdateScope(this);
    }

    private sealed class BatchUpdateScope : IDisposable
    {
        private MainWindowViewModel? _vm;
        public BatchUpdateScope(MainWindowViewModel vm) => _vm = vm;
        public void Dispose()
        {
            var vm = System.Threading.Interlocked.Exchange(ref _vm, null);
            if (vm == null) return;
            if (System.Threading.Interlocked.Decrement(ref vm._batchUpdateDepth) == 0)
            {
                if (!vm._isLoadingUI)
                {
                    try { vm.SaveSettings(); }
                    catch (Exception ex) { vm._logger.Warning(ex, "[VM] SaveSettings after batch update failed"); }
                    vm.MarkRoutingSettingsChanged();
                }
            }
        }
    }

    private void RefreshAppCheckboxes()
    {
        foreach (var group in AppGroups.Concat(BypassAppGroups))
        {
            foreach (var app in group.Apps)
                app.RaiseIsCheckedChanged();
        }
    }
}
