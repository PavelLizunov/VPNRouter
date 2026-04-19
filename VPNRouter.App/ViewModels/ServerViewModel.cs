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

    /// <summary>True once any probe has completed (TestStatus != Unknown).</summary>
    public bool HasTestResult => TestStatus != ServerProbeStatus.Unknown;

    /// <summary>
    /// Compact ping text for the list column: "42 ms", "—" when not tested,
    /// "×" when unreachable/timeout.
    /// </summary>
    public string PingDisplay => TestStatus switch
    {
        ServerProbeStatus.Unknown                          => "—",
        ServerProbeStatus.Unreachable or ServerProbeStatus.Timeout => "×",
        ServerProbeStatus.TlsFailed                        => "TLS ×",
        ServerProbeStatus.Implausible                      => "<5 ms",
        _                                                  => PingMs > 0 ? $"{PingMs} ms" : "—"
    };

    /// <summary>One-character status dot (🟢/🟡/🔴) or testing spinner text.</summary>
    public string StatusDot => IsTesting ? "…" : TestStatus switch
    {
        ServerProbeStatus.Ok           => "●",
        ServerProbeStatus.Slow         => "●",
        ServerProbeStatus.Unreachable  => "●",
        ServerProbeStatus.Timeout      => "●",
        ServerProbeStatus.TlsFailed    => "●",
        ServerProbeStatus.Implausible  => "●",
        _                              => "○"
    };

    /// <summary>Brush for <see cref="StatusDot"/>.</summary>
    public IBrush StatusDotBrush => TestStatus switch
    {
        ServerProbeStatus.Ok           => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),  // emerald
        ServerProbeStatus.Slow         => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),  // amber
        ServerProbeStatus.TlsFailed    => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),  // red
        ServerProbeStatus.Unreachable  => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        ServerProbeStatus.Timeout      => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        ServerProbeStatus.Implausible  => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        _                              => new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF))   // gray
    };

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
}
