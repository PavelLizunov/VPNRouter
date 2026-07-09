using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.App.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using CoreStrings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// ViewModel for a single VLESS server entry.
/// Preserves the full VlessServerEntry so TLS, Transport, and Reality
/// configs survive SaveSettings → Load round-trips.
/// </summary>
public partial class ServerViewModel : ViewModelBase
{
    // Keep the original entry for fields not exposed in UI (TLS, Transport, etc.)
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

    /// <summary>True when this server is the one VPN is currently connected through.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>
    /// v2.32 (r10, F-C) — True if this entry lives in <c>vless.servers[]</c>
    /// but is NOT part of any active subscription. Surfaces an "Not in
    /// subscription" badge in ServersPage so the user can spot legacy
    /// manual entries that survived the F-B migration cleanup (e.g. user
    /// manually re-added an entry after migration, or migration didn't
    /// fire yet on a brand-new install).
    /// </summary>
    [ObservableProperty] private bool _isOrphanFromSubscription;

    /// <summary>
    /// r8 #6: true when a real UDP-capable sibling (Hysteria2 / TUIC) exists for
    /// this naive entry — set by <see cref="RefreshUdpSiblingFlags"/> via the same
    /// <see cref="VPNRouter.Core.Services.NaivePairing"/> logic config-gen uses, so
    /// the subtitle shows "naive + hy2" only when the generator would actually pair.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostSubtitle))]
    [NotifyPropertyChangedFor(nameof(ProtocolUseCase))]
    [NotifyPropertyChangedFor(nameof(ProtocolUseCaseTooltip))]
    private bool _hasUdpSibling;

    // ── Connectivity test state (v2.15.2) ────────────────────────────────

    /// <summary>Last TCP+TLS probe outcome. Unknown = never tested.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PingDisplay))]
    [NotifyPropertyChangedFor(nameof(StatusDot))]
    [NotifyPropertyChangedFor(nameof(StatusDotBrush))]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    private ServerProbeStatus _testStatus = ServerProbeStatus.Unknown;

    /// <summary>Measured round-trip latency (ms) from the last probe. 0 = never tested.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PingDisplay))]
    private int _pingMs;

    /// <summary>Human-readable error from last probe (null = no error / success).</summary>
    [ObservableProperty] private string? _testError;

    /// <summary>True while a Test operation is running for this server — disables the per-row button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDot))]
    [NotifyPropertyChangedFor(nameof(StatusDotBrush))]
    private bool _isTesting;

    // ── Computed display properties ──────────────────────────────────────

    /// <summary>v2.30.7-r2 — accessible name for UIA/screen readers
    /// (was leaking the fully-qualified class name "VPNRouter.App.ViewModels.ServerViewModel").
    /// Gives the row a meaningful identification for keyboard navigation +
    /// automation tools.</summary>
    public override string ToString()
    {
        var portStr = Port > 0 ? $":{Port}" : string.Empty;
        if (!string.IsNullOrEmpty(Name)) return $"{Name} ({Server}{portStr})";
        return $"{Server}{portStr}";
    }

    /// <summary>True once any probe has completed (TestStatus != Unknown).</summary>
    public bool HasTestResult => TestStatus != ServerProbeStatus.Unknown;

    /// <summary>
    /// Compact ping text for the list column: "42 ms", "—" when not tested,
    /// "×" when unreachable/timeout.
    /// v2.31.6-r16: <see cref="ServerProbeStatus.SkippedNotApplicable"/>
    /// renders as a neutral "—" — quick TCP+TLS probe doesn't apply to
    /// this protocol (e.g. unknown), so we don't claim a result.
    /// </summary>
    public string PingDisplay => TestStatus switch
    {
        ServerProbeStatus.Unknown                          => "—",
        ServerProbeStatus.SkippedNotApplicable             => "—",
        ServerProbeStatus.Unreachable or ServerProbeStatus.Timeout => "×",
        ServerProbeStatus.TlsFailed                        => "TLS ×",
        ServerProbeStatus.Implausible                      => "<5 ms",
        _                                                  => PingMs > 0 ? $"{PingMs} ms" : "—"
    };

    /// <summary>One-character status dot (filled/hollow) or testing spinner text.</summary>
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

    /// <summary>Brush for <see cref="StatusDot"/>. Resolves from the token
    /// dictionary (Tokens.axaml) so the dot adapts to theme variant in v2.16.5.
    /// v2.31.6-r16: SkippedNotApplicable maps to TextMutedBrush — same visual
    /// treatment as Unknown, signalling "we didn't actually test this".</summary>
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

    /// <summary>
    /// Called by the parent MainWindowViewModel when the theme variant
    /// changes so the list-row repaints with the palette-adjusted dot colour.
    /// </summary>
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

    /// <summary>Apply a probe result to this VM (updates PingMs, Status, Error, clears IsTesting).</summary>
    public void ApplyProbeResult(ServerProbeResult result)
    {
        IsTesting = false;
        TestStatus = result.Status;
        PingMs = result.LatencyMs;
        TestError = result.Error;
        RecomputeHealthVerdict();
    }

    // ── Phased health verdict (urltest R2, audit batch-1 #3) ─────────────
    // The audit's wording rule: never render "works" off ping/TCP alone. The
    // quick-probe + deep-verify outcomes are folded through the pure
    // mapper/classifier into ONE honest verdict line per row.

    /// <summary>Merged health verdict from the quick probe + deep verify phases.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthVerdictText))]
    [NotifyPropertyChangedFor(nameof(HealthTooltip))]
    [NotifyPropertyChangedFor(nameof(HasHealthVerdict))]
    private ServerHealthVerdict _healthVerdict = ServerHealthVerdict.Unknown;

    /// <summary>Deep-verify contribution to the phases (kept so a later quick probe re-merges).</summary>
    private ServerHealthPhases _deepPhases = new();

    /// <summary>Hide the verdict line until at least one probe produced a signal.</summary>
    public bool HasHealthVerdict => HealthVerdict != ServerHealthVerdict.Unknown;

    /// <summary>Row verdict label (RU/EN), e.g. "TCP открыт, VPN-протокол не проверен".</summary>
    public string HealthVerdictText => CoreStrings.HealthVerdictLabel(HealthVerdict);

    /// <summary>
    /// Rich tooltip: verdict + the audit's RU-block / canary explanation where the
    /// verdict warrants it + the raw probe errors for diagnostics.
    /// </summary>
    public string HealthTooltip
    {
        get
        {
            var sb = new System.Text.StringBuilder(CoreStrings.HealthVerdictLabel(HealthVerdict));
            if (HealthVerdict == ServerHealthVerdict.ProtocolHandshakeBlockedLikely)
                sb.Append("\n\n").Append(CoreStrings.HealthRuBlockWarning);
            else if (HealthVerdict == ServerHealthVerdict.OnlyControlWorks)
                sb.Append("\n\n").Append(CoreStrings.HealthCanaryFailedWarning);
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
        // Verdict may be unchanged while the underlying error text moved — the
        // tooltip must still refresh.
        OnPropertyChanged(nameof(HealthTooltip));
    }

    // ── Deep verify state (v2.15.3) ──────────────────────────────────────

    /// <summary>True while a deep-verify (sing-box + HTTP) probe is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepDisplay))]
    private bool _isDeepTesting;

    /// <summary>True if the last deep-verify pass completed successfully (HTTP trace through proxy).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepDisplay))]
    [NotifyPropertyChangedFor(nameof(HasDeepResult))]
    private bool _isDeepVerified;

    /// <summary>True if the last deep-verify failed. Mutually exclusive with IsDeepVerified.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepDisplay))]
    [NotifyPropertyChangedFor(nameof(HasDeepResult))]
    private bool _isDeepFailed;

    /// <summary>HTTP latency through the spawned sing-box SOCKS proxy (ms).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepDisplay))]
    private int _httpLatencyMs;

    /// <summary>Measured download throughput through proxy (Mbps). 0 = not measured.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepDisplay))]
    private int _bandwidthMbps;

    /// <summary>Deep verify failure reason (null on success).</summary>
    [ObservableProperty] private string? _deepError;

    public bool HasDeepResult => IsDeepVerified || IsDeepFailed;

    /// <summary>Compact one-line deep-verify summary for the list column.</summary>
    public string DeepDisplay
    {
        get
        {
            if (IsDeepTesting) return "⏳";
            if (IsDeepFailed) return "✗";
            if (IsDeepInconclusive) return "!";   // local/unsupported — not a server verdict
            if (IsDeepVerified)
            {
                if (BandwidthMbps > 0) return $"✓ {BandwidthMbps}M";
                if (HttpLatencyMs > 0) return $"✓ {HttpLatencyMs}ms";
                return "✓";
            }
            return "—";
        }
    }

    /// <summary>
    /// True when the last deep verify neither passed nor condemned the server —
    /// a local/infra failure (our sing-box broke) or the verifier can't test this
    /// protocol on this build. R2: these must NOT render as a server "✗".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeepDisplay))]
    private bool _isDeepInconclusive;

    /// <summary>Apply a deep-verify outcome.</summary>
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
            // Single source of truth: only a mapper-confirmed server-meaningful
            // failure (ProxiedHttpControl=Fail) condemns the server; local-infra
            // and unsupported-by-verifier outcomes are inconclusive.
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
        Name = entry.Name;
        Server = entry.Server;
        Port = entry.Port;
        Uuid = entry.Uuid;
        Flow = entry.Flow;
        Security = entry.Security;

        // Pick fields based on security type, not object nullity
        // (YamlDotNet creates empty objects even when YAML has no values)
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

    /// <summary>
    /// Convert back to VlessServerEntry, preserving TLS/Transport/Reality
    /// that the UI doesn't edit directly.
    /// </summary>
    public VlessServerEntry ToEntry()
    {
        // Start from original entry to preserve TLS, Transport, etc.
        var entry = _originalEntry ?? new VlessServerEntry();

        // Apply UI-editable fields
        entry.Name = Name;
        entry.Server = Server;
        entry.Port = Port;
        entry.Uuid = Uuid;
        entry.Flow = Flow;
        entry.Security = Security;

        // Update Reality from UI fields
        if (Security?.Equals("reality", StringComparison.OrdinalIgnoreCase) == true)
        {
            entry.Reality ??= new VlessRealityConfig();
            entry.Reality.Enabled = true;
            entry.Reality.ServerName = ServerName;
            entry.Reality.Fingerprint = Fingerprint;
            entry.Reality.PublicKey = PublicKey;
            entry.Reality.ShortId = ShortId;
        }

        // Update TLS SNI from UI if TLS mode
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

    /// <summary>
    /// v2.25.3 — compact protocol/security subtitle shown in the Servers /
    /// Subscribe list design ("tcp + reality"). Helps distinguish entries
    /// with similar names (e.g. same host on multiple ports with different
    /// transports). Returns empty string for legacy entries without these
    /// fields so the XAML can hide the subtitle row when irrelevant.
    /// </summary>
    public string HostSubtitle
    {
        get
        {
            // v2.42.0-r2: detect dns-tunnel by its payload fields, not just the
            // Protocol string — Android's JSON cache can drop Protocol back to
            // "vless" while the dns fields survive, which mislabeled a working
            // dns-tunnel server as "tcp + reality". IsDnsTunnel is field-based.
            // v2.45.0-r3: same treatment for AmneziaWG — an awg entry carries an
            // Awg block (and empty Uuid / default Security="reality"), so without
            // a field check it fell through to the VLESS default and showed
            // "tcp + reality" (user-reported: "при добавлении AWG пишет что это
            // vless"). Detect by the Awg payload so the label is right even if a
            // future round-trip drops Protocol.
            var protocol = ProtocolKey;
            var parts = new System.Collections.Generic.List<string>();

            // v2.30.1-r3: for non-VLESS protocols (Hysteria2 / TUIC / SS),
            // the subtitle displays the protocol name plus its salient
            // sub-feature (obfs / plugin / cipher) instead of the
            // VLESS-specific "transport + security" pair.
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
                    // r8 #6: "naive + hy2" ONLY when a real UDP-capable sibling
                    // actually exists (HasUdpSibling, set by RefreshUdpSiblingFlags
                    // via the same NaivePairing logic config-gen uses) — so the
                    // label can't claim a pairing the generator wouldn't make.
                    // Else "naive" (was mislabeled "tcp + reality" pre-r4: naive
                    // carries default Security/Transport fields the outbound ignores).
                    parts.Add(HasUdpSibling ? "naive + hy2" : "naive");
                    break;

                case "dns-tunnel":
                    // DNS-tunnel (slipstream) — VLESS tunnelled over DNS. The
                    // generated outbound is plain VLESS to a local port, but the
                    // subtitle must show the real transport so users can tell this
                    // last-resort server apart from a normal one.
                    parts.Add("dns-tunnel");
                    break;

                case "amneziawg":
                case "awg":
                    // AmneziaWG (WireGuard + obfuscation). Show the protocol plus
                    // a hint that obfuscation is active when junk-packet params
                    // are set, so it reads distinctly from a plain VLESS row.
                    parts.Add("amneziawg");
                    if (_originalEntry?.Awg != null && _originalEntry.Awg.Jc > 0)
                        parts.Add("obfs");
                    break;

                default:
                    // VLESS — keep original "transport + security" format
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

    /// <summary>
    /// r8 #6: set <see cref="HasUdpSibling"/> on every naive VM in the collection
    /// by asking <see cref="VPNRouter.Core.Services.NaivePairing"/> (the same logic
    /// ConfigGenerator uses) whether a UDP-capable sibling actually exists in the
    /// pool. Call after (re)building a Servers / SubscriptionServers collection.
    /// </summary>
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
