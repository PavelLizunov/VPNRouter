using VPNRouter.Core.Localization;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels.Internals;

// Transient Business-only display state. Never saved with settings or health history.
public sealed class BusinessServerChoiceViewModel : ViewModelBase
{
    internal ServerViewModel Server { get; }
    public string DisplayName => Server.DisplayName;
    public string CountryCode => BusinessServerCountry.Code(Server.Name);
    public string CountryName => Strings.BusinessCountryName(CountryCode);
    public bool HasCountry => CountryCode.Length != 0;
    public Avalonia.Media.IImage? CountryFlag => BusinessServerCountry.Flag(CountryCode);
    private ServerProbeResult _result = ServerProbeResult.Unknown;
    private bool _checking;

    internal BusinessServerChoiceViewModel(ServerViewModel server) => Server = server;

    public bool IsGood => !_checking && _result.Status == ServerProbeStatus.Ok;
    public bool IsWarning => !_checking && _result.Status is ServerProbeStatus.Slow or ServerProbeStatus.Implausible;
    public bool IsBad => !_checking && _result.Status is ServerProbeStatus.Unreachable or ServerProbeStatus.Timeout or ServerProbeStatus.TlsFailed;

    public string StatusText => _checking ? Strings.BusinessServerChecking : _result.Status switch
    {
        ServerProbeStatus.Ok => Strings.BusinessServerReachable,
        ServerProbeStatus.Slow => Strings.BusinessServerSlow,
        ServerProbeStatus.Unreachable or ServerProbeStatus.Timeout or ServerProbeStatus.TlsFailed => Strings.BusinessServerNoReply,
        ServerProbeStatus.Implausible => Strings.BusinessServerInconclusive,
        _ => Strings.BusinessServerNotChecked
    };

    public string PingText => _checking ? Strings.BusinessServerChecking : _result.Status switch
    {
        ServerProbeStatus.Ok or ServerProbeStatus.Slow => _result.LatencyMs > 0
            ? Strings.BusinessServerLatency(_result.LatencyMs) : Strings.BusinessServerSubMillisecond,
        ServerProbeStatus.Unreachable or ServerProbeStatus.Timeout or ServerProbeStatus.TlsFailed => Strings.BusinessServerNoReply,
        ServerProbeStatus.Implausible => Strings.BusinessServerInconclusive,
        _ => Strings.BusinessServerNotChecked
    };

    internal void Update(ServerProbeResult result, bool checking = false)
    {
        _result = result;
        _checking = checking;
        OnPropertyChanged(nameof(IsGood));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(IsBad));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PingText));
    }
}
