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

    public string? LiteDownloadUrl { get; init; }
    public long LiteSizeBytes { get; init; }
    public bool HasLiteUpdate { get; init; }

    public string? FullChecksumUrl { get; init; }
    public string? LiteChecksumUrl { get; init; }
    public string? FullChecksumSha256 { get; init; }
}
