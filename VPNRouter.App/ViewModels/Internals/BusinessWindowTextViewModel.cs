using VPNRouter.Core.Localization;

namespace VPNRouter.App.ViewModels.Internals;

// Window-owned reactive labels; language changes do not recreate service owners.
public sealed class BusinessWindowTextViewModel : ViewModelBase
{
    private MainWindowViewModel? _client;
    private bool _languageSaveFailed;
    public string ConnectButton => _client?.IsConnecting == true && IsStoppingStatus(_client.StatusText) ? Strings.Stopping
        : _client?.IsConnecting == true ? Strings.ButtonConnecting
        : _client?.IsConnected == true ? Strings.BusinessDisconnect : Strings.BusinessConnect;
    public string ConnectionStatus => _languageSaveFailed ? Strings.BusinessLanguageSaveFailed
        : _client?.IsConnecting == true && IsStoppingStatus(_client.StatusText) ? Strings.Stopping
        : _client?.IsConnecting == true && IsNeutralStartingStatus(_client.StatusText) ? Strings.ButtonConnecting
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
    }

    public string Menu => Strings.BusinessMenu;
    public string PolicyStatus => _client?.BusinessPolicyStatusText ?? string.Empty;
    public bool HasPolicyStatus => !string.IsNullOrEmpty(PolicyStatus);
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
    public string ServerRefresh => Strings.BusinessServerRefresh;
    public string UpdateServers => Strings.BusinessUpdateServers;
    public string InstallUpdate => Strings.UpdateButton;
    public string LaterUpdate => Strings.UpdateButtonDismiss;
    public string RepairTitle => Strings.BusinessRepairTitle;
    public string RepairAnalyze => Strings.BusinessRepairAnalyze;
    public string RepairConsent => Strings.BusinessRepairConsent;
    public string RepairClose => Strings.BusinessRepairClose;
    public string DiagnosticsHeader => Strings.BusinessDiagnosticsHeader;
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
