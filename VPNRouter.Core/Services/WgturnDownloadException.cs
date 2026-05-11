namespace VPNRouter.Core.Services;

/// <summary>
/// Exception thrown when a wgturn-cli download hits an actionable
/// failure we can describe to the user (category + human-readable
/// message). UI layer should display <see cref="Exception.Message"/>
/// directly instead of wrapping again. Mirrors the
/// <see cref="ZapretDownloadException"/> shape so call-sites can
/// use the same dispatch pattern.
/// </summary>
public sealed class WgturnDownloadException : Exception
{
    public WgturnErrorCategory Category { get; }

    public WgturnDownloadException(WgturnErrorCategory category, string message, Exception? inner = null)
        : base(message, inner) => Category = category;
}

/// <summary>
/// Categorises wgturn-cli download failures so UI can branch on the
/// type (e.g. "wait and retry" for transient, "report a bug" for
/// upstream format change, "install a browser" for unsupported
/// platform).
/// </summary>
public enum WgturnErrorCategory
{
    /// <summary>GitHub API rate-limited us (403). Transient, time-based.</summary>
    GitHubRateLimit,
    /// <summary>GitHub server error (5xx). Transient, retry-friendly.</summary>
    GitHubServerError,
    /// <summary>Network drop / DNS / timeout. Transient, user-dependent.</summary>
    Network,
    /// <summary>Downloaded bytes don't match the GitHub-reported size, or SHA256 mismatch.</summary>
    Corrupted,
    /// <summary>Release structure unexpected (no asset matching our OS/arch naming).</summary>
    Invalid,
    /// <summary>Antivirus / file system / permission issue during install.</summary>
    FileSystem,
    /// <summary>Current OS/arch combination has no published wgturn-cli binary.</summary>
    UnsupportedPlatform,
    /// <summary>Another download already in progress.</summary>
    Concurrent,
    /// <summary>Everything else.</summary>
    Unknown,
}
