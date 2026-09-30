#nullable enable

using System.Text;
using Serilog;

namespace VPNRouter.Core.Services;

public enum CleanupOutcome
{
    Removed,
    WouldRemove,
    NotPresent,
    Skipped,
    Failed,
}

public sealed record CleanupAction(string Area, string Target, CleanupOutcome Outcome, string? Detail = null);

public sealed class CleanupReport
{
    public CleanupReport(bool dryRun, IReadOnlyList<CleanupAction> actions)
    {
        DryRun = dryRun;
        Actions = actions;
    }

    public bool DryRun { get; }

    public IReadOnlyList<CleanupAction> Actions { get; }

    public bool HasFailures => Actions.Any(a => a.Outcome == CleanupOutcome.Failed);

    public int Count(CleanupOutcome outcome) => Actions.Count(a => a.Outcome == outcome);

    public string Format()
    {
        var sb = new StringBuilder();
        foreach (var a in Actions)
        {
            var mark = a.Outcome switch
            {
                CleanupOutcome.Removed => "removed",
                CleanupOutcome.WouldRemove => "would remove",
                CleanupOutcome.NotPresent => "not present",
                CleanupOutcome.Skipped => "skipped",
                _ => "FAILED",
            };
            sb.Append($"[{mark}] {a.Area}: {a.Target}");
            if (!string.IsNullOrEmpty(a.Detail)) sb.Append($" ({a.Detail})");
            sb.AppendLine();
        }
        sb.Append(DryRun
            ? $"Dry run: {Count(CleanupOutcome.WouldRemove)} item(s) would be removed, nothing was changed."
            : $"Removed {Count(CleanupOutcome.Removed)}, already clean {Count(CleanupOutcome.NotPresent)}, skipped {Count(CleanupOutcome.Skipped)}, failed {Count(CleanupOutcome.Failed)}.");
        return sb.ToString();
    }
}

/// <summary>The two registry-backed steps of the cleanup, behind a seam so tests never touch the real registry.</summary>
public interface ICleanupRegistry
{
    bool DnsHardeningStatePresent();

    void RestoreDnsHardening();

    bool AutostartValuePresent();

    void RemoveAutostartValue();
}

/// <summary>
/// Removes what VPNRouter leaves in Windows outside its install folder: firewall rules, DNS hardening, its own driver
/// services and the per-user autostart value. Idempotent, prints what it did, and a dry run changes nothing. Every
/// system call goes through <see cref="IProcessRunner"/> or <see cref="ICleanupRegistry"/>.
/// </summary>
public sealed class SystemCleanup
{
    internal const string SplitTunnelService = "mullvad-split-tunnel";

    // Service names are case-insensitive, so "windivert" is the same service as "WinDivert".
    internal static readonly string[] ZapretServices = { "zapret", "WinDivert", "WinDivert14", "WinDivert15" };

    private readonly IProcessRunner _runner;
    private readonly ICleanupRegistry _registry;
    private readonly ILogger _logger;
    private readonly string _scPath;

    public SystemCleanup(IProcessRunner runner, ICleanupRegistry registry, ILogger? logger = null, string? scPath = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? Log.Logger;
        _scPath = scPath ?? (OperatingSystem.IsWindows() ? WindowsServiceCommand.GetSystemScPath() : "sc");
    }

    public CleanupReport Run(bool dryRun)
    {
        var actions = new List<CleanupAction>();

        // The firewall goes first so DNS is reachable again before the DNS settings are restored.
        Step(actions, "firewall", () => CleanFirewall(dryRun, actions));
        Step(actions, "dns", () => CleanDns(dryRun, actions));
        Step(actions, "driver", () => CleanService(SplitTunnelService, dryRun, actions, "split-tunnel driver", requireOwnDriverPath: true));
        foreach (var name in ZapretServices)
            Step(actions, "zapret", () => CleanService(name, dryRun, actions, "Zapret/WinDivert driver", requireOwnDriverPath: false));
        Step(actions, "autostart", () => CleanAutostart(dryRun, actions));

        return new CleanupReport(dryRun, actions);
    }

    private void Step(List<CleanupAction> actions, string area, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Cleanup] {Area} step failed", area);
            actions.Add(new CleanupAction(area, "step", CleanupOutcome.Failed, ex.GetType().Name + ": " + ex.Message));
        }
    }

    private void CleanFirewall(bool dryRun, List<CleanupAction> actions)
    {
        // Not disposed on purpose: its Dispose only deletes rules it created itself (none here) and logs a misleading line.
        var fw = new FirewallManager(_logger, _runner);
        var rules = fw.FindRulesByPrefixes(FirewallManager.ManagedRulePrefixes);
        if (rules.Count == 0)
        {
            actions.Add(new CleanupAction("firewall", "VPNRouter rules", CleanupOutcome.NotPresent));
            return;
        }

        foreach (var rule in rules.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (dryRun)
            {
                actions.Add(new CleanupAction("firewall", rule, CleanupOutcome.WouldRemove));
                continue;
            }
            actions.Add(fw.DeleteRuleByName(rule)
                ? new CleanupAction("firewall", rule, CleanupOutcome.Removed)
                : new CleanupAction("firewall", rule, CleanupOutcome.Failed, "netsh could not delete the rule"));
        }
    }

    private void CleanDns(bool dryRun, List<CleanupAction> actions)
    {
        if (!_registry.DnsHardeningStatePresent())
        {
            actions.Add(new CleanupAction("dns", "saved DNS settings", CleanupOutcome.NotPresent));
            return;
        }
        if (dryRun)
        {
            actions.Add(new CleanupAction("dns", "saved DNS settings", CleanupOutcome.WouldRemove, "would restore the original values"));
            return;
        }
        _registry.RestoreDnsHardening();
        actions.Add(_registry.DnsHardeningStatePresent()
            ? new CleanupAction("dns", "saved DNS settings", CleanupOutcome.Failed, "the saved state is still there after the restore")
            : new CleanupAction("dns", "saved DNS settings", CleanupOutcome.Removed, "original values restored"));
    }

    private void CleanAutostart(bool dryRun, List<CleanupAction> actions)
    {
        if (!_registry.AutostartValuePresent())
        {
            actions.Add(new CleanupAction("autostart", "current user's Run value", CleanupOutcome.NotPresent));
            return;
        }
        if (dryRun)
        {
            actions.Add(new CleanupAction("autostart", "current user's Run value", CleanupOutcome.WouldRemove));
            return;
        }
        _registry.RemoveAutostartValue();
        actions.Add(_registry.AutostartValuePresent()
            ? new CleanupAction("autostart", "current user's Run value", CleanupOutcome.Failed, "the value is still there")
            : new CleanupAction("autostart", "current user's Run value", CleanupOutcome.Removed));
    }

    private void CleanService(string name, bool dryRun, List<CleanupAction> actions, string what, bool requireOwnDriverPath)
    {
        var query = RunSc("query", name);
        if (!ServiceExists(query))
        {
            actions.Add(new CleanupAction("service", name, CleanupOutcome.NotPresent));
            return;
        }

        // Another program can ship a service with the same name (Mullvad's split-tunnel driver, other WinDivert users):
        // only a service whose binary lives in a VPNRouter folder is ours to remove.
        var binPath = ParseBinaryPath(RunSc("qc", name).Stdout);
        if (!IsOwnBinaryPath(binPath, requireOwnDriverPath))
        {
            actions.Add(new CleanupAction("service", name, CleanupOutcome.Skipped,
                string.IsNullOrEmpty(binPath) ? "cannot read its binary path, left alone" : $"{what} belongs to another program: {binPath}"));
            return;
        }

        if (dryRun)
        {
            actions.Add(new CleanupAction("service", name, CleanupOutcome.WouldRemove, $"stop and delete the {what}"));
            return;
        }

        RunSc("stop", name);
        var delete = RunSc("delete", name);
        if (delete.ExitCode == 0)
            actions.Add(new CleanupAction("service", name, CleanupOutcome.Removed));
        else if (delete.ExitCode == 1072)
            actions.Add(new CleanupAction("service", name, CleanupOutcome.Removed, "marked for deletion, it disappears when nothing holds it open"));
        else
            actions.Add(new CleanupAction("service", name, CleanupOutcome.Failed, $"sc delete exit {delete.ExitCode}"));
    }

    private ProcessResult RunSc(params string[] args) =>
        _runner.RunAsync(new ProcessRequest(_scPath, args, Timeout: TimeSpan.FromSeconds(10))).GetAwaiter().GetResult();

    internal static bool ServiceExists(ProcessResult query) =>
        !query.TimedOut
        && query.ExitCode != 1060
        && (query.Stdout.Contains("SERVICE_NAME", StringComparison.OrdinalIgnoreCase)
            || query.Stdout.Contains("STATE", StringComparison.OrdinalIgnoreCase));

    internal static string? ParseBinaryPath(string scQcOutput)
    {
        foreach (var raw in scQcOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase)) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) return null;
            var value = line[(colon + 1)..].Trim();
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    internal static bool IsOwnBinaryPath(string? binPath, bool requireOwnDriverPath)
    {
        if (string.IsNullOrWhiteSpace(binPath)) return false;
        if (requireOwnDriverPath)
            return SplitTunnelPolicy.ClassifyServiceBinPath(binPath, string.Empty)
                   == SplitTunnelDriverProtocol.ServiceCollisionAction.AdoptMovedInstall;
        var normalized = binPath.Replace('/', '\\').Trim('"', ' ').ToLowerInvariant();
        return normalized.Contains(@"\vpnrouter\", StringComparison.Ordinal);
    }
}

public sealed class WindowsCleanupRegistry : ICleanupRegistry
{
    public bool DnsHardeningStatePresent() =>
#if PLATFORM_WINDOWS
        WindowsDnsHardening.HasSavedState();
#else
        false;
#endif

    public void RestoreDnsHardening()
    {
#if PLATFORM_WINDOWS
        WindowsDnsHardening.Restore();
#endif
    }

    public bool AutostartValuePresent() => OperatingSystem.IsWindows() && VPNRouter.Core.Platform.AutostartHelper.IsEnabled();

    public void RemoveAutostartValue()
    {
        if (OperatingSystem.IsWindows()) VPNRouter.Core.Platform.AutostartHelper.Disable();
    }
}
