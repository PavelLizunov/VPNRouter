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
    private const int RulesToastDurationMs = 2000;

    [ObservableProperty] private bool _customRulesAboveToggles;

    partial void OnCustomRulesAboveTogglesChanged(bool value)
    {
        if (_isLoadingUI) return;
        _settings.App.CustomRulesPriority = value ? "custom_first" : "toggles_first";
        SaveSettings();
        MarkRoutingSettingsChanged();
    }

    [ObservableProperty] private string _customRulesText = string.Empty;

    [ObservableProperty] private string _customRulesErrorText = string.Empty;

    [ObservableProperty] private string _customRulesConflictText = string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel> CustomRulesList { get; }
    private bool _isSyncingCustomRules;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomRulesCountText))]
    private string _customRulesSearchText = string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel> FilteredCustomRulesList { get; }

    private void RebuildFilteredCustomRulesList()
    {
        FilteredCustomRulesList.Clear();
        var query = (CustomRulesSearchText ?? string.Empty).Trim().ToLowerInvariant();
        var actionFilter = RulesActionFilter ?? "all";

        int total = 0, direct = 0, proxy = 0, block = 0;

        foreach (var vm in CustomRulesList)
        {
            total++;
            switch (vm.Action)
            {
                case "direct": direct++; break;
                case "proxy":  proxy++;  break;
                case "block":  block++;  break;
            }

            if (actionFilter != "all" &&
                !string.Equals(vm.Action, actionFilter, System.StringComparison.OrdinalIgnoreCase))
                continue;

            if (query.Length > 0)
            {
                var haystack = $"{vm.Action} {vm.Type} {vm.Value} {vm.Comment}".ToLowerInvariant();
                if (!haystack.Contains(query)) continue;
            }
            FilteredCustomRulesList.Add(vm);
        }

        RulesFilterCountAll    = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RulesFilterCountDirect = direct.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RulesFilterCountProxy  = proxy.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RulesFilterCountBlock  = block.ToString(System.Globalization.CultureInfo.InvariantCulture);

        RebuildReadModeGroups();

        OnPropertyChanged(nameof(CustomRulesCountText));
    }

    partial void OnCustomRulesSearchTextChanged(string value) => RebuildFilteredCustomRulesList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRuleActionHint))]
    private string _newRuleAction = "direct";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRuleTypeHint))]
    [NotifyPropertyChangedFor(nameof(NewRuleValuePlaceholder))]
    private string _newRuleType = "domain_suffix";

    [ObservableProperty] private string _newRuleValue = string.Empty;
    [ObservableProperty] private string _newRuleComment = string.Empty;
    [ObservableProperty] private string _newRuleValidationError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRuleValueBorderColor))]
    private bool _newRuleValueIsValid = true;

    [ObservableProperty] private string _newRuleValueHint = string.Empty;
    [ObservableProperty] private string _newRuleValuePlaceholder = ".corp.example";

    public bool NewRuleValueBorderColor => !NewRuleValueIsValid;

    public string NewRuleActionHint => NewRuleAction switch
    {
        "direct" => Strings.RuleActionHintDirect,
        "proxy"  => Strings.RuleActionHintProxy,
        "block"  => Strings.RuleActionHintBlock,
        _ => string.Empty,
    };

    public string NewRuleTypeHint => NewRuleType switch
    {
        "domain"         => Strings.RuleTypeHintDomain,
        "domain_suffix"  => Strings.RuleTypeHintDomainSuffix,
        "domain_keyword" => Strings.RuleTypeHintDomainKeyword,
        "ip_cidr"        => Strings.RuleTypeHintIpCidr,
        "port"           => Strings.RuleTypeHintPort,
        "port_range"     => Strings.RuleTypeHintPortRange,
        "network"        => Strings.RuleTypeHintNetwork,
        "process_name"   => Strings.RuleTypeHintProcessName,
        "process_path"   => Strings.RuleTypeHintProcessPath,
        "geosite"        => Strings.RuleTypeHintGeosite,
        "geoip"          => Strings.RuleTypeHintGeoip,
        _                => string.Empty,
    };

    partial void OnNewRuleTypeChanged(string value)
    {
        NewRuleValuePlaceholder = ResolveValuePlaceholder(value);
        ValidateNewRuleValue(NewRuleValue);
    }

    partial void OnNewRuleValueChanged(string value) => ValidateNewRuleValue(value);

    private void ValidateNewRuleValue(string val)
    {
        if (string.IsNullOrWhiteSpace(val))
        {
            NewRuleValueIsValid = true;
            NewRuleValueHint = NewRuleTypeHint;
            return;
        }

        bool ok;
        if (NewRuleType == "domain_regex")
        {
            try { _ = new System.Text.RegularExpressions.Regex(val); ok = true; }
            catch { ok = false; }
        }
        else if (_typeValidatorMap.TryGetValue(NewRuleType, out var regex))
        {
            ok = regex.IsMatch(val.Trim());
        }
        else
        {
            ok = true;
        }

        NewRuleValueIsValid = ok;
        NewRuleValueHint = ok
            ? (IsRussian ? "✓ корректно" : "✓ valid")
            : (IsRussian ? $"✗ не подходит формату {NewRuleType}" : $"✗ wrong format for {NewRuleType}");
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRulesFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsRulesFilterDirect))]
    [NotifyPropertyChangedFor(nameof(IsRulesFilterProxy))]
    [NotifyPropertyChangedFor(nameof(IsRulesFilterBlock))]
    private string _rulesActionFilter = "all";

    public bool IsRulesFilterAll    => RulesActionFilter == "all";
    public bool IsRulesFilterDirect => RulesActionFilter == "direct";
    public bool IsRulesFilterProxy  => RulesActionFilter == "proxy";
    public bool IsRulesFilterBlock  => RulesActionFilter == "block";

    [ObservableProperty] private string _rulesFilterCountAll    = string.Empty;
    [ObservableProperty] private string _rulesFilterCountDirect = string.Empty;
    [ObservableProperty] private string _rulesFilterCountProxy  = string.Empty;
    [ObservableProperty] private string _rulesFilterCountBlock  = string.Empty;

    [RelayCommand]
    private void SetRulesActionFilter(string filter)
    {
        if (string.IsNullOrEmpty(filter)) filter = "all";
        RulesActionFilter = filter;
        RebuildFilteredCustomRulesList();
    }

    public IReadOnlyList<string> AvailableRuleActions { get; }

    public IReadOnlyList<string> AvailableRuleTypes { get; }

    partial void OnCustomRulesTextChanged(string value)
    {
        if (_isLoadingUI) return;
        if (_isSyncingCustomRules) return;
        SaveSettings();
        OnPropertyChanged(nameof(CustomRulesErrorText));
        OnPropertyChanged(nameof(CustomRulesConflictText));

        RebuildCustomRulesList();

        OnPropertyChanged(nameof(RulesEditorIsDirty));

        MarkRoutingSettingsChanged();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRulesViewCards))]
    [NotifyPropertyChangedFor(nameof(IsRulesViewRead))]
    [NotifyPropertyChangedFor(nameof(IsRulesViewEdit))]
    private string _rulesViewMode = "cards";

    [ObservableProperty] private bool _isRulesNarrow;

    public bool IsRulesViewCards => RulesViewMode == "cards";

    public bool IsRulesViewRead => RulesViewMode == "read";

    public bool IsRulesViewEdit => RulesViewMode == "edit";

    [RelayCommand]
    private void SetRulesViewCards() => RulesViewMode = "cards";

    [RelayCommand]
    private void SetRulesViewRead()
    {
        RebuildReadModeGroups();
        RulesViewMode = "read";
    }

    [RelayCommand]
    private void SetRulesViewEdit()
    {
        EditedCustomRulesText = CustomRulesText;
        RulesViewMode = "edit";
        RecomputeRulesEditorState();
    }

    public System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel> ReadModeDirectRules { get; }
    public System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel> ReadModeProxyRules { get; }
    public System.Collections.ObjectModel.ObservableCollection<CustomRuleViewModel> ReadModeBlockRules { get; }

    [ObservableProperty]
    private string _editedCustomRulesText = string.Empty;

    partial void OnEditedCustomRulesTextChanged(string value) => RecomputeRulesEditorState();

    [ObservableProperty] private string _rulesEditorLineNumbers = "1";

    [ObservableProperty] private string _rulesEditorStatusText = string.Empty;

    [ObservableProperty] private string _rulesEditorErrorListText = string.Empty;

    [ObservableProperty] private bool _rulesEditorHasErrors;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RulesEditorApplyText))]
    private int _rulesEditorActiveCount;

    public bool RulesEditorIsDirty =>
        !string.Equals(EditedCustomRulesText ?? string.Empty,
                       CustomRulesText ?? string.Empty,
                       System.StringComparison.Ordinal);

    public string RulesEditorApplyText => IsRussian
        ? $"Применить ({RulesEditorActiveCount})"
        : $"Apply ({RulesEditorActiveCount})";

    private void RecomputeRulesEditorState()
    {
        var text = EditedCustomRulesText ?? string.Empty;
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        var validActions = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
        {
            "direct", "proxy", "block"
        };
        var validTypes = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
        {
            "domain", "domain_suffix", "domain_keyword", "domain_regex",
            "ip_cidr", "port", "port_range", "network",
            "process_name", "process_path", "geosite", "geoip"
        };

        int active = 0;
        var errors = new System.Collections.Generic.List<(int Line, string Msg)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var ln = raw.Trim();
            if (string.IsNullOrEmpty(ln)) continue;
            if (ln.StartsWith("#", System.StringComparison.Ordinal) ||
                ln.StartsWith("!", System.StringComparison.Ordinal)) continue;

            var hashIdx = ln.IndexOf('#');
            if (hashIdx >= 0) ln = ln.Substring(0, hashIdx).Trim();
            if (string.IsNullOrWhiteSpace(ln)) continue;

            var tokens = ln.Split(new[] { ' ', '\t' },
                System.StringSplitOptions.RemoveEmptyEntries);

            var firstTok = tokens.Length > 0 ? tokens[0] : string.Empty;
            if (!validActions.Contains(firstTok))
            {
                errors.Add((i + 1, IsRussian
                    ? $"неизвестный action «{firstTok}»"
                    : $"unknown action «{firstTok}»"));
                continue;
            }
            var secondTok = tokens.Length > 1 ? tokens[1] : string.Empty;
            if (!validTypes.Contains(secondTok))
            {
                errors.Add((i + 1, Strings.RuleParserUnknownType(secondTok)));
                continue;
            }
            if (tokens.Length < 3)
            {
                errors.Add((i + 1, Strings.RuleParserMissingValue));
                continue;
            }
            active++;
        }

        RulesEditorActiveCount = active;
        RulesEditorHasErrors = errors.Count > 0;

        var sbNums = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) sbNums.Append('\n');
            sbNums.Append((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        RulesEditorLineNumbers = sbNums.ToString();

        var status = IsRussian
            ? $"{active} {(active == 1 ? "правило" : "правил")} активно"
            : $"{active} rule{(active == 1 ? "" : "s")} active";
        if (errors.Count > 0)
        {
            status += IsRussian
                ? $"  ·  {errors.Count} {(errors.Count == 1 ? "ошибка" : "ошибок")}"
                : $"  ·  {errors.Count} error{(errors.Count == 1 ? "" : "s")}";
        }
        RulesEditorStatusText = status;

        if (errors.Count == 0)
        {
            RulesEditorErrorListText = string.Empty;
        }
        else
        {
            var head = new System.Text.StringBuilder();
            int take = System.Math.Min(4, errors.Count);
            for (int i = 0; i < take; i++)
            {
                if (i > 0) head.Append('\n');
                var e = errors[i];
                head.Append(IsRussian
                    ? $"строка {e.Line}: {e.Msg}"
                    : $"line {e.Line}: {e.Msg}");
            }
            if (errors.Count > take)
            {
                head.Append('\n');
                head.Append(IsRussian
                    ? $"и ещё {errors.Count - take}…"
                    : $"and {errors.Count - take} more…");
            }
            RulesEditorErrorListText = head.ToString();
        }

        OnPropertyChanged(nameof(RulesEditorIsDirty));
        OnPropertyChanged(nameof(RulesEditorApplyText));
    }

    [RelayCommand]
    private void ApplyEditedRules()
    {
        if (RulesEditorHasErrors) return;
        CustomRulesText = EditedCustomRulesText ?? string.Empty;
        RecomputeRulesEditorState();
    }

    [RelayCommand]
    private void RevertEditedRules()
    {
        EditedCustomRulesText = CustomRulesText ?? string.Empty;
        RecomputeRulesEditorState();
    }

    [ObservableProperty] private bool _isRulesHelpBannerDismissed;

    [RelayCommand]
    private void DismissRulesHelpBanner() => IsRulesHelpBannerDismissed = true;

    private void RebuildCustomRulesList()
    {
        if (_isSyncingCustomRules) return;
        _isSyncingCustomRules = true;
        try
        {
            CustomRulesList.Clear();
            foreach (var rule in _settings.App.CustomRules)
            {
                CustomRulesList.Add(new CustomRuleViewModel(
                    rule,
                    onChanged: OnCustomRuleRowChanged,
                    onRemoveRequested: OnCustomRuleRowRemoveRequested));
            }
        }
        finally { _isSyncingCustomRules = false; }
        RebuildFilteredCustomRulesList();
    }

    [RelayCommand]
    private void ClearAllCustomRules()
    {
        if (CustomRulesList.Count == 0) return;
        ClearAllConfirmPending = true;
        ClearAllConfirmText = IsRussian
            ? $"Удалить все правила ({CustomRulesList.Count})?"
            : $"Delete all rules ({CustomRulesList.Count})?";
    }

    [RelayCommand]
    private void ConfirmClearAllCustomRules()
    {
        if (CustomRulesList.Count == 0)
        {
            ClearAllConfirmPending = false;
            ClearAllConfirmText = string.Empty;
            return;
        }
        CustomRulesList.Clear();
        FilteredCustomRulesList.Clear();
        FlushCustomRulesListToSettings();
        ClearAllConfirmPending = false;
        ClearAllConfirmText = string.Empty;
        ShowRulesToast(Strings.RulesAllDeleted);
    }

    [RelayCommand]
    private void CancelClearAllCustomRules()
    {
        ClearAllConfirmPending = false;
        ClearAllConfirmText = string.Empty;
    }

    [ObservableProperty] private bool _clearAllConfirmPending;
    [ObservableProperty] private string _clearAllConfirmText = string.Empty;

    [RelayCommand]
    private void EnableAllCustomRules()
    {
        if (CustomRulesList.Count == 0) return;
        foreach (var vm in CustomRulesList) vm.Enabled = true;
    }

    [RelayCommand]
    private void DisableAllCustomRules()
    {
        if (CustomRulesList.Count == 0) return;
        foreach (var vm in CustomRulesList) vm.Enabled = false;
    }

    [RelayCommand]
    private void SortCustomRulesByType()
    {
        if (CustomRulesList.Count <= 1)
        {
            ShowRulesToast(IsRussian
                ? "Нечего сортировать"
                : "Nothing to sort");
            return;
        }

        var preOrder = CustomRulesList
            .Select(r => $"{r.Type}|{r.Action}|{r.Value}")
            .ToList();

        var sorted = CustomRulesList
            .OrderBy(r => r.Type, System.StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Action, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        var postOrder = sorted.Select(r => $"{r.Type}|{r.Action}|{r.Value}").ToList();
        bool changed = !preOrder.SequenceEqual(postOrder);

        _isSyncingCustomRules = true;
        try
        {
            CustomRulesList.Clear();
            foreach (var r in sorted) CustomRulesList.Add(r);
        }
        finally { _isSyncingCustomRules = false; }
        FlushCustomRulesListToSettings();
        RebuildFilteredCustomRulesList();

        ShowRulesToast(changed
            ? (IsRussian ? $"✓ Отсортировано по типу ({sorted.Count})"
                         : $"✓ Sorted by type ({sorted.Count})")
            : Strings.RulesAlreadySorted);
    }

    [ObservableProperty] private string _rulesToastText = string.Empty;

    private System.Threading.CancellationTokenSource? _rulesToastCts;

    private void ShowRulesToast(string text)
    {
        RulesToastText = text;
        var oldCts = _rulesToastCts;
        _rulesToastCts = new System.Threading.CancellationTokenSource();
        var token = _rulesToastCts.Token;
        if (oldCts != null)
        {
            try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
            oldCts.Dispose();
        }
        _ = System.Threading.Tasks.Task.Delay(RulesToastDurationMs, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!token.IsCancellationRequested) RulesToastText = string.Empty;
            });
        }, System.Threading.Tasks.TaskScheduler.Default);
    }

    private void OnCustomRuleRowChanged(CustomRuleViewModel _)
    {
        if (_isSyncingCustomRules || _isLoadingUI) return;
        FlushCustomRulesListToSettings();
    }

    private void OnCustomRuleRowRemoveRequested(CustomRuleViewModel row)
    {
        if (_isLoadingUI) return;
        CustomRulesList.Remove(row);
        FilteredCustomRulesList.Remove(row);
        OnPropertyChanged(nameof(CustomRulesCountText));
        FlushCustomRulesListToSettings();
    }

    private void FlushCustomRulesListToSettings()
    {
        if (_isSyncingCustomRules) return;
        _isSyncingCustomRules = true;
        try
        {
            _settings.App.CustomRules = CustomRulesList.Select(vm => vm.ToModel()).ToList();
            CustomRulesText = VPNRouter.Core.Services.CustomRulesParser
                .SerializeToText(_settings.App.CustomRules);
            var conflicts = VPNRouter.Core.Services.CustomRulesParser
                .DetectConflicts(_settings.App.CustomRules);
            CustomRulesConflictText = conflicts.Count == 0
                ? string.Empty
                : string.Join("\n", conflicts);
            CustomRulesErrorText = string.Empty;
        }
        finally { _isSyncingCustomRules = false; }
        RebuildFilteredCustomRulesList();
        SaveSettings();
        MarkRoutingSettingsChanged();
    }

    [RelayCommand]
    private async Task ImportCustomRulesAsync()
    {
        try
        {
            var window = GetMainWindow();
            if (window == null)
            {
                NewRuleValidationError = Strings.RulesFilePickerOpenFailed;
                return;
            }

            var files = await window.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = Strings.RulesImportDialogTitle,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Rule files (CSV, JSON)")
                    {
                        Patterns = new[] { "*.csv", "*.json", "*.txt" },
                    },
                    new Avalonia.Platform.Storage.FilePickerFileType("All files")
                    {
                        Patterns = new[] { "*.*" },
                    },
                }
            });
            if (files.Count == 0) return;

            var file = files[0];
            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            var text = await File.ReadAllTextAsync(path);
            var result = VPNRouter.Core.Services.CustomRulesImportExport.ImportFromText(text);

            if (result.Rules.Count == 0)
            {
                NewRuleValidationError = result.Warnings.Count > 0
                    ? Strings.RulesImportFailed(result.Warnings[0])
                    : Strings.RulesImportNoRules;
                return;
            }

            foreach (var rule in result.Rules)
            {
                CustomRulesList.Add(new CustomRuleViewModel(
                    rule,
                    onChanged: OnCustomRuleRowChanged,
                    onRemoveRequested: OnCustomRuleRowRemoveRequested));
            }
            FlushCustomRulesListToSettings();

            var msg = Strings.RulesImported(result.Rules.Count, result.DetectedFormat.ToString());
            if (result.Warnings.Count > 0)
                msg += Strings.RulesImportWithWarnings(result.Warnings.Count);
            NewRuleValidationError = msg;
            foreach (var w in result.Warnings)
                _logger.Information("[CustomRules import] {Warning}", w);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] ImportCustomRules failed");
            NewRuleValidationError = Strings.RulesImportError(ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportCustomRulesAsync()
    {
        try
        {
            var window = GetMainWindow();
            if (window == null)
            {
                NewRuleValidationError = Strings.RulesFilePickerOpenFailed;
                return;
            }

            if (CustomRulesList.Count == 0)
            {
                NewRuleValidationError = Strings.RulesExportNothing;
                return;
            }

            var file = await window.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = Strings.RulesExportDialogTitle,
                SuggestedFileName = $"vpnrouter-rules-{DateTime.Now:yyyyMMdd}",
                DefaultExtension = "json",
                FileTypeChoices = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("VPNRouter JSON (native)")
                    {
                        Patterns = new[] { "*.json" },
                    },
                    new Avalonia.Platform.Storage.FilePickerFileType("CSV (spreadsheet-friendly)")
                    {
                        Patterns = new[] { "*.csv" },
                    },
                    new Avalonia.Platform.Storage.FilePickerFileType("sing-box JSON (NekoBox / Hiddify compat)")
                    {
                        Patterns = new[] { "*.singbox.json" },
                    },
                }
            });
            if (file == null) return;

            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            var fmt = VPNRouter.Core.Services.CustomRulesImportExport.Format.VpnrouterJson;
            if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                fmt = VPNRouter.Core.Services.CustomRulesImportExport.Format.Csv;
            else if (path.EndsWith(".singbox.json", StringComparison.OrdinalIgnoreCase))
                fmt = VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson;

            var rules = CustomRulesList.Select(vm => vm.ToModel()).ToList();
            var content = VPNRouter.Core.Services.CustomRulesImportExport.ExportToText(rules, fmt);
            await File.WriteAllTextAsync(path, content);

            NewRuleValidationError = Strings.RulesExported(rules.Count, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[VM] ExportCustomRules failed");
            NewRuleValidationError = Strings.RulesExportError(ex.Message);
        }
    }

    [RelayCommand]
    private void AddCustomRuleFromForm()
    {
        if (string.IsNullOrWhiteSpace(NewRuleValue))
        {
            NewRuleValidationError = Strings.RulesEmptyValue;
            return;
        }
        if (!NewRuleValueIsValid)
        {
            NewRuleValidationError = IsRussian
                ? $"Значение не подходит к типу «{NewRuleType}»"
                : $"Value doesn't match type \"{NewRuleType}\"";
            return;
        }
        var commentSuffix = string.IsNullOrWhiteSpace(NewRuleComment)
            ? string.Empty
            : $"  # {NewRuleComment.Trim()}";
        var line = $"{NewRuleAction} {NewRuleType} {NewRuleValue.Trim()}{commentSuffix}";
        var parsed = VPNRouter.Core.Services.CustomRulesParser.ParseFromText(line);
        if (parsed.Errors.Count > 0)
        {
            NewRuleValidationError = parsed.Errors[0].Reason;
            return;
        }
        if (parsed.Rules.Count == 0)
        {
            NewRuleValidationError = "Failed to parse";
            return;
        }
        CustomRulesList.Add(new CustomRuleViewModel(
            parsed.Rules[0],
            onChanged: OnCustomRuleRowChanged,
            onRemoveRequested: OnCustomRuleRowRemoveRequested));
        NewRuleValue = string.Empty;
        NewRuleComment = string.Empty;
        NewRuleValidationError = string.Empty;
        FlushCustomRulesListToSettings();
    }
}
