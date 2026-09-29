using VPNRouter.Core.Localization;

namespace VPNRouter.Core.Services;

public partial class UpdateChecker
{
    internal static bool IsVersionDowngrade(string currentVersion, string targetVersion) =>
        TryParseSemVer(currentVersion, out var current) &&
        TryParseSemVer(targetVersion, out var target) &&
        target.CompareTo(current) < 0;

    internal static bool ShouldWriteInstallReceipt(string currentVersion, string targetVersion) =>
        !IsVersionDowngrade(currentVersion, targetVersion);

    internal static void DeleteInstallReceiptForDowngrade(string? dataDir = null)
    {
        var receiptPath = Path.Combine(dataDir ?? AppPaths.DataDir, ".update-installed-version");
        if (!File.Exists(receiptPath))
            return;
        File.Delete(receiptPath);
        if (File.Exists(receiptPath))
            throw new IOException(Strings.DowngradeReceiptCleanupFailed);
    }

    internal static string? BackupConfigForDowngrade(
        string targetVersion,
        string? configPath = null)
    {
        if (!TryParseSemVer(targetVersion, out _))
            throw new InvalidOperationException(Strings.DowngradeInvalidVersion);

        configPath ??= AppPaths.ConfigYamlPath;
        if (!File.Exists(configPath))
            return null;

        var backupPath =
            $"{configPath}.before-downgrade-to-{targetVersion}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}";
        using var source = new FileStream(
            configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var destination = AppPaths.CreatePrivateFile(backupPath);
        source.CopyTo(destination);
        destination.Flush(flushToDisk: true);
        return backupPath;
    }

    public static string? CheckInstallReceipt(string currentVersion)
    {
        try
        {
            var receiptPath = Path.Combine(AppPaths.DataDir, ".update-installed-version");
            if (!File.Exists(receiptPath))
                return null;

            var lines = File.ReadAllLines(receiptPath);
            if (lines.Length < 2) { TryDelete(receiptPath); return null; }
            var previousVersion = lines[1].Trim();

            if (!TryParseSemVer(previousVersion, out var prev) ||
                !TryParseSemVer(currentVersion, out var cur))
            {
                TryDelete(receiptPath);
                return null;
            }

            if (cur.CompareTo(prev) > 0)
            {
                TryDelete(receiptPath);
                return null;
            }

            var updateLogPath = Path.Combine(AppPaths.LogsDir, "update.log");
            return $"Last update attempt did not take effect. Still running {currentVersion}. " +
                   $"See {updateLogPath} for details.";
        }
        catch { return null; }

        static void TryDelete(string p) { try { File.Delete(p); } catch { } }
    }

    private void TryWriteInstallReceipt(string logPath, Action<string> log)
    {
        try
        {
            var receiptPath = Path.Combine(AppPaths.DataDir, ".update-installed-version");
            File.WriteAllText(receiptPath, $"{DateTime.UtcNow:o}\n{_currentVersion}\n");
            log($"Receipt written: {receiptPath}");
        }
        catch (Exception ex)
        {
            log($"Receipt write failed: {ex.Message}");
        }
    }

    private void TryWriteInstallReceipt()
    {
        try
        {
            var receiptPath = Path.Combine(AppPaths.DataDir, ".update-installed-version");
            File.WriteAllText(receiptPath, $"{DateTime.UtcNow:o}\n{_currentVersion}\n");
        }
        catch
        {
        }
    }
}
