using System.Diagnostics;
using Serilog;

namespace VPNRouter.Core.Platform.macOS;

/// <summary>
/// Managed install/migration state machine for the macOS privileged helper.
/// The helper probe is authoritative; a user-writable marker is never used.
/// </summary>
public static class MacPrivilegedSetup
{
    public static bool EnsureInstalled(ILogger logger)
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        var client = new MacPrivilegedClient(logger: logger);
        var state = client.Probe();
        if (IsCurrent(state, out var helperHash, out var singboxHash))
            return true;

        var helperPath = Path.Combine(AppContext.BaseDirectory, MacPrivilegePaths.HelperBundleName);
        var installerPath = Path.Combine(AppContext.BaseDirectory, MacPrivilegePaths.InstallerScriptName);
        var templatePath = Path.Combine(AppContext.BaseDirectory, MacPrivilegePolicy.TemplateFileName);
        var singboxPath = AppPaths.BundledSingBoxExePath;
        if (!IsProtectedRegularFile(helperPath) || !IsProtectedRegularFile(installerPath) ||
            !IsProtectedRegularFile(templatePath) || !IsProtectedRegularFile(singboxPath))
        {
            logger.Error(
                "[MacPrivilege] Bundled privileged artifacts are missing, linked, or in a writable directory; Connect is blocked " +
                "(helper={Helper}, installer={Installer}, template={Template}, singbox={SingBox})",
                File.Exists(helperPath), File.Exists(installerPath),
                File.Exists(templatePath), File.Exists(singboxPath));
            return false;
        }

        try
        {
            var template = File.ReadAllText(templatePath);
            var rendered = MacPrivilegePolicy.RenderFromTemplate(template, Environment.UserName);
            if (!MacPrivilegePolicy.IsManagedPolicy(rendered, Environment.UserName))
            {
                logger.Error(
                    "[MacPrivilege] Bundled sudoers template does not render exactly one managed helper rule; Connect is blocked");
                return false;
            }

            helperHash = MacPrivilegedClient.ComputeFileSha256(helperPath);
            singboxHash = MacPrivilegedClient.ComputeFileSha256(singboxPath);
            var command = BuildInstallerCommand(
                installerPath, templatePath, helperPath, singboxPath, helperHash, singboxHash,
                AppVersion.Version, Environment.UserName);
            var result = RunElevated(command, logger);
            if (!result)
                return false;

            var after = client.Probe();
            return IsCurrent(after, out _, out _);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[MacPrivilege] Managed installation failed; Connect is blocked");
            return false;
        }
    }

    internal static bool IsCurrent(MacPrivilegeState state, out string helperHash, out string singboxHash)
    {
        helperHash = string.Empty;
        singboxHash = string.Empty;
        if (!state.Ready || state.Policy != MacPrivilegePaths.PolicyVersion) return false;

        var helperPath = Path.Combine(AppContext.BaseDirectory, MacPrivilegePaths.HelperBundleName);
        var singboxPath = AppPaths.BundledSingBoxExePath;
        if (!File.Exists(helperPath) || !File.Exists(singboxPath)) return false;

        helperHash = MacPrivilegedClient.ComputeFileSha256(helperPath);
        singboxHash = MacPrivilegedClient.ComputeFileSha256(singboxPath);
        return string.Equals(state.HelperSha256, helperHash, StringComparison.Ordinal)
               && string.Equals(state.SingBoxSha256, singboxHash, StringComparison.Ordinal);
    }

    internal static string BuildInstallerCommand(
        string installerPath,
        string policyTemplatePath,
        string helperPath,
        string singboxPath,
        string helperHash,
        string singboxHash,
        string appVersion,
        string userName)
    {
        MacPrivilegePolicy.ValidateUserName(userName);
        return
            $"/bin/bash {Quote(installerPath)} " +
            $"--policy-template {Quote(policyTemplatePath)} " +
            $"--helper {Quote(helperPath)} " +
            $"--singbox {Quote(singboxPath)} " +
            $"--helper-sha256 {Quote(helperHash)} " +
            $"--singbox-sha256 {Quote(singboxHash)} " +
            $"--app-version {Quote(appVersion)} " +
            $"--user {Quote(userName)}";
    }

    internal static string BuildAppleScript(string shellCommand)
    {
        var escaped = shellCommand
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"do shell script \"{escaped}\" with administrator privileges";
    }

    internal static string Quote(string value)
    {
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static bool IsProtectedRegularFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null)
            return false;

        var fileWriteBits = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        var directoryWriteBits = UnixFileMode.OtherWrite;
        try
        {
            if ((File.GetUnixFileMode(path) & fileWriteBits) != 0)
                return false;
            for (var directory = info.Directory; directory is not null;
                 directory = directory.Parent)
            {
                if (directory.LinkTarget is not null)
                    return false;
                if ((File.GetUnixFileMode(directory.FullName) & directoryWriteBits) != 0)
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool RunElevated(string shellCommand, ILogger logger)
    {
        var psi = new ProcessStartInfo("/usr/bin/osascript")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(BuildAppleScript(shellCommand));

        using var process = Process.Start(psi);
        if (process is null)
        {
            logger.Error("[MacPrivilege] Could not start osascript");
            return false;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            logger.Error("[MacPrivilege] Administrator installation timed out");
            return false;
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            logger.Warning(
                "[MacPrivilege] Administrator installation exit={Exit} stdout={Out} stderr={Err}",
                process.ExitCode, stdout.Trim(), stderr.Trim());
            return false;
        }

        logger.Information("[MacPrivilege] Managed helper installed or refreshed");
        return true;
    }
}
