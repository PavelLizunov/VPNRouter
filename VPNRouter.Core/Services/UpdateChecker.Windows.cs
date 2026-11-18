using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace VPNRouter.Core.Services;

public partial class UpdateChecker
{
    private sealed record WindowsStage(string Archive, string Sha256, string Version, bool LocalFixture = false);
    private readonly ConcurrentDictionary<string, WindowsStage> _windowsStages = new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsOfficialWindowsRelease(Uri uri, string version, bool checksum = false)
    {
        var prefix = $"https://github.com/PavelLizunov/VPNRouter/releases/download/v{version}/VPNRouter-";
        var extension = checksum ? ".zip.sha256" : ".zip";
        var current = $"v{version}-win{extension}";
        return uri.AbsoluteUri.Equals(prefix + current, StringComparison.OrdinalIgnoreCase) ||
               uri.AbsoluteUri.Equals(prefix + "update-" + current, StringComparison.OrdinalIgnoreCase) ||
               uri.AbsoluteUri.Equals(prefix + $"install-v{version}{extension}", StringComparison.OrdinalIgnoreCase);
    }

    private void PrepareStagingDirectory()
    {
        if (OperatingSystem.IsWindows() && ZapretRuntimeDirectory.TestRoot is null &&
            !ProductEdition.IsBusiness)
        {
            if (!Path.IsPathFullyQualified(_stagingDir) || _stagingDir.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("Cannot locate protected Windows update staging.");
            ZapretRuntimeDirectory.CheckWindowsParents(_stagingDir);
            ZapretRuntimeDirectory.PrepareWindowsRoot(_stagingDir);
        }
        else Directory.CreateDirectory(_stagingDir);
    }

    private WindowsStage RequireWindowsStage(string directory, string? version = null, string? sha256 = null)
    {
        if (!_windowsStages.TryGetValue(Path.GetFullPath(directory), out var stage) ||
            (version != null && (stage.Version != version ||
                !string.Equals(stage.Sha256, sha256, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("The update must be verified in this application session.");
        return stage;
    }

    [SupportedOSPlatform("windows")]
    internal string StageWindowsCiArchive(string archive, string version, string sha256)
    {
        var scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "VPNRouter Update Test", "app");
        if (Environment.GetEnvironmentVariable("VPNROUTER_CI") != "1" ||
            !Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).Equals(scratch, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Local update fixtures require the dedicated CI test installation.");
        PrepareStagingDirectory();
        var attempt = Directory.CreateDirectory(Path.Combine(_stagingDir, Guid.NewGuid().ToString("N")));
        var verified = Path.Combine(attempt.FullName, "release.zip");
        using (var source = File.OpenRead(Path.GetFullPath(archive)))
        using (var destination = new FileStream(verified, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            source.CopyTo(destination);
        var directory = Path.Combine(attempt.FullName, "extracted");
        ExtractVerifiedWindowsArchive(verified, sha256, directory);
        _windowsStages[directory] = new(verified, sha256, version, LocalFixture: true);
        return directory;
    }

    internal static string ExtractVerifiedWindowsArchive(string archivePath, string sha256, string directory)
    {
        using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Convert.ToHexString(SHA256.HashData(archive)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Windows update archive checksum mismatch.");
        archive.Position = 0;
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
        zip.ExtractToDirectory(directory);
        static bool ContainsLauncher(string dir) => File.Exists(Path.Combine(dir, "VPNRouter.GUI.exe")) ||
            File.Exists(Path.Combine(dir, "VPNRouter.GUI.dll"));
        var subdirectory = Path.Combine(directory, "app");
        var payload = ContainsLauncher(subdirectory) ? subdirectory : directory;
        if (!ContainsLauncher(payload)) throw new InvalidDataException("Windows update has no GUI launcher.");
        var gui = Path.Combine(payload, "VPNRouter.GUI.exe");
        var bootstrap = Path.Combine(payload, "_bootstrap");
        if (!Directory.Exists(bootstrap) || !File.Exists(gui)) return payload;
        File.Copy(gui, Path.Combine(bootstrap, "VPNRouter.GUI.exe"), overwrite: true);
        return bootstrap;
    }

    private void ApplyUpdateWindows(string extractedDir, bool writeInstallReceipt)
    {
        if (!OperatingSystem.IsWindows() || ZapretRuntimeDirectory.TestRoot is not null)
            throw new PlatformNotSupportedException("Windows update publication requires a protected Windows installation.");
        var stage = RequireWindowsStage(extractedDir);
        PrepareStagingDirectory();
        var app = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var install = new DirectoryInfo(WindowsInstallDir);
        ZapretRuntimeDirectory.CheckWindowsParents(install.FullName);
        ZapretRuntimeDirectory.CheckWindowsTree(install, allowCreatorOwnerTemplate: true);

        // Re-extract the digest-bound archive while holding the same stream. Never publish the preview tree.
        var work = Path.Combine(_stagingDir, Guid.NewGuid().ToString("N"));
        ZapretRuntimeDirectory.PrepareWindowsRoot(work);
        var payload = ExtractVerifiedWindowsArchive(stage.Archive, stage.Sha256, Path.Combine(work, "release"));
        foreach (var name in new[] { "publish-windows-update.ps1", "zapret-maintenance.ps1", "stop-vpnrouter.ps1" })
        {
            using var resource = typeof(UpdateChecker).Assembly.GetManifestResourceStream("VPNRouter.Update." + name)
                ?? throw new InvalidOperationException("Missing packaged Windows update helper: " + name);
            using var output = new FileStream(Path.Combine(work, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            resource.CopyTo(output);
        }

        if (!install.FullName.Equals(app, StringComparison.OrdinalIgnoreCase))
        {
            var snap = UpdateBackup.CreateSnapshot(install.FullName);
            StatusChanged?.Invoke(snap.Success
                ? "Backup created — update will be reversible if it fails."
                : $"Backup creation skipped: {snap.Diagnostic}");
        }
        if (writeInstallReceipt) TryWriteInstallReceipt();

        var system = Environment.SystemDirectory;
        var shell = ZapretRuntimeDirectory.SystemFile(@"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            WorkingDirectory = system,
            CreateNoWindow = true,
        };
        var windows = Directory.GetParent(system)!.FullName;
        start.Environment.Clear();
        start.Environment["SystemRoot"] = start.Environment["WINDIR"] = windows;
        start.Environment["SystemDrive"] = Path.GetPathRoot(system)!.TrimEnd('\\');
        start.Environment["TEMP"] = start.Environment["TMP"] = work;
        start.Environment["ComSpec"] = Path.Combine(system, "cmd.exe");
        start.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        start.Environment["USERPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        start.Environment["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        start.Environment["LOCALAPPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        start.Environment["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        start.Environment["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        start.Environment["PATH"] = string.Join(';', system, windows, Path.Combine(system, "Wbem"), Path.GetDirectoryName(shell));
        start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(shell)!, "Modules");
        using var parent = Process.GetCurrentProcess();
        foreach (var value in new[] {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", Path.Combine(work, "publish-windows-update.ps1"), "-Source", payload, "-InstallDir", install.FullName,
            "-ApplicationDir", app, "-DataDir", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VPNRouter"), "-ParentId", parent.Id.ToString(CultureInfo.InvariantCulture),
            "-ParentStarted", parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
        }) start.ArgumentList.Add(value);
        if (stage.LocalFixture) start.ArgumentList.Add("-Fixture");
        if (stage.LocalFixture || Environment.GetEnvironmentVariable("VPNROUTER_CI") == "1")
            start.ArgumentList.Add("-SkipRelaunch");
        using var helper = Process.Start(start) ?? throw new IOException("Cannot start the Windows update publisher.");
        if (helper.WaitForExit(300) && helper.ExitCode != 0)
            throw new IOException("The Windows update publisher failed before application exit.");
    }
}
