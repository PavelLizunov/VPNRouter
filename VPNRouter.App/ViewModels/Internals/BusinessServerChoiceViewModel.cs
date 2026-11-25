using VPNRouter.Core.Localization;
using VPNRouter.Core.Services;
using VPNRouter.App.Services;

namespace VPNRouter.App.ViewModels.Internals;

// Transient Business-only display state. Never saved with settings or health history.
public sealed class BusinessServerChoiceViewModel : ViewModelBase
{
    internal ServerViewModel Server { get; }
    internal bool SupportsEndToEndProbe =>
        string.Equals(Server.SourceEntry.Protocol, "dnstt", StringComparison.OrdinalIgnoreCase)
            ? Server.SourceEntry.Dnstt != null && SingBoxFeatures.DnsttAvailable
            : !Server.SourceEntry.IsDnsTunnel;
    public string DisplayName => Server.SourceEntry.Dnstt != null &&
        !Server.DisplayName.Contains("DNSTT", StringComparison.OrdinalIgnoreCase)
        ? $"{Server.DisplayName} · DNSTT" : Server.DisplayName;
    private BusinessNodeSnapshot? _metadataSnapshot;
    private BusinessNodeMetadata? _metadata;
    public bool HasMetadata { get; private set; }
    public string CountryCode => _metadata != null ? _metadata.CountryIso ?? string.Empty
        : Server.SourceEntry.Dnstt == null ? BusinessServerCountry.Code(Server.Name) : string.Empty;
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
    public bool IsSlow => !_checking && _result.Status == ServerProbeStatus.Slow;
    public bool IsBad => !_checking && _result.Status is ServerProbeStatus.Unreachable or ServerProbeStatus.Timeout or ServerProbeStatus.TlsFailed;
    internal ServerProbeStatus ProbeStatus => _result.Status;
    internal bool IsChecking => _checking;
    public string SlowBadgeText => Strings.BusinessServerSlowShort;

    public string StatusText => _checking ? Strings.BusinessServerChecking
        : !SupportsEndToEndProbe ? Strings.BusinessServerUnsupportedByCheck
        : _result.Status switch
    {
        ServerProbeStatus.Ok => Strings.BusinessServerReachable,
        ServerProbeStatus.Slow => Strings.BusinessServerSlow,
        ServerProbeStatus.Unreachable or ServerProbeStatus.Timeout or ServerProbeStatus.TlsFailed => Strings.BusinessServerNoReply,
        ServerProbeStatus.Implausible => Strings.BusinessServerInconclusive,
        _ => Strings.BusinessServerNotChecked
    };

    public string PingText => _checking ? Strings.BusinessServerChecking
        : !SupportsEndToEndProbe ? Strings.BusinessServerUnsupportedByCheck
        : _result.Status switch
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
        OnPropertyChanged(nameof(SlowBadgeText));
        RefreshMetadata();
    }

    internal void UpdateMetadata(BusinessNodeSnapshot? snapshot, BusinessNodeMetadata? row)
    {
        HasMetadata = true;
        _metadataSnapshot = snapshot;
        _metadata = snapshot == null ? null : row;
        RefreshMetadata();
    }

    internal void UpdateMetadata(BusinessNodeSnapshot? snapshot)
    {
        BusinessNodeMetadata? row = null;
        if (snapshot != null)
        {
            if (Server.SourceEntry.Dnstt is { } dnstt) row = snapshot.FindDnsttNode(dnstt.Domain);
            else if (snapshot.TryGetNode(Server.Server, out var endpoint)) row = endpoint;
        }
        UpdateMetadata(snapshot, row);
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
        OnPropertyChanged(nameof(IsSlow));
        OnPropertyChanged(nameof(IsBad));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PingText));
    }
}
