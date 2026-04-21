using System;
using System.Diagnostics;
using System.Linq;
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
    /// v2.25.0-r2: on macOS the probe was silently returning "unknown"
    /// because stderr wasn't redirected — sing-box on darwin writes some
    /// boot diagnostics to stderr first, and without a reader the stderr
    /// pipe fills up and the child blocks before it gets to print the
    /// version line. Fix: redirect both streams, wait, then parse whichever
    /// stream contained the "version" token. Also write any exception to a
    /// sidecar log so the next Mac report has something concrete.
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
                    RedirectStandardError  = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();

            // Drain both pipes so neither fills up and blocks the child.
            // ReadToEnd is fine because sing-box version exits within ms.
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(3000);

            // Check stdout first (canonical), then stderr as fallback.
            foreach (var source in new[] { stdout, stderr })
            {
                foreach (var line in source.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("sing-box version", StringComparison.OrdinalIgnoreCase))
                        return trimmed.Substring("sing-box version".Length).Trim();
                }
            }

            // Nothing matched — last-resort: return the first non-empty
            // output line so the user at least sees WHAT came back rather
            // than an opaque "unknown". Cap length so the dialog layout
            // doesn't explode if sing-box printed a huge stack trace.
            var firstLine = (stdout + "\n" + stderr)
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => !string.IsNullOrEmpty(l));
            if (!string.IsNullOrEmpty(firstLine))
                return firstLine.Length > 48 ? firstLine.Substring(0, 48) + "…" : firstLine;

            return "unknown";
        }
        catch (Exception ex)
        {
            // Best-effort: log to ~/.../logs/about-probe.log so the next
            // support round can see what failed without having to add a
            // debugger. Silent if log write itself fails.
            try
            {
                var logPath = System.IO.Path.Combine(AppPaths.LogsDir, "about-probe.log");
                System.IO.Directory.CreateDirectory(AppPaths.LogsDir);
                System.IO.File.AppendAllText(
                    logPath,
                    $"[{DateTime.UtcNow:u}] GetSingBoxVersion failed: {ex.GetType().Name}: {ex.Message}\n");
            }
            catch { /* log failure is not a crash */ }

            return $"err: {ex.GetType().Name}";
        }
    }
}
