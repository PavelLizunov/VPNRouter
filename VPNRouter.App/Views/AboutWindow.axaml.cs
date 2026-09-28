using System;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VPNRouter.Core;

namespace VPNRouter.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        try
        {
            using var stream = AssetLoader.Open(
                new Uri("avares://VPNRouter.App/Assets/penguin_logo.png"));
            LogoImage.Source = new Bitmap(stream);
        }
        catch
        {
        }

        VersionTextBlock.Text = $"v{AppVersion.Version}";
        SingBoxTextBlock.Text = GetSingBoxVersion();
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void OnOpenRepoClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            const string url = "https://github.com/PavelLizunov/VPNRouter";
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uri.AbsoluteUri,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
        }
    }

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

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(3000);

            foreach (var source in new[] { stdout, stderr })
            {
                foreach (var line in source.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("sing-box version", StringComparison.OrdinalIgnoreCase))
                        return trimmed.Substring("sing-box version".Length).Trim();
                }
            }

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
            try
            {
                var logPath = System.IO.Path.Combine(AppPaths.LogsDir, "about-probe.log");
                System.IO.Directory.CreateDirectory(AppPaths.LogsDir);
                System.IO.File.AppendAllText(
                    logPath,
                    $"[{DateTime.UtcNow:u}] GetSingBoxVersion failed: {ex.GetType().Name}: {ex.Message}\n");
            }
            catch { }

            return $"err: {ex.GetType().Name}";
        }
    }
}
