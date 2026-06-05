using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

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

    /// <summary>Apply a probe result to this VM (updates PingMs, Status, Error, clears IsTesting).</summary>
    public void ApplyProbeResult(ServerProbeResult result)
    {
        IsTesting = false;
        TestStatus = result.Status;
        PingMs = result.LatencyMs;
        TestError = result.Error;
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
            if (IsDeepVerified)
            {
                if (BandwidthMbps > 0) return $"✓ {BandwidthMbps}M";
                if (HttpLatencyMs > 0) return $"✓ {HttpLatencyMs}ms";
                return "✓";
            }
            return "—";
        }
    }

    /// <summary>Apply a deep-verify outcome.</summary>
    public void ApplyDeepResult(DeepVerifyResult result)
    {
        IsDeepTesting = false;
        if (result.Ok)
        {
            IsDeepVerified = true;
            IsDeepFailed = false;
            HttpLatencyMs = result.HttpLatencyMs;
            BandwidthMbps = result.BandwidthMbps.HasValue ? (int)Math.Round(result.BandwidthMbps.Value) : 0;
            DeepError = null;
        }
        else
        {
            IsDeepVerified = false;
            IsDeepFailed = true;
            DeepError = result.Error;
        }
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
            var protocol = (_originalEntry?.Protocol ?? "vless").ToLowerInvariant();
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
                    // v2.41.1-r4: NaiveProxy (HTTP/2 over TLS via Cronet). The
                    // entry carries default Security="reality"/Transport="tcp"
                    // fields that BuildNaiveOutbound ignores — without this case
                    // the subtitle would mislabel naive as "tcp + reality".
                    parts.Add("naive");
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
}
