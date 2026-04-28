using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.App.Localization;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.App.ViewModels.FreeConfigs;

/// <summary>
/// View-model wrapper for a single FreeConfigEntry shown in the DataGrid.
/// Formats country flag, latency, status color etc.
/// </summary>
public partial class FreeConfigItemViewModel : ObservableObject
{
    public FreeConfigEntry Entry { get; }

    public FreeConfigItemViewModel(FreeConfigEntry entry)
    {
        Entry = entry;
    }

    /// <summary>v2.28.6 Phase 3: in-row spinner toggle while a single-row
    /// Recheck is in flight. Bound by the Recheck commands; the saved-tab
    /// row template flips its trailing icon to a spinner when this is true.</summary>
    [ObservableProperty] private bool _isRecheckRunning;

    public string Id          => Entry.Id;
    public string Endpoint    => $"{Entry.Host}:{Entry.Port}";
    public string Sni         => Entry.Sni ?? "";
    public string Transport   => Entry.Transport;
    public string Security    => Entry.Security;

    /// <summary>Country code like "RU" or "—".</summary>
    public string CountryCode => string.IsNullOrEmpty(Entry.CountryCode) ? "—" : Entry.CountryCode;

    /// <summary>Flag emoji based on country code (best-effort).</summary>
    public string CountryFlag => FlagFor(Entry.CountryCode);

    /// <summary>Human label: e.g. "🇷🇺 RU".</summary>
    public string CountryDisplay => string.IsNullOrEmpty(Entry.CountryCode)
        ? "—"
        : $"{FlagFor(Entry.CountryCode)} {Entry.CountryCode}";

    /// <summary>v2.28.5-r2: bandwidth column was previously crammed into the
    /// LatencyDisplay badge; now it's its own column so the latency badge
    /// stays tight. Shows "X Mbps" when measured, "—" when not (a verified
    /// config without bandwidth means the deep verifier ran without bw
    /// measurement enabled, e.g. user picked a non-bw preset).</summary>
    public string BandwidthDisplay => Entry.MeasuredBandwidthMbps.HasValue
        ? $"{Entry.MeasuredBandwidthMbps} Mbps"
        : "—";

    public string LatencyDisplay => Entry.Status switch
    {
        FreeConfigStatus.Verified    => $"{Entry.LatencyMs} ms ✓✓",
        FreeConfigStatus.Ok          => $"{Entry.LatencyMs} ms ✓",
        FreeConfigStatus.Slow        => $"{Entry.LatencyMs} ms slow",
        FreeConfigStatus.Implausible => "fake (<5ms)",
        FreeConfigStatus.TlsFailed   => "TLS failed",
        FreeConfigStatus.Timeout     => "timeout",
        FreeConfigStatus.Unreachable => "unreachable",
        FreeConfigStatus.ParseError  => "parse error",
        _                             => "—",
    };

    public int LatencySortKey => Entry.Status switch
    {
        FreeConfigStatus.Verified                     => Entry.LatencyMs, // best rank
        FreeConfigStatus.Ok when Entry.LatencyMs > 0 => Entry.LatencyMs + 100_000,
        FreeConfigStatus.Slow                         => Entry.LatencyMs + 200_000,
        FreeConfigStatus.Implausible                  => 400_000,
        FreeConfigStatus.TlsFailed                    => 500_000,
        FreeConfigStatus.Timeout                      => 1_000_000,
        FreeConfigStatus.Unreachable                  => 1_000_001,
        _                                              => 999_999,
    };

    public bool IsWorking => Entry.Status == FreeConfigStatus.Ok;

    // ── v2.28.6 Phase 2 — Saved-tab freshness ──

    /// <summary>True if the most recent re-verify (or the original verify)
    /// failed, leaving us with last-good numbers but no current connectivity.
    /// Phase 3 sets <see cref="FreeConfigEntry.LastVerifyFailedAt"/> to
    /// drive this; Phase 1 left it null on every entry.</summary>
    public bool HasFailedLastCheck =>
        Entry.LastVerifyFailedAt.HasValue &&
        (!Entry.LastTestedAt.HasValue ||
            Entry.LastVerifyFailedAt.Value >= Entry.LastTestedAt.Value);

    /// <summary>True when the saved entry is older than 24 h since its
    /// last successful verify, OR the last re-verify failed. Drives the
    /// "Recheck stale (N)" bulk button + the dim-opacity rendering.</summary>
    public bool IsStale
    {
        get
        {
            if (HasFailedLastCheck) return true;
            if (!Entry.LastTestedAt.HasValue) return true;
            return (DateTime.UtcNow - Entry.LastTestedAt.Value).TotalHours > 24;
        }
    }

    /// <summary>Human label for the Status column on the Saved tab:
    /// "fresh" / "Nd ago" / "stale" / "failed". Computed from
    /// <see cref="FreeConfigEntry.LastTestedAt"/> +
    /// <see cref="FreeConfigEntry.LastVerifyFailedAt"/> at VM build time.</summary>
    public string FreshnessLabel
    {
        get
        {
            if (HasFailedLastCheck) return Strings.FcFreshnessFailed;
            if (!Entry.LastTestedAt.HasValue) return Strings.FcFreshnessFresh;
            var ageDays = (DateTime.UtcNow - Entry.LastTestedAt.Value).TotalDays;
            if (ageDays < 1) return Strings.FcFreshnessFresh;
            if (ageDays > 7) return Strings.FcFreshnessStale;
            return Strings.FcFreshnessAgeingDays((int)Math.Floor(ageDays));
        }
    }

    /// <summary>Visual dim level for the Saved tab: 1.0 fresh / 0.75
    /// ageing / 0.5 stale or failed. Bound to row Opacity.</summary>
    public double OpacityValue
    {
        get
        {
            if (HasFailedLastCheck) return 0.5;
            if (!Entry.LastTestedAt.HasValue) return 1.0;
            var ageDays = (DateTime.UtcNow - Entry.LastTestedAt.Value).TotalDays;
            if (ageDays < 1) return 1.0;
            if (ageDays > 7) return 0.5;
            return 0.75;
        }
    }

    /// <summary>Sort key for the Saved tab: ascending by freshness tier
    /// (fresh first), then by latency. Failed-last-check rows go to the
    /// bottom regardless of age.</summary>
    public int FreshnessSortKey
    {
        get
        {
            if (HasFailedLastCheck) return 1_000_000;
            if (!Entry.LastTestedAt.HasValue) return Entry.LatencyMs > 0 ? Entry.LatencyMs : 0;
            var ageDays = (DateTime.UtcNow - Entry.LastTestedAt.Value).TotalDays;
            int tier = ageDays < 1 ? 0 : ageDays > 7 ? 200_000 : 100_000;
            return tier + (Entry.LatencyMs > 0 ? Entry.LatencyMs : 0);
        }
    }

    /// <summary>Hex color string for latency badge.</summary>
    public string LatencyColor => Entry.Status switch
    {
        FreeConfigStatus.Verified                         => "#059669",  // emerald — deep-verified ✓✓
        FreeConfigStatus.Ok   when Entry.LatencyMs < 100 => "#22C55E",  // green — TCP+TLS OK + fast
        FreeConfigStatus.Ok   when Entry.LatencyMs < 300 => "#65A30D",  // lime
        FreeConfigStatus.Ok                               => "#F59E0B",  // orange — slower
        FreeConfigStatus.Slow                             => "#EF4444",  // red
        FreeConfigStatus.Implausible                      => "#DC2626",  // dark red — fake
        FreeConfigStatus.TlsFailed                        => "#F97316",  // orange — dead
        _                                                  => "#9CA3AF",  // gray — offline
    };

    /// <summary>Tooltip shown on hover — human-readable reason.</summary>
    public string ErrorTooltip => Entry.LastError ?? string.Empty;

    /// <summary>Display name from vless:// fragment, fallback to endpoint.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Entry.Name) ? Endpoint : Entry.Name;

    /// <summary>Converts ISO-2 country code to flag emoji. Empty/unknown → globe emoji.</summary>
    public static string FlagFor(string? cc)
    {
        if (string.IsNullOrEmpty(cc) || cc.Length != 2) return "🌐";

        var upper = cc.ToUpperInvariant();
        // Regional Indicator Symbol Letter A = U+1F1E6, 'A' = 0x41.
        var chars = new int[2];
        chars[0] = 0x1F1E6 + (upper[0] - 'A');
        chars[1] = 0x1F1E6 + (upper[1] - 'A');
        return char.ConvertFromUtf32(chars[0]) + char.ConvertFromUtf32(chars[1]);
    }
}
