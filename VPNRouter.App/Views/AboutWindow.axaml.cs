using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VPNRouter.Core;

namespace VPNRouter.App.Views;

/// <summary>
/// About dialog. Shows version, sing-box version, author, and a link back to
/// the GitHub repo. Opened from the ⋯ menu flyout. v2.25.0 — took over the
/// "by NiniTux · v2.x.y · sing-box …" subtitle that used to live in the
/// compact header so the header can be a single tight row of badges.
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // Load the same logo the main window shows in its header. Falls back
        // silently — a missing asset here is a cosmetic miss, not a crash.
        try
        {
            using var stream = AssetLoader.Open(
                new Uri("avares://VPNRouter.App/Assets/penguin_logo.png"));
            LogoImage.Source = new Bitmap(stream);
        }
        catch
        {
            // Asset missing at runtime — leave placeholder blank.
        }

        // Populate the info block from AppVersion and the bundled sing-box.
        VersionTextBlock.Text = $"v{AppVersion.Version}";
        SingBoxTextBlock.Text = GetSingBoxVersion();
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void OnOpenRepoClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/PavelLizunov/VPNRouter",
                UseShellExecute = true
            });
        }
        catch
        {
            // Default browser unavailable / denied — no-op, this is just a
            // convenience link, the user can paste the URL manually.
        }
    }

    /// <summary>
    /// Resolve the bundled sing-box binary version. On first launch the
    /// binary may not be extracted yet; in that case we return a placeholder
    /// rather than raising. Mirrors MainWindowViewModel.GetSingBoxVersion but
    /// kept local so the About dialog is self-contained.
    /// </summary>
    private static string GetSingBoxVersion()
    {
        try
        {
            var singboxPath = AppPaths.SingBoxExePath;
            if (!System.IO.File.Exists(singboxPath))
                return "not installed";

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = singboxPath,
                    Arguments = "version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);

            // Parse "sing-box version 1.13.7" → keep only the version number
            // so the About dialog shows it without the redundant "sing-box"
            // prefix (the field already has a "sing-box" label next to it).
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("sing-box version", StringComparison.OrdinalIgnoreCase))
                    return trimmed.Substring("sing-box version".Length).Trim();
            }
            return "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
