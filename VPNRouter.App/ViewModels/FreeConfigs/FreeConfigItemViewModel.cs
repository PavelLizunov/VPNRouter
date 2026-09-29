using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.App.Localization;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.App.ViewModels.FreeConfigs;

public partial class FreeConfigItemViewModel : ObservableObject
{
    public FreeConfigEntry Entry { get; }

    public FreeConfigItemViewModel(FreeConfigEntry entry)
    {
        Entry = entry;
    }

    [ObservableProperty] private bool _isRecheckRunning;

    public string Id          => Entry.Id;
    public string Endpoint    => $"{Entry.Host}:{Entry.Port}";
    public string Sni         => Entry.Sni ?? "";
    public string Transport   => Entry.Transport;
    public string Security    => Entry.Security;

    public override string ToString()
    {
        var country = string.IsNullOrEmpty(Entry.CountryCode) ? string.Empty : $"{Entry.CountryCode} ";
        return $"{country}{Endpoint} ({LatencyDisplay})";
    }

    public string CountryCode => string.IsNullOrEmpty(Entry.CountryCode) ? "—" : Entry.CountryCode;

    public string CountryFlag => FlagFor(Entry.CountryCode);

    public string CountryDisplay => string.IsNullOrEmpty(Entry.CountryCode)
        ? "—"
        : $"{FlagFor(Entry.CountryCode)} {Entry.CountryCode}";

    public string BandwidthDisplay => Entry.MeasuredBandwidthMbps.HasValue
        ? $"{Entry.MeasuredBandwidthMbps} Mbps"
        : "—";

    public string LatencyDisplay => Entry.Status switch
    {
        FreeConfigStatus.Verified when Entry.LatencyMs <= 0 => "— ✓✓",
        FreeConfigStatus.Verified    => $"{Entry.LatencyMs} ms ✓✓",
        FreeConfigStatus.Ok when Entry.LatencyMs <= 0       => "— ✓",
        FreeConfigStatus.Ok          => $"{Entry.LatencyMs} ms ✓",
        FreeConfigStatus.Slow        => $"{Entry.LatencyMs} ms slow",
        FreeConfigStatus.Implausible => "fake (<5ms)",
        FreeConfigStatus.TlsFailed   => "TLS failed",
        FreeConfigStatus.Timeout     => "timeout",
        FreeConfigStatus.Unreachable => "unreachable",
        FreeConfigStatus.ParseError  => "parse error",
        _                             => "—",
    };

    public int LatencySortKey => SortKeyFor(Entry);

    public static int SortKeyFor(FreeConfigEntry entry) => entry.Status switch
    {
        FreeConfigStatus.Verified when entry.LatencyMs <= 0 => 90_000,
        FreeConfigStatus.Verified                     => entry.LatencyMs,
        FreeConfigStatus.Ok when entry.LatencyMs > 0 => entry.LatencyMs + 100_000,
        FreeConfigStatus.Slow                         => entry.LatencyMs + 200_000,
        FreeConfigStatus.Implausible                  => 400_000,
        FreeConfigStatus.TlsFailed                    => 500_000,
        FreeConfigStatus.Timeout                      => 1_000_000,
        FreeConfigStatus.Unreachable                  => 1_000_001,
        _                                              => 999_999,
    };

    public bool IsWorking => Entry.Status == FreeConfigStatus.Ok;

    public bool HasFailedLastCheck => FreeConfigFreshness.HasFailedLastCheck(Entry);

    public bool IsStale => FreeConfigFreshness.IsStale(Entry, DateTime.UtcNow);

    public string FreshnessLabel
    {
        get
        {
            var now = DateTime.UtcNow;
            return FreeConfigFreshness.ClassifyTier(Entry, now) switch
            {
                FreeConfigFreshnessTier.Failed => Strings.FcFreshnessFailed,
                FreeConfigFreshnessTier.Stale  => Strings.FcFreshnessStale,
                FreeConfigFreshnessTier.Ageing => Strings.FcFreshnessAgeingDays(
                    FreeConfigFreshness.AgeDays(Entry, now)),
                _ => Strings.FcFreshnessFresh,
            };
        }
    }

    public double OpacityValue =>
        FreeConfigFreshness.OpacityFor(
            FreeConfigFreshness.ClassifyTier(Entry, DateTime.UtcNow));

    public int FreshnessSortKey => FreeConfigFreshness.SortKey(Entry, DateTime.UtcNow);

    public bool IsFast => Entry.Status == FreeConfigStatus.Verified ||
                          (Entry.Status == FreeConfigStatus.Ok && Entry.LatencyMs < 100);
    public bool IsMedium => Entry.Status == FreeConfigStatus.Ok && Entry.LatencyMs >= 100 && Entry.LatencyMs < 300;
    public bool IsSlow => Entry.Status == FreeConfigStatus.Ok && Entry.LatencyMs >= 300;
    public bool IsDanger => Entry.Status is FreeConfigStatus.Slow or FreeConfigStatus.Implausible or FreeConfigStatus.TlsFailed;
    public bool IsNeutral => !IsFast && !IsMedium && !IsSlow && !IsDanger;

    public string LatencyColor => Entry.Status switch
    {
        FreeConfigStatus.Verified                         => "#059669",
        FreeConfigStatus.Ok   when Entry.LatencyMs < 100 => "#22C55E",
        FreeConfigStatus.Ok   when Entry.LatencyMs < 300 => "#65A30D",
        FreeConfigStatus.Ok                               => "#F59E0B",
        FreeConfigStatus.Slow                             => "#EF4444",
        FreeConfigStatus.Implausible                      => "#DC2626",
        FreeConfigStatus.TlsFailed                        => "#F97316",
        _                                                  => "#9CA3AF",
    };

    public string ErrorTooltip => Entry.LastError ?? string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Entry.Name) ? Endpoint : Entry.Name;

    public static string FlagFor(string? cc)
    {
        if (string.IsNullOrEmpty(cc) || cc.Length != 2) return "🌐";

        var upper = cc.ToUpperInvariant();
        var chars = new int[2];
        chars[0] = 0x1F1E6 + (upper[0] - 'A');
        chars[1] = 0x1F1E6 + (upper[1] - 'A');
        return char.ConvertFromUtf32(chars[0]) + char.ConvertFromUtf32(chars[1]);
    }
}
