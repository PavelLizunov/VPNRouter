namespace VPNRouter.Core.Models;

public class UpdateInfo
{
    public string CurrentVersion { get; init; } = string.Empty;
    public string LatestVersion { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string ReleaseNotes { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public bool IsNewer { get; init; }

    // ── Lite update support ──
    /// <summary>URL for the lite update ZIP (app binaries only, no runtime/sing-box).</summary>
    public string? LiteDownloadUrl { get; init; }
    /// <summary>Size of the lite update ZIP in bytes.</summary>
    public long LiteSizeBytes { get; init; }
    /// <summary>True if a lite update package is available AND the current install supports it.</summary>
    public bool HasLiteUpdate { get; init; }

    // ── Checksum verification (v2.15.8) ──
    /// <summary>URL of the .sha256 file for the full install ZIP (null if not published).</summary>
    public string? FullChecksumUrl { get; init; }
    /// <summary>URL of the .sha256 file for the lite update ZIP (null if not published).</summary>
    public string? LiteChecksumUrl { get; init; }
}
