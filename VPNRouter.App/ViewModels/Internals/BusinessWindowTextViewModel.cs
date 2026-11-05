using VPNRouter.Core.Localization;

namespace VPNRouter.App.ViewModels.Internals;

// Window-owned reactive labels; language changes do not recreate service owners.
public sealed class BusinessWindowTextViewModel : ViewModelBase
{
    private MainWindowViewModel? _client;
    private bool _languageSaveFailed;
    public string ConnectButton => _client?.IsConnecting == true && IsStoppingStatus(_client.StatusText) ? Strings.Stopping
        : _client?.IsConnecting == true ? Strings.ButtonConnecting
        : _client?.IsConnected == true ? Strings.StopVPN : Strings.StartVPN;
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
        _client = client;
        _languageSaveFailed = false;
        Refresh();
    }
    internal void SetLanguageSaveFailed(bool failed) { _languageSaveFailed = failed; Refresh(); }

    public string Menu => Strings.BusinessMenu;
    public string Language => Strings.BusinessLanguage;
    public string CountryHint => Strings.BusinessCountryHint;
    public string Server => Strings.BusinessServer;
    public string ServerRefresh => Strings.BusinessServerRefresh;
    public string UpdateServers => Strings.BusinessUpdateServers;
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
    public string ModesHint => Strings.BusinessModesHint;
    public string Hint => Strings.BusinessHint;
    public string Details => Strings.BusinessDetails;

    internal void Refresh() => OnPropertyChanged(string.Empty);
}
