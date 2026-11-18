using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Serilog;

namespace VPNRouter.App.Services;

public static class SelfRepair
{
    private const string MarkerFileName = "self-repair-marker";
    private const int LoopWindowMinutes = 10;

    public sealed record Decision(bool ShouldRun, string Reason);

    public static Decision Plan(ILogger? logger = null)
    {
        if (VPNRouter.Core.ProductEdition.IsBusiness)
            return new Decision(false, "Business pilot updates are installed by the administrator.");
        try
        {
            var dir = VPNRouter.Core.AppPaths.DataDir;
            Directory.CreateDirectory(dir);
            var marker = Path.Combine(dir, MarkerFileName);
            if (File.Exists(marker))
            {
                var stamp = File.GetLastWriteTimeUtc(marker);
                var age = DateTime.UtcNow - stamp;
                if (age < TimeSpan.FromMinutes(LoopWindowMinutes))
                {
                    return new Decision(false,
                        $"repair attempted {age.TotalMinutes:F0} min ago — not looping (marker: {marker})");
                }
            }
            return new Decision(true, "no recent repair marker — safe to attempt");
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[SelfRepair] failed to read repair marker — defaulting to run");
            return new Decision(true, "marker read failed — defaulting to run");
        }
    }

    public static void Run(ILogger? logger = null)
    {
        if (VPNRouter.Core.ProductEdition.IsBusiness) return;
        try
        {
            var dir = VPNRouter.Core.AppPaths.DataDir;
            Directory.CreateDirectory(dir);
            var marker = Path.Combine(dir, MarkerFileName);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[SelfRepair] failed to write loop marker — proceeding anyway");
        }

        var compiledVersion = VPNRouter.Core.AppVersion.Version;
        var isPrerelease = compiledVersion.Contains("-r", StringComparison.Ordinal);
        using var helper = Process.Start(BuildProcessStartInfo(AppContext.BaseDirectory, isPrerelease, Environment.SystemDirectory))
            ?? throw new IOException("Cannot start the Windows repair helper.");
        logger?.Information("[SelfRepair] Started verified-release repair for {Version}", compiledVersion);
    }

    internal static ProcessStartInfo BuildProcessStartInfo(string applicationDir, bool prerelease, string system)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDir);
        if (!Path.IsPathFullyQualified(applicationDir) || applicationDir.Any(c => c == '"' || char.IsControl(c)) || !Path.IsPathFullyQualified(system))
            throw new ArgumentException("Invalid Windows repair path.");

        using var resource = typeof(SelfRepair).Assembly.GetManifestResourceStream("VPNRouter.Windows.RepairBootstrap")
            ?? throw new IOException("Missing Windows repair helper.");
        using var reader = new StreamReader(resource);
        var script = reader.ReadToEnd()
            .Replace("__APPLICATION_DIR__", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(applicationDir)))
            .Replace("__PRERELEASE__", prerelease ? "$true" : "$false");
        var shellDir = Path.Combine(system, "WindowsPowerShell", "v1.0");
        var psi = new ProcessStartInfo(Path.Combine(shellDir, "powershell.exe"))
        {
            WorkingDirectory = system,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment.Clear();
        psi.Environment["SystemRoot"] = psi.Environment["WINDIR"] = Directory.GetParent(system)!.FullName;
        psi.Environment["PATH"] = system + ";" + shellDir;
        psi.Environment["PSModulePath"] = Path.Combine(shellDir, "Modules");
        psi.Environment["ComSpec"] = Path.Combine(system, "cmd.exe");
        psi.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        psi.Environment["SystemDrive"] = Path.GetPathRoot(system)!.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                     Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) })
            psi.ArgumentList.Add(arg);
        return psi;
    }
}
