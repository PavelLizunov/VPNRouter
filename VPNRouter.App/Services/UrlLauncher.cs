#nullable enable
using System;
using System.Diagnostics;
using Serilog;

namespace VPNRouter.App.Services;

/// <summary>
/// Provides secure URL launching by validating URI scheme and structure before passing to shell execution.
/// </summary>
public static class UrlLauncher
{
    /// <summary>
    /// Validates whether the given string is a valid absolute HTTP or HTTPS URL.
    /// </summary>
    public static bool IsValidWebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    /// <summary>
    /// Launches an external HTTP or HTTPS URL safely using the system default browser.
    /// Rejects non-HTTP/HTTPS schemes (e.g. file://, cmd://, ms-settings://, local executables).
    /// </summary>
    public static bool TryOpenUrl(string? url)
    {
        if (!IsValidWebUrl(url))
        {
            Log.Logger.Warning("[UrlLauncher] Rejected non-HTTP(S) or invalid URL launch request: {Url}", url);
            return false;
        }

        try
        {
            var uri = new Uri(url!);
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Logger.Debug(ex, "[UrlLauncher] Failed to open URL: {Url}", url);
            return false;
        }
    }
}
