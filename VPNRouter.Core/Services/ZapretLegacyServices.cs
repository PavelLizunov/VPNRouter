using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace VPNRouter.Core.Services;

public static class ZapretLegacyServices
{
    private const string ChoiceKey = "VPNRouterMigrationV1";
    private const string StrategyKey = "zapret-discord-youtube";
    private const string Pending = "Zapret service choice retained. Run VPNRouter.App.exe --migrate-zapret as administrator to acquire the verified package and retry.";
    private const string Conflict = "Zapret migration is blocked by a conflicting service, driver or winws process. Resolve its ownership and run VPNRouter.App.exe --migrate-zapret.";
    internal sealed record Result(bool SafeForReboot, string? Message = null, bool NeedsPackage = false);
    internal sealed record Strategy(string Name, Func<string[]> ResolveArguments);

    public static Task RetireAsync(ILogger logger, CancellationToken cancellationToken = default) =>
        RetryAsync(logger, cancellationToken);

    // Only the explicit installer/repair command may download a package on behalf of the owner.
    internal static Task<Result> RepairAsync(ILogger logger, string? selection, CancellationToken ct = default) =>
        RepairAsync(() => RetryAsync(logger, ct, selection),
            () => new ZapretUpdater(logger).DownloadAndExtractAsync(ct), logger);

    internal static async Task<Result> RepairAsync(Func<Task<Result>> migrate, Func<Task> acquire, ILogger logger)
    {
        var result = await migrate().ConfigureAwait(false);
        if (!result.NeedsPackage) return result;
        try
        {
            // RetryAsync has released the installation lock. The downloader owns it during publication.
            await acquire().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning(ex, "[Zapret] Verified package acquisition failed during migration");
            return result with { Message = "Verified Zapret package download failed. " + Pending };
        }
        return await migrate().ConfigureAwait(false);
    }

    internal static Task<Result> RetryAsync(ILogger logger, CancellationToken ct = default, string? selection = null) =>
        Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows() || ZapretRuntimeDirectory.TestRoot != null) return new Result(true);
            try
            {
                var paths = ZapretServiceOwnership.Current();
                using var registration = WindowsServiceRegistration.Open(ZapretServiceOwnership.Service, false);
                bool hasService = registration != null && paths.Owns(ZapretServiceOwnership.Service, registration.Config) &&
                    (ZapretServiceOwnership.Matches(ZapretServiceOwnership.Service, registration.Config.Image, paths.LegacyBin) ||
                     registration.ReadValue(ChoiceKey) != null);
                if (!hasService && !ZapretServiceOwnership.DriverNames.Any(name =>
                    {
                        using var driver = WindowsServiceRegistration.Open(name, false);
                        return driver != null && ZapretServiceOwnership.Matches(name, driver.Config.Image, paths.LegacyBin);
                    })) return new Result(true);

                // Serialize service restoration with package replacement as well as other migrations.
                var root = ZapretRuntimeDirectory.Root;
                using var migrationLock = ZapretRuntimeDirectory.LockInstallation(root);
                var result = Migrate(paths, WindowsServiceRegistration.Open, InstalledStrategies, selection, HasForeignWinws);
                if (result.Message != null) logger.Warning("[Zapret] {Message}", result.Message);
                return result;
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[Zapret] Could not finish Zapret service migration");
                return new Result(false, "Could not finish Zapret service migration. Run VPNRouter.App.exe --migrate-zapret as administrator before restarting Windows.");
            }
        }, ct);

    private static bool HasForeignWinws(ZapretServiceOwnership paths)
    {
        var candidates = System.Diagnostics.Process.GetProcessesByName("winws");
        try
        {
            foreach (var process in candidates)
            {
                try
                {
                    _ = process.Handle;
                    if (process.HasExited) continue;
                    var image = ProcessOwnership.ImagePathOf(process);
                    if (!string.Equals(image, Path.Combine(paths.ProtectedBin, "winws.exe"), StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(image, Path.Combine(paths.LegacyBin, "winws.exe"), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (InvalidOperationException) { /* exited during inspection */ }
                catch (System.ComponentModel.Win32Exception) { return true; }
            }
            return false;
        }
        finally { foreach (var process in candidates) process.Dispose(); }
    }

    private static IReadOnlyList<Strategy> InstalledStrategies()
    {
        if (!ZapretUpdater.IsInstalled()) return [];
        return ZapretUpdater.ParseStrategies()
            .Where(s => ValidName(s.Name) && s.BatPath != null)
            .Select(s => new Strategy(s.Name, () => ZapretManager.ResolveInstalledStrategyArguments(s.BatPath!))).ToList();
    }

    internal static Result Migrate(ZapretServiceOwnership paths, Func<string, bool, IWindowsRegistration?> open,
        Func<IReadOnlyList<Strategy>> installed, string? selection = null, Func<ZapretServiceOwnership, bool>? processConflict = null)
    {
        const string service = ZapretServiceOwnership.Service;
        using var lookup = open(service, false);
        var initial = lookup?.Config;
        if ((initial != null && !paths.Owns(service, initial)) ||
            ZapretActions.HasDriverConflict(paths, open, allowLegacy: true) || processConflict?.Invoke(paths) == true)
            return new(false, Conflict);

        using var registration = lookup == null ? null : open(service, true);
        if (registration?.Config != initial) return new(false, Conflict);
        string[]? choice = null;
        string exe = Path.Combine(paths.ProtectedBin, "winws.exe");
        string parked = "\"" + exe + "\"";
        if (registration != null && paths.Owns(service, registration.Config))
        {
            var config = registration.Config;
            bool legacy = ZapretServiceOwnership.Matches(service, config.Image, paths.LegacyBin);
            choice = registration.ReadValue(ChoiceKey) as string[];
            if (legacy && choice == null)
            {
                string? name = registration.ReadValue(StrategyKey) as string;
                var original = FirewallManager.SplitShellArgs(config.Image);
                var snapshot = registration.ReadValue("VPNRouterPreUpgradeState") as int?;
                uint originalStart = snapshot is int v ? (uint)(v & 15) : config.Start;
                bool originallyActive = snapshot is int state ? (state & 16) != 0 : registration.IsActive;
                choice = [originalStart.ToString(System.Globalization.CultureInfo.InvariantCulture), originallyActive ? "1" : "0",
                    ValidName(name) ? name! : "", ArgumentHash(original.Skip(1), paths.LegacyBin)];
                registration.WriteChoice(ChoiceKey, choice);
            }
            if (legacy)
            {
                // Change both fields in one SCM operation before stopping. A crash from here
                // onwards cannot launch the writable executable at the next boot.
                registration.Configure(parked, 4);
            }
            if (legacy || (choice != null && config.Image == parked && config.Start == 4))
            {
                if (registration.Config != new ServiceConfig(parked, 0x10, 4))
                    return new(false, "The Zapret service changed before it could be stopped.");
                registration.Stop();
            }
        }

        foreach (var name in ZapretServiceOwnership.DriverNames)
        {
            using var driver = open(name, false);
            if (driver == null) continue;
            var config = driver.Config;
            var file = ZapretServiceOwnership.FileName(name, config.Image, paths.LegacyBin);
            if (file != null && config.Type == 1)
            {
                using var writable = open(name, true);
                if (writable == null || writable.Config != config)
                    throw new IOException("WinDivert configuration changed during migration.");
                // Drivers are shared. Never unload a running driver or delete its registration.
                // Redirect future loads of this edition's old driver to the protected location.
                var saved = writable.ReadValue("VPNRouterPreUpgradeState") as int?;
                bool disabled = saved is int v ? (v & 15) == 4 : config.Start == 4;
                writable.Configure(Path.Combine(paths.ProtectedBin, file), disabled ? 4u : 3u);
                writable.WriteChoice("VPNRouterPreUpgradeState", null);
            }
            else if (!paths.Owns(name, config))
                return new(false, Conflict);
        }

        if (choice == null || registration == null) return new(true);
        if (ZapretActions.HasDriverConflict(paths, open) || processConflict?.Invoke(paths) == true) return new(false, Conflict);
        if (choice.Length != 4 || choice[0] is not ("2" or "3" or "4") || choice[1] is not ("0" or "1"))
            return new(true, Pending);
        string image;
        try
        {
            string preferred = selection ?? choice[2];
            var strategies = installed();
            if (strategies.Count == 0) return new(true, Pending, NeedsPackage: true);
            var match = strategies.Where(s => ValidName(s.Name) && (preferred.Length != 0
                ? string.Equals(s.Name, preferred, StringComparison.OrdinalIgnoreCase)
                : ArgumentHash(s.ResolveArguments(), paths.ProtectedBin) == choice[3])).Take(2).ToList();
            if (match.Count != 1)
            {
                var names = string.Join(", ", strategies.Select(s => s.Name).Where(ValidName));
                return new(true, "Run VPNRouter.App.exe --migrate-zapret \"strategy name\" as administrator. Installed strategies: " + names);
            }
            var verified = match[0].ResolveArguments();
            if (verified.Length == 0) return new(true, Pending);
            image = ZapretActions.ServiceImage(exe, verified);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or ZapretDownloadException)
        {
            return new(true, Pending);
        }
        var current = registration.Config;
        if (!paths.Owns(service, current)) return new(false, "The Zapret service owner changed during migration.");
        uint start = uint.Parse(choice[0], System.Globalization.CultureInfo.InvariantCulture);
        if (current.Image != image || current.Start != start)
        {
            if (current.Image != parked || current.Start != 4) return new(true, "The Zapret service changed. Review its settings before retrying migration.");
            registration.Configure(image, start);
        }
        if (registration.Config != new ServiceConfig(image, 0x10, start))
            return new(false, "The Zapret service changed before migration finished.");
        if (start != 4 && choice[1] == "1" && !registration.IsActive) registration.Start();
        registration.WriteChoice("VPNRouterPreUpgradeState", null);
        registration.WriteChoice(ChoiceKey, null);
        return new(true);
    }

    internal static bool MatchesLegacyImage(string name, string? image, string legacyBin) =>
        ZapretServiceOwnership.Matches(name, image, legacyBin);

    private static bool ValidName(string? value) => value is { Length: > 0 and <= 128 } &&
        value.StartsWith("general", StringComparison.OrdinalIgnoreCase) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '(' or ')' or '_' or '-' or '+');

    private static string ArgumentHash(IEnumerable<string> args, string bin)
    {
        string package = Path.GetDirectoryName(bin)! + Path.DirectorySeparatorChar;
        var normalized = args.Select(arg => arg.Replace(package, "%PACKAGE%", StringComparison.OrdinalIgnoreCase));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", normalized))));
    }

}
