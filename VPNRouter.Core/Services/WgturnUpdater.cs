using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// W-4 stub — placeholder for the real implementation provided by W-1
/// (see <c>plans/wgturn-on-demand-download.md</c> §4 + §6). This file
/// exists so W-4 (the Tools tab UI card) can compile and pass tests
/// against a stable API surface before W-1 lands.
///
/// <para>When the W-1 chip merges first, this file should be deleted
/// or replaced wholesale — the W-1 implementation owns the full GitHub
/// fetch / asset resolve / SHA verify / atomic install pipeline. The
/// stub returns sensible defaults so unit tests run, but is NOT a
/// functional downloader.</para>
///
/// <para>API contract (frozen):
/// <list type="bullet">
/// <item><see cref="IsInstalled"/> — <c>File.Exists(AppPaths.WgturnCliExePath)</c></item>
/// <item><see cref="GetLocalVersion"/> — reads <c>{DataDir}/wgturn/version.txt</c></item>
/// <item><see cref="GetLocalVariant"/> — reads <c>{DataDir}/wgturn/variant.txt</c></item>
/// <item><see cref="DownloadLatestAsync"/> — full async fetch + install pipeline</item>
/// </list>
/// </para>
/// </summary>
public class WgturnUpdater
{
    /// <summary>Root directory for wgturn artifacts (binary, version
    /// file, variant marker). Sibling of the main <c>bin/</c> so it
    /// can be wiped without touching sing-box.</summary>
    public static string WgturnDir => Path.Combine(AppPaths.DataDir, "wgturn");

    /// <summary>Directory for the wgturn-cli executable. W-2 path
    /// migration target — pre-W-2 the binary lived in
    /// <see cref="AppPaths.BinDir"/> alongside sing-box, post-W-2 it
    /// moves here so the install/uninstall lifecycle is independent.</summary>
    public static string BinDir => Path.Combine(WgturnDir, "bin");

    /// <summary>Persisted version tag (e.g. <c>v0.1.0</c>). Written
    /// by <see cref="DownloadLatestAsync"/> after a successful install.</summary>
    public static string VersionFilePath => Path.Combine(WgturnDir, "version.txt");

    /// <summary>Persisted variant marker (<c>slim</c> or <c>embedded</c>).
    /// Lets the UI show which build the user has without re-fetching
    /// the release JSON.</summary>
    public static string VariantFilePath => Path.Combine(WgturnDir, "variant.txt");

    public event Action<string>? StatusChanged;

    private readonly ILogger? _logger;

    public WgturnUpdater(ILogger? logger = null) => _logger = logger;

    /// <summary>True if <see cref="AppPaths.WgturnCliExePath"/> exists
    /// (W-2 final location). Pre-W-2 the binary may instead live in
    /// <see cref="AppPaths.BinDir"/>; the W-2 migrator moves it.</summary>
    public static bool IsInstalled() => File.Exists(AppPaths.WgturnCliExePath);

    /// <summary>Read locally installed version from
    /// <see cref="VersionFilePath"/>. Returns <c>null</c> if the file
    /// is missing or unreadable.</summary>
    public static string? GetLocalVersion()
    {
        try
        {
            if (File.Exists(VersionFilePath))
                return File.ReadAllText(VersionFilePath).Trim();
        }
        catch { /* tolerate transient FS errors */ }
        return null;
    }

    /// <summary>Read the locally installed variant marker. Returns
    /// <c>null</c> if the marker is missing or unreadable.</summary>
    public static WgturnVariant? GetLocalVariant()
    {
        try
        {
            if (File.Exists(VariantFilePath))
            {
                var raw = File.ReadAllText(VariantFilePath).Trim().ToLowerInvariant();
                return raw switch
                {
                    "embedded" => WgturnVariant.Embedded,
                    "slim" => WgturnVariant.Slim,
                    _ => null,
                };
            }
        }
        catch { /* tolerate transient FS errors */ }
        return null;
    }

    /// <summary>
    /// W-1 owns the real implementation. The stub throws
    /// <see cref="NotImplementedException"/> so any caller that actually
    /// invokes a download in a stub-only build fails fast instead of
    /// silently succeeding. UI bindings should still wire to this
    /// method so the merge resolution doesn't need to touch the VM.
    /// </summary>
    public Task<string> DownloadLatestAsync(
        WgturnVariant variant = WgturnVariant.Slim,
        CancellationToken ct = default)
    {
        throw new NotImplementedException(
            "WgturnUpdater.DownloadLatestAsync is a W-4 stub. " +
            "Merge the W-1 chip (see plans/wgturn-on-demand-download.md §4) " +
            "to wire up the real GitHub fetch pipeline.");
    }
}

/// <summary>
/// W-1 / W-4 — variant of the wgturn-cli build the user picked:
/// slim (~10 MB, no embedded server) or embedded (~120 MB, with full
/// wgturn-server binary baked in for offline operator setup).
/// </summary>
public enum WgturnVariant
{
    /// <summary>Smaller (~10 MB) build with just the client.</summary>
    Slim,

    /// <summary>Larger (~120 MB) build with the wgturn-server binary
    /// embedded so the user can operate their own emergency endpoint
    /// without an extra download.</summary>
    Embedded,
}

/// <summary>W-1 error categories — kept here so W-4 unit tests can
/// reference the type. W-1 owns the actual throw sites.</summary>
public enum WgturnErrorCategory
{
    GitHubRateLimit,
    GitHubServerError,
    Network,
    Corrupted,
    Invalid,
    FileSystem,
    UnsupportedPlatform,
    Concurrent,
    Unknown,
}

/// <summary>W-1 exception type — stub so the W-4 VM and tests can
/// reference it without compile errors when W-1 hasn't landed.</summary>
public sealed class WgturnDownloadException : Exception
{
    public WgturnErrorCategory Category { get; }
    public WgturnDownloadException(WgturnErrorCategory category, string message, Exception? inner = null)
        : base(message, inner) => Category = category;
}
