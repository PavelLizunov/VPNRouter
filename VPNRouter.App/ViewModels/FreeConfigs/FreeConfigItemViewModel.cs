using CommunityToolkit.Mvvm.ComponentModel;
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

    public string LatencyDisplay => Entry.Status switch
    {
        FreeConfigStatus.Ok          => $"{Entry.LatencyMs} ms ✓",
        FreeConfigStatus.Slow        => $"{Entry.LatencyMs} ms slow",
        FreeConfigStatus.TlsFailed   => "TLS failed",
        FreeConfigStatus.Timeout     => "timeout",
        FreeConfigStatus.Unreachable => "unreachable",
        FreeConfigStatus.ParseError  => "parse error",
        _                             => "—",
    };

    public int LatencySortKey => Entry.Status switch
    {
        FreeConfigStatus.Ok when Entry.LatencyMs > 0 => Entry.LatencyMs,
        FreeConfigStatus.Slow                         => Entry.LatencyMs + 10_000,
        FreeConfigStatus.TlsFailed                    => 500_000,
        FreeConfigStatus.Timeout                      => 1_000_000,
        FreeConfigStatus.Unreachable                  => 1_000_001,
        _                                              => 999_999,
    };

    public bool IsWorking => Entry.Status == FreeConfigStatus.Ok;

    /// <summary>Hex color string for latency badge.</summary>
    public string LatencyColor => Entry.Status switch
    {
        FreeConfigStatus.Ok   when Entry.LatencyMs < 100 => "#22C55E",  // green — verified + fast
        FreeConfigStatus.Ok   when Entry.LatencyMs < 300 => "#65A30D",  // lime — verified + OK
        FreeConfigStatus.Ok                               => "#F59E0B",  // orange — verified + slow-ish
        FreeConfigStatus.Slow                             => "#EF4444",  // red — too slow
        FreeConfigStatus.TlsFailed                        => "#F97316",  // orange — reachable but fake/dead
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
