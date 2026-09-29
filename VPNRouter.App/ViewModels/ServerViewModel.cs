using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.App.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using CoreStrings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.ViewModels;

public partial class ServerViewModel : ViewModelBase
{
    private VlessServerEntry _originalEntry;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _server = string.Empty;
    [ObservableProperty] private int _port = 443;
    [ObservableProperty] private string _uuid = string.Empty;
    [ObservableProperty] private string _flow = "xtls-rprx-vision";
    [ObservableProperty] private string _security = "reality";
    [ObservableProperty] private string _serverName = "yahoo.com";
    [ObservableProperty] private string _fingerprint = "firefox";
    [ObservableProperty] private string _publicKey = string.Empty;
    [ObservableProperty] private string _shortId = string.Empty;
    [ObservableProperty] private bool _isSelected;

    [ObservableProperty] private bool _isActive;

    [ObservableProperty] private bool _isOrphanFromSubscription;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostSubtitle))]
    [NotifyPropertyChangedFor(nameof(ProtocolUseCase))]
    [NotifyPropertyChangedFor(nameof(ProtocolUseCaseTooltip))]
    private bool _hasUdpSibling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PingDisplay))]
    [NotifyPropertyChangedFor(nameof(StatusDot))]
    [NotifyPropertyChangedFor(nameof(StatusDotBrush))]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    private ServerProbeStatus _testStatus = ServerProbeStatus.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PingDisplay))]
    private int _pingMs;

    [ObservableProperty] private string? _testError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDot))]
    [NotifyPropertyChangedFor(nameof(StatusDotBrush))]
    private bool _isTesting;

    public override string ToString()
    {
        var portStr = Port > 0 ? $":{Port}" : string.Empty;
        if (!string.IsNullOrEmpty(Name)) return $"{Name} ({Server}{portStr})";
        return $"{Server}{portStr}";
    }

    public bool HasTestResult => TestStatus != ServerProbeStatus.Unknown;

    public string PingDisplay => TestStatus switch
    {
        ServerProbeStatus.Unknown                          => "—",
        ServerProbeStatus.SkippedNotApplicable             => "—",
        ServerProbeStatus.Unreachable or ServerProbeStatus.Timeout => "×",
        ServerProbeStatus.TlsFailed                        => "TLS ×",
        ServerProbeStatus.Implausible                      => "<5 ms",
        _                                                  => PingMs > 0 ? $"{PingMs} ms" : "—"
    };

    public string StatusDot => IsTesting ? "…" : TestStatus switch
    {
        ServerProbeStatus.Ok                   => "●",
        ServerProbeStatus.Slow                 => "●",
        ServerProbeStatus.Unreachable          => "●",
        ServerProbeStatus.Timeout              => "●",
        ServerProbeStatus.TlsFailed            => "●",
        ServerProbeStatus.Implausible          => "●",
        ServerProbeStatus.SkippedNotApplicable => "○",
        _                                      => "○"
    };

    public IBrush StatusDotBrush
    {
        get
        {
            var key = TestStatus switch
            {
                ServerProbeStatus.Ok                   => "SuccessSolidBrush",
                ServerProbeStatus.Slow                 => "WarningSolidBrush",
                ServerProbeStatus.Implausible          => "WarningSolidBrush",
                ServerProbeStatus.TlsFailed            => "DangerSolidBrush",
                ServerProbeStatus.Unreachable          => "DangerSolidBrush",
                ServerProbeStatus.Timeout              => "DangerSolidBrush",
                ServerProbeStatus.SkippedNotApplicable => "TextMutedBrush",
                _                                      => "TextMutedBrush"
            };
            return LookupBrush(key) ?? new SolidColorBrush(Color.FromRgb(0x94, 0xA0, 0xB2));
        }
    }

    private static IBrush? LookupBrush(string key)
    {
        var app = Avalonia.Application.Current;
        if (app != null &&
            app.Resources.TryGetResource(key, app.ActualThemeVariant, out var res) &&
            res is IBrush brush)
        {
            return brush;
        }
        return null;
    }

    public void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(StatusDotBrush));
    }

    public void NotifyLocalizationChanged()
    {
        OnPropertyChanged(nameof(ProtocolUseCase));
        OnPropertyChanged(nameof(ProtocolUseCaseTooltip));
        OnPropertyChanged(nameof(HealthVerdictText));
        OnPropertyChanged(nameof(HealthTooltip));
    }

    public void ApplyProbeResult(ServerProbeResult result)
    {
        IsTesting = false;
        TestStatus = result.Status;
        PingMs = result.LatencyMs;
        TestError = result.Error;
        RecomputeHealthVerdict();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthVerdictText))]
    [NotifyPropertyChangedFor(nameof(HealthTooltip))]
    [NotifyPropertyChangedFor(nameof(HasHealthVerdict))]
    private ServerHealthVerdict _healthVerdict = ServerHealthVerdict.Unknown;

    private ServerHealthPhases _deepPhases = new();

    public bool HasHealthVerdict => HealthVerdict != ServerHealthVerdict.Unknown;

    public string HealthVerdictText => CoreStrings.HealthVerdictLabel(HealthVerdict);

    public string HealthTooltip
    {
        get
        {
            var sb = new System.Text.StringBuilder(CoreStrings.HealthVerdictLabel(HealthVerdict));
            if (HealthVerdict == ServerHealthVerdict.ProtocolHandshakeBlockedLikely)
                sb.Append("\n\n").Append(CoreStrings.HealthRuBlockWarning);
            else if (HealthVerdict == ServerHealthVerdict.OnlyControlWorks)
                sb.Append("\n\n").Append(CoreStrings.HealthCanaryFailedWarning);
            if (IsProviderHighRisk)
                sb.Append("\n\n").Append(CoreStrings.HealthProviderHighRisk);
            var rec = _originalEntry != null ? ServerHealthStore.GetFreshRecord(_originalEntry) : null;
            if (rec != null)
                sb.Append('\n').Append(CoreStrings.HealthCheckedAgo(DateTimeOffset.UtcNow - rec.RecordedAt));
            if (!string.IsNullOrEmpty(DeepError)) sb.Append('\n').Append(DeepError);
            if (!string.IsNullOrEmpty(TestError)) sb.Append('\n').Append(TestError);
            return sb.ToString();
        }
    }

    private void RecomputeHealthVerdict()
    {
        var quick = ServerHealthPhaseMapper.FromQuickProbe(TestStatus);
        HealthVerdict = ServerHealthClassifier
            .Classify(ServerHealthPhaseMapper.Merge(quick, _deepPhases))
            .Verdict;
        if (_originalEntry != null)
        {
            var entry = _originalEntry;
            var verdict = HealthVerdict;
            var key = ProviderKey.ForIp(entry.Server);
            ServerHealthStore.Record(entry, verdict, providerKey: key);
            if (key is null && !string.IsNullOrWhiteSpace(entry.Server) && verdict != ServerHealthVerdict.Unknown)
            {
                _ = Task.Run(async () =>
                {
                    var resolved = await ProviderKey.ResolveAsync(entry.Server).ConfigureAwait(false);
                    if (resolved == null) return;
                    var cur = ServerHealthStore.GetFreshRecord(entry);
                    if (cur != null)
                        ServerHealthStore.Record(entry, cur.Verdict, cur.RecordedAt, resolved);
                    else
                        ServerHealthStore.Record(entry, verdict, providerKey: resolved);
                });
            }
        }
        OnPropertyChanged(nameof(HealthTooltip));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthTooltip))]
    private bool _isProviderHighRisk;

    public static void RefreshProviderRiskFlags(System.Collections.Generic.IEnumerable<ServerViewModel> vms)
    {
        var list = new System.Collections.Generic.List<ServerViewModel>(vms);
        var withRecs = new System.Collections.Generic.List<(ServerViewModel Vm, ServerHealthRecordDto? Rec)>(list.Count);
        foreach (var vm in list)
        {
            ServerHealthRecordDto? rec = null;
            try { if (vm._originalEntry != null) rec = ServerHealthStore.GetFreshRecord(vm._originalEntry); }
            catch { }
            withRecs.Add((vm, rec));
        }

        var grouped = new System.Collections.Generic.List<(string, ServerHealthVerdict)>();
        foreach (var (_, rec) in withRecs)
            if (!string.IsNullOrEmpty(rec?.ProviderKey))
                grouped.Add((rec!.ProviderKey!, rec.Verdict));

        var highRisk = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var risk in ServerHealthClassifier.AnalyzeProviderRisk(grouped))
            if (risk.HighRisk) highRisk.Add(risk.Asn);

        foreach (var (vm, rec) in withRecs)
            vm.IsProviderHighRisk = rec?.ProviderKey != null && highRisk.Contains(rec.ProviderKey);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepDisplay))]
    private bool _isDeepTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepDisplay))]
    [NotifyPropertyChangedFor(nameof(HasDeepResult))]
    private bool _isDeepVerified;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepDisplay))]
    [NotifyPropertyChangedFor(nameof(HasDeepResult))]
    private bool _isDeepFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepDisplay))]
    private int _httpLatencyMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepDisplay))]
    private int _bandwidthMbps;

    [ObservableProperty] private string? _deepError;

    public bool HasDeepResult => IsDeepVerified || IsDeepFailed;

    public string DeepDisplay
    {
        get
        {
            if (IsDeepTesting) return "⏳";
            if (IsDeepFailed) return "✗";
            if (IsDeepInconclusive) return "!";
            if (IsDeepVerified)
            {
                if (BandwidthMbps > 0) return $"✓ {BandwidthMbps}M";
                if (HttpLatencyMs > 0) return $"✓ {HttpLatencyMs}ms";
                return "✓";
            }
            return "—";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepDisplay))]
    private bool _isDeepInconclusive;

    public void ApplyDeepResult(DeepVerifyResult result)
    {
        IsDeepTesting = false;
        _deepPhases = ServerHealthPhaseMapper.FromDeepVerify(result);
        if (result.Ok)
        {
            IsDeepVerified = true;
            IsDeepFailed = false;
            IsDeepInconclusive = false;
            HttpLatencyMs = result.HttpLatencyMs;
            BandwidthMbps = result.BandwidthMbps.HasValue ? (int)Math.Round(result.BandwidthMbps.Value) : 0;
            DeepError = null;
        }
        else
        {
            IsDeepVerified = false;
            var condemning = _deepPhases.ProxiedHttpControl == PhaseOutcome.Fail;
            IsDeepFailed = condemning;
            IsDeepInconclusive = !condemning;
            DeepError = result.Error;
        }
        RecomputeHealthVerdict();
    }

    public ServerViewModel()
    {
        _originalEntry = new VlessServerEntry();
    }

    public ServerViewModel(VlessServerEntry entry)
    {
        _originalEntry = entry;
        try
        {
            var persisted = ServerHealthStore.GetFresh(entry);
            if (persisted.HasValue) _healthVerdict = persisted.Value;
        }
        catch { }
        Name = entry.Name;
        Server = entry.Server;
        Port = entry.Port;
        Uuid = entry.Uuid;
        Flow = entry.Flow;
        Security = entry.Security;

        var isReality = Security?.Equals("reality", StringComparison.OrdinalIgnoreCase) == true;

        if (isReality && entry.Reality != null)
        {
            ServerName = entry.Reality.ServerName ?? "yahoo.com";
            Fingerprint = entry.Reality.Fingerprint ?? "firefox";
            PublicKey = entry.Reality.PublicKey ?? "";
            ShortId = entry.Reality.ShortId ?? "";
        }
        else if (entry.Tls != null)
        {
            ServerName = entry.Tls.ServerName ?? entry.Server;
            Fingerprint = entry.Tls.Fingerprint ?? "";
        }
    }

    public VlessServerEntry ToEntry()
    {
        var entry = _originalEntry ?? new VlessServerEntry();

        entry.Name = Name;
        entry.Server = Server;
        entry.Port = Port;
        entry.Uuid = Uuid;
        entry.Flow = Flow;
        entry.Security = Security;

        if (Security?.Equals("reality", StringComparison.OrdinalIgnoreCase) == true)
        {
            entry.Reality ??= new VlessRealityConfig();
            entry.Reality.Enabled = true;
            entry.Reality.ServerName = ServerName;
            entry.Reality.Fingerprint = Fingerprint;
            entry.Reality.PublicKey = PublicKey;
            entry.Reality.ShortId = ShortId;
        }

        if (Security?.Equals("tls", StringComparison.OrdinalIgnoreCase) == true)
        {
            entry.Tls ??= new VlessTlsConfig();
            entry.Tls.Enabled = true;
            entry.Tls.ServerName = ServerName;
        }

        return entry;
    }

    public string DisplayName => string.IsNullOrEmpty(Name) ? Server : Name;

    private string ProtocolKey => (_originalEntry?.Awg != null)
        ? "amneziawg"
        : (_originalEntry?.IsDnsTunnel == true)
            ? "dns-tunnel"
            : (_originalEntry?.Protocol ?? "vless").ToLowerInvariant();

    public string HostSubtitle
    {
        get
        {
            var protocol = ProtocolKey;
            var parts = new System.Collections.Generic.List<string>();

            switch (protocol)
            {
                case "hysteria2":
                    parts.Add("hysteria2");
                    if (!string.IsNullOrWhiteSpace(_originalEntry?.ObfsType))
                        parts.Add(_originalEntry!.ObfsType.ToLowerInvariant());
                    break;

                case "tuic":
                    parts.Add("tuic");
                    if (!string.IsNullOrWhiteSpace(_originalEntry?.CongestionControl))
                        parts.Add(_originalEntry!.CongestionControl.ToLowerInvariant());
                    break;

                case "shadowsocks":
                case "ss":
                    parts.Add("ss");
                    if (!string.IsNullOrWhiteSpace(_originalEntry?.Method))
                        parts.Add(_originalEntry!.Method.ToLowerInvariant());
                    if (!string.IsNullOrWhiteSpace(_originalEntry?.Plugin))
                        parts.Add(_originalEntry!.Plugin.ToLowerInvariant());
                    break;

                case "naive":
                    parts.Add(HasUdpSibling ? "naive + hy2" : "naive");
                    break;

                case "dns-tunnel":
                    parts.Add("dns-tunnel");
                    break;

                case "amneziawg":
                case "awg":
                    parts.Add("amneziawg");
                    if (_originalEntry?.Awg != null && _originalEntry.Awg.Jc > 0)
                        parts.Add("obfs");
                    break;

                default:
                    var transport = _originalEntry?.Transport?.Type;
                    if (!string.IsNullOrWhiteSpace(transport))
                        parts.Add(transport!.ToLowerInvariant());
                    if (!string.IsNullOrWhiteSpace(Security) &&
                        !Security.Equals("none", System.StringComparison.OrdinalIgnoreCase))
                        parts.Add(Security.ToLowerInvariant());
                    break;
            }

            return string.Join(" + ", parts);
        }
    }

    public string ProtocolUseCase
    {
        get
        {
            var protocol = ProtocolKey;
            var transport = _originalEntry?.Transport?.Type?.ToLowerInvariant();
            return protocol switch
            {
                "hysteria2" or "hy2" or "tuic" => Strings.ProtocolUseGamesVoice,
                "naive" => HasUdpSibling ? Strings.ProtocolUseWebUdpPair : Strings.ProtocolUseWebOnly,
                "amneziawg" or "awg" => Strings.ProtocolUseLowLatency,
                "dns-tunnel" => Strings.ProtocolUseEmergency,
                "shadowsocks" or "ss" => Strings.ProtocolUseFallback,
                _ when transport is "xhttp" => Strings.ProtocolUseStealthWeb,
                _ when transport is "ws" or "grpc" => Strings.ProtocolUseWebFallback,
                _ => Strings.ProtocolUseDaily,
            };
        }
    }

    public string ProtocolUseCaseTooltip
    {
        get
        {
            var protocol = ProtocolKey;
            var transport = _originalEntry?.Transport?.Type?.ToLowerInvariant();
            return protocol switch
            {
                "hysteria2" or "hy2" or "tuic" => Strings.ProtocolUseGamesVoiceTip,
                "naive" => HasUdpSibling ? Strings.ProtocolUseWebUdpPairTip : Strings.ProtocolUseWebOnlyTip,
                "amneziawg" or "awg" => Strings.ProtocolUseLowLatencyTip,
                "dns-tunnel" => Strings.ProtocolUseEmergencyTip,
                "shadowsocks" or "ss" => Strings.ProtocolUseFallbackTip,
                _ when transport is "xhttp" => Strings.ProtocolUseStealthWebTip,
                _ when transport is "ws" or "grpc" => Strings.ProtocolUseWebFallbackTip,
                _ => Strings.ProtocolUseDailyTip,
            };
        }
    }

    public static void RefreshUdpSiblingFlags(System.Collections.Generic.IEnumerable<ServerViewModel> vms)
    {
        var list = new System.Collections.Generic.List<ServerViewModel>(vms);
        var pool = new System.Collections.Generic.List<VlessServerEntry>(list.Count);
        foreach (var v in list)
            if (v._originalEntry != null) pool.Add(v._originalEntry);
        foreach (var v in list)
            if (VPNRouter.Core.Services.NaivePairing.IsNaive(v._originalEntry))
                v.HasUdpSibling = VPNRouter.Core.Services.NaivePairing.FindUdpSibling(v._originalEntry!, pool) != null;
    }
}
