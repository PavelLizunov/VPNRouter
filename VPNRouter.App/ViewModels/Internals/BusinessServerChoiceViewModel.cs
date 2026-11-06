using VPNRouter.Core.Localization;
using VPNRouter.Core.Services;
using VPNRouter.App.Services;

namespace VPNRouter.App.ViewModels.Internals;

// Transient Business-only display state. Never saved with settings or health history.
public sealed class BusinessServerChoiceViewModel : ViewModelBase
{
    internal ServerViewModel Server { get; }
    public string DisplayName => Server.DisplayName;
    private BusinessNodeSnapshot? _metadataSnapshot;
    private BusinessNodeMetadata? _metadata;
    public bool HasMetadata { get; private set; }
    public string CountryCode => _metadata != null ? _metadata.CountryIso ?? string.Empty
        : BusinessServerCountry.Code(Server.Name);
    public string CountryName => Strings.BusinessCountryName(CountryCode);
    public bool HasCountry => CountryCode.Length != 0;
    public Avalonia.Media.IImage? CountryFlag => BusinessServerCountry.Flag(CountryCode);
    internal BusinessNodeActivityLevel ActivityLevel => HasFreshMetadata
        ? _metadata!.ActivityLevel : BusinessNodeActivityLevel.Unknown;
    private bool HasFreshMetadata => _metadataSnapshot != null && _metadata != null &&
        _metadataSnapshot.GetFreshness(_metadata) == BusinessNodeFreshness.Fresh;
    private ServerProbeResult _result = ServerProbeResult.Unknown;
    private bool _checking;

    public bool IsActivityLow => ActivityLevel == BusinessNodeActivityLevel.Low;
    public bool IsActivityMedium => ActivityLevel == BusinessNodeActivityLevel.Medium;
    public bool IsActivityHigh => ActivityLevel == BusinessNodeActivityLevel.High;
    public string MetricsText => ActivityLevel switch
    {
        BusinessNodeActivityLevel.Low => Strings.BusinessNodeActivityLow,
        BusinessNodeActivityLevel.Medium => Strings.BusinessNodeActivityMedium,
        BusinessNodeActivityLevel.High => Strings.BusinessNodeActivityHigh,
        _ => _metadataSnapshot != null && _metadata != null &&
            _metadataSnapshot.GetFreshness(_metadata) == BusinessNodeFreshness.Stale
                ? Strings.BusinessNodeMetricsStale : Strings.BusinessNodeMetricsUnavailable
    };

    public string MetricsHint => Strings.BusinessNodeMetricsHint(
        _metadataSnapshot != null && _metadata != null ? _metadataSnapshot.GetAgeSeconds(_metadata) : null,
        _metadataSnapshot != null && _metadata != null &&
            _metadataSnapshot.GetFreshness(_metadata) == BusinessNodeFreshness.Stale);

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

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(CountryName));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PingText));
        RefreshMetadata();
    }

    internal void UpdateMetadata(BusinessNodeSnapshot? snapshot, BusinessNodeMetadata? row)
    {
        HasMetadata = true;
        _metadataSnapshot = snapshot;
        _metadata = snapshot == null ? null : row;
        RefreshMetadata();
    }

    private void RefreshMetadata()
    {
        OnPropertyChanged(nameof(HasMetadata));
        OnPropertyChanged(nameof(CountryCode));
        OnPropertyChanged(nameof(CountryName));
        OnPropertyChanged(nameof(CountryFlag));
        OnPropertyChanged(nameof(HasCountry));
        OnPropertyChanged(nameof(MetricsText));
        OnPropertyChanged(nameof(MetricsHint));
        OnPropertyChanged(nameof(IsActivityLow));
        OnPropertyChanged(nameof(IsActivityMedium));
        OnPropertyChanged(nameof(IsActivityHigh));
    }

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
