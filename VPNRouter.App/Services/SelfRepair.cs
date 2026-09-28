using System;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace VPNRouter.App.Services;

public static class SelfRepair
{
    private const string MarkerFileName = "self-repair-marker";
    private const int LoopWindowMinutes = 10;

    private const string InstallScriptUrl = "https://vpn.ninitux.com/install.ps1";

    public sealed record Decision(bool ShouldRun, string Reason);

    public static Decision Plan(ILogger? logger = null)
    {
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
        var prereleaseFlag = isPrerelease ? " -Prerelease" : string.Empty;
        logger?.Information("[SelfRepair] running install.ps1{Flag} (current build = {Version})",
            prereleaseFlag, compiledVersion);

        var bootstrapScript =
            "$ErrorActionPreference = 'Stop'\r\n" +
            "$ProgressPreference = 'SilentlyContinue'\r\n" +
            "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12\r\n" +
            "$tmp = Join-Path $env:TEMP 'vpnr-repair.ps1'\r\n" +
            $"Invoke-WebRequest -Uri '{InstallScriptUrl}' -OutFile $tmp -UseBasicParsing\r\n" +
            $"& $tmp{prereleaseFlag}\r\n";

        var bootstrapPath = Path.Combine(
            Path.GetTempPath(),
            $"vpnr-self-repair-{DateTime.UtcNow:yyyyMMddHHmmss}.ps1");
        try
        {
            File.WriteAllText(bootstrapPath, bootstrapScript);
        }
        catch (Exception ex)
        {
            logger?.Error(ex, "[SelfRepair] failed to write bootstrap helper to {Path}", bootstrapPath);
            throw;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{bootstrapPath}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            Process.Start(psi);
            logger?.Information("[SelfRepair] launched install.ps1 web one-liner — current process will exit so installer can replace files");
        }
        catch (Exception ex)
        {
            logger?.Error(ex, "[SelfRepair] failed to spawn repair helper — install must be repaired manually");
            throw;
        }
    }
}
