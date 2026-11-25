using VPNRouter.Core.Localization;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels.Internals;

// Window-owned reactive labels; language changes do not recreate service owners.
public sealed class BusinessWindowTextViewModel : ViewModelBase
{
    private MainWindowViewModel? _client;
    private bool _languageSaveFailed;
    private BusinessServerChoiceViewModel? _savedServer;
    private bool _hasProbeTargets, _isServerChecking;
    public bool CanToggleConnection => _client is { CanToggleConnection: true } &&
        (!_client.IsBusinessRepairInProgress || _client.IsConnected);
    public bool IsConnectionBusy => _client is { } client && (client.IsConnecting || client.IsBusinessRepairInProgress);
    public string ConnectButton => _client?.IsConnecting == true && IsStoppingStatus(_client.StatusText) ? Strings.Stopping
        : _client?.IsConnecting == true ? Strings.ButtonConnecting
        : _client?.IsConnected == true ? Strings.BusinessDisconnect : Strings.BusinessConnect;
    public string ConnectionStatus => _languageSaveFailed ? Strings.BusinessLanguageSaveFailed
        : _client?.IsConnecting == true && IsStoppingStatus(_client.StatusText) ? Strings.Stopping
        : _client is { IsBusinessRepairInProgress: true, IsConnected: false } ? Strings.BusinessRepairChecking
        : _client?.IsConnecting == true && IsNeutralStartingStatus(_client.StatusText) ? Strings.ButtonConnecting
        : _client?.StatusText == Strings.BusinessPolicyApplying ? Strings.ButtonConnecting
        : _client?.IsConnected == true && _client.StatusText == Strings.SmpStatusConnectedNoDetails
            ? _client.IsBusinessServiceManaged ? Strings.BusinessServiceConnected : Strings.BusinessConnected
        : _client?.IsConnected == true && IsHealthyConnectionStatus(_client.StatusText)
            ? Strings.BusinessConnected
        : _client?.IsConnected == true && IsHealthyConnectionStatus(_client.StatusText, serviceManaged: true)
            ? Strings.BusinessServiceConnected : _client?.StatusText ?? string.Empty;

    private static bool IsStoppingStatus(string status) => status is "Stopping..." or "Остановка...";

    private static bool IsNeutralStartingStatus(string status) =>
        string.IsNullOrEmpty(status) || status is "Starting..." or "Запуск..." or "Connecting…" or "Подключение…" or "Not connected" or "Не подключено";

    private static bool IsHealthyConnectionStatus(string status, bool serviceManaged = false)
    {
        var prefixes = serviceManaged ? new[] { "Connected via service [", "Подключено через службу [" } : new[] { "Connected [", "Подключено [" };
        foreach (var prefix in prefixes)
        {
            if (!status.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var end = status.IndexOf(']', prefix.Length);
            if (end < 0) return false;
            var mode = status[prefix.Length..end];
            return (mode is "split" or "full" or "manual/split" or "manual/full" or "subscribe/split" or "subscribe/full" or "custom/split" or "custom/full") && (end == status.Length - 1 || status[(end + 1)..].StartsWith(" → ", StringComparison.Ordinal));
        }
        return false;
    }

    internal void SetClient(MainWindowViewModel? client)
    {
        if (_client != null) _client.BusinessPolicyStatusChanged -= Refresh;
        _client = client;
        _savedServer = null;
        _hasProbeTargets = _isServerChecking = false;
        if (_client != null) _client.BusinessPolicyStatusChanged += Refresh;
        _languageSaveFailed = false;
        Refresh();
    }
    internal void SetLanguageSaveFailed(bool failed) { _languageSaveFailed = failed; Refresh(); }

    internal void RefreshAutoSelect()
    {
        OnPropertyChanged(nameof(AutoSelectEnabled));
        OnPropertyChanged(nameof(AutoSelectStatus));
        OnPropertyChanged(nameof(HasAutoSelectStatus));
        OnPropertyChanged(nameof(ServerSummary));
        OnPropertyChanged(nameof(ServerPickerCaption));
        OnPropertyChanged(nameof(ServerPickerPlaceholder));
    }

    public string Menu => Strings.BusinessMenu;
    public string PolicyStatus => _client?.BusinessPolicyStatusText ?? string.Empty;
    public bool HasPolicyStatus => _client?.HasHomeBusinessPolicyNotice == true;
    public bool IsPolicyDanger => _client?.IsBusinessPolicyDanger == true;
    public bool IsPolicyApplying => _client?.IsBusinessPolicyApplying == true;
    public string Language => Strings.BusinessLanguage;
    public string Server => Strings.BusinessServer;
    public string ServerMenu => Strings.BusinessServerMenu;
    public string ModeMenu => Strings.BusinessModeMenu;
    public string AutoSelect => Strings.BusinessAutoSelectServer;
    public string AutoSelectHint => Strings.BusinessAutoSelectHint;
    public bool AutoSelectEnabled
    {
        get => _client?.BusinessAutoSelect == true;
        set
        {
            if (_client != null && _client.BusinessAutoSelect != value)
                _client.SetBusinessAutoSelect(value);
        }
    }
    public string AutoSelectStatus => _client?.BusinessAutoSelectStatusText ?? string.Empty;
    public bool HasAutoSelectStatus => _client?.HasBusinessAutoSelectStatus == true;
    internal void SetSavedServer(BusinessServerChoiceViewModel? choice)
    {
        _savedServer = choice;
        OnPropertyChanged(nameof(ServerPickerPlaceholder));
        RefreshServerSummary();
    }

    internal void RefreshServerSummary()
    {
        OnPropertyChanged(nameof(ServerSummary));
        OnPropertyChanged(nameof(AutoSelectStatus));
        OnPropertyChanged(nameof(HasAutoSelectStatus));
    }

    public string ServerSummary
    {
        get
        {
            if (_client == null) return string.Empty;
            if (_client.IsConnected)
                return _client.BusinessActiveServerName is { } active ? Strings.BusinessServerSummaryActive(active)
                    : _client.IsBusinessServiceManaged ? Strings.BusinessServerSummaryActiveService
                    : Strings.BusinessServerSummaryActiveUnknown;

            if (_client.BusinessAutoSelect)
            {
                if (_client.IsBusinessAutoChecking) return Strings.BusinessServerSummaryAutoChecking;
                if (_client.BusinessFreshAutoWinner is { } winner)
                    return Strings.BusinessServerSummaryAutoChecked(winner.Name, winner.LatencyMs);
                return _client.IsBusinessAutoProblem
                    ? Strings.BusinessServerSummaryAutoUnavailable : Strings.BusinessServerSummaryAutoPending;
            }

            if (_client.SelectedServer is not { } selected) return Strings.BusinessServerNotChecked;
            if (_savedServer is { IsChecking: false } saved && ReferenceEquals(saved.Server, selected))
            {
                if (saved.IsBad)
                    return Strings.BusinessServerSummaryManualWithTag(selected.DisplayName, Strings.BusinessServerNoReplyShort);
                if (saved.ProbeStatus is ServerProbeStatus.Ok or ServerProbeStatus.Slow)
                    return Strings.BusinessServerSummaryManualWithTag(selected.DisplayName, saved.PingText);
            }
            return Strings.BusinessServerSummaryManual(selected.DisplayName);
        }
    }

    public string ServerPickerPlaceholder => _savedServer?.DisplayName ?? Strings.BusinessServer;
    public string ServerPickerCaption => AutoSelectEnabled
        ? Strings.BusinessServerPickerAutoCaption : Strings.BusinessServerPickerManualCaption;
    public string ServerLocked => _client?.BusinessActiveServerName is { } name
        ? Strings.BusinessServerLockedActive(name) : Strings.BusinessServerLocked;
    public string ModeSummary => _client?.IsConnected == true
        ? _client.BusinessActiveRoutingMode switch
        {
            "split" => Strings.BusinessSplit,
            "full" => Strings.BusinessFullCompact,
            _ => Strings.BusinessModeUnknown
        }
        : _client?.IsSplitTunnel == true ? Strings.BusinessSplit : Strings.BusinessFullCompact;
    internal void RefreshModeSummary() => OnPropertyChanged(nameof(ModeSummary));
    public string ModeLocked => Strings.BusinessModeLocked;
    public string FullDescription => Strings.BusinessFullDescription;
    public string SplitDescription => Strings.BusinessSplitDescription;
    public string UpdateStopNote => Strings.BusinessUpdateStopNote;
    public string ServerRefresh => _isServerChecking ? Strings.BusinessServerRefreshChecking : Strings.BusinessServerRefresh;
    public bool CanTestServers => _hasProbeTargets && !_isServerChecking;

    internal void UpdateServerCheck(bool hasTargets, bool isChecking)
    {
        if (_hasProbeTargets == hasTargets && _isServerChecking == isChecking) return;
        _hasProbeTargets = hasTargets;
        _isServerChecking = isChecking;
        OnPropertyChanged(nameof(ServerRefresh));
        OnPropertyChanged(nameof(CanTestServers));
    }

    public string UpdateServers => Strings.BusinessUpdateServers;
    public string InstallUpdate => Strings.UpdateButton;
    public string LaterUpdate => Strings.UpdateButtonDismiss;
    public string RepairTitle => _client?.IsConnected == true
        ? Strings.BusinessRepairConnectedTitle : Strings.BusinessRepairTitle;
    public string DiagnosticsCollect => Strings.BusinessDiagnosticsCollect;
    public string DiagnosticsSend => Strings.BusinessDiagnosticsSend;
    public string DiagnosticsCancel => Strings.BusinessDiagnosticsCancel;
    public string DiagnosticsOpen => Strings.BusinessDiagnosticsOpen;
    public string DiagnosticsHint => Strings.BusinessDiagnosticsHint;
    public string ServerHealthHint => Strings.BusinessServerHealthHint;
    public string ServerHealthLegend => Strings.BusinessServerHealthLegend;
    public string ServerHint => Strings.BusinessServerHint;
    public string Split => Strings.BusinessSplit;
    public string Full => Strings.BusinessFull;
    public string FullCompact => Strings.BusinessFullCompact;

    internal void Refresh() => OnPropertyChanged(string.Empty);
}
