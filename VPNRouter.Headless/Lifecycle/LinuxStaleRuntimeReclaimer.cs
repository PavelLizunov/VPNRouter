using System.Globalization;
using System.Runtime.InteropServices;
using Serilog;
using VPNRouter.Core.Services;

namespace VPNRouter.Headless.Lifecycle;

/// <summary>
/// Stops VPNRouter runtime processes of the same user that an earlier Headless
/// instance left behind: a default-data-dir stdio helper from a previous plugin
/// load, and packaged sing-box processes whose parent is no longer a Headless
/// (for example after a crash, or after a package upgrade replaced the running
/// binaries). Without this the new session sees its own orphan as a foreign owner
/// and refuses to connect while the orphan keeps routing traffic.
/// </summary>
/// <remarks>
/// A sing-box with file capabilities is not dumpable, so its /proc exe link is
/// unreadable; processes are matched by uid, argv[0] and parent instead.
/// </remarks>
internal static class LinuxStaleRuntimeReclaimer
{
    private const string DeletedSuffix = " (deleted)";
    private const int SigTerm = 15;
    private const int SigKill = 9;
    private static readonly TimeSpan HelperExitBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EngineExitBudget = TimeSpan.FromSeconds(5);

    internal readonly record struct ProcInfo(int Pid, int ParentPid, uint Uid, string Executable, string[] Args);

    /// <summary>Returns true when at least one stale process was signalled.</summary>
    public static async Task<bool> ReclaimAsync(ILogger? logger, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return false;
        var helperPath = NormalizeExecutable(Environment.ProcessPath);
        if (string.IsNullOrEmpty(helperPath)) return false;
        var selfPid = Environment.ProcessId;
        var enginePath = SingBoxRuntimePolicy.PackagedExecutablePath;

        try
        {
            var processes = ReadProcesses();
            if (processes.FirstOrDefault(p => p.Pid == selfPid) is not { Pid: > 0 } self) return false;

            var helpers = StaleHelpers(processes, selfPid, self.Uid, helperPath);
            foreach (var helper in helpers)
            {
                logger?.Warning("[StaleRuntime] Stopping stale Headless helper PID {Pid}", helper.Pid);
                Signal(helper, SigTerm);
            }
            if (helpers.Count > 0)
                await WaitGoneAsync(helpers, HelperExitBudget, ct).ConfigureAwait(false);

            // A stopped helper normally stops its own sing-box; whatever is left is orphaned.
            var engines = OrphanEngines(ReadProcesses(), selfPid, self.Uid, helperPath, enginePath);
            foreach (var engine in engines)
            {
                logger?.Warning("[StaleRuntime] Stopping orphaned sing-box PID {Pid}", engine.Pid);
                Signal(engine, SigTerm);
            }
            if (engines.Count > 0 && !await WaitGoneAsync(engines, EngineExitBudget, ct).ConfigureAwait(false))
            {
                foreach (var engine in engines) Signal(engine, SigKill);
                await WaitGoneAsync(engines, TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }

            return helpers.Count > 0 || engines.Count > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Warning("[StaleRuntime] Reclaim skipped: {Message}", BoundedTeardown.SanitizeExceptionMessage(ex));
            return false;
        }
    }

    internal static List<ProcInfo> StaleHelpers(IReadOnlyList<ProcInfo> processes, int selfPid, uint uid, string helperPath)
        => processes.Where(p => p.Pid != selfPid && p.Uid == uid && p.Executable == helperPath
                                && p.Args.Contains("--stdio") && !p.Args.Contains("--data-dir"))
                    .ToList();

    internal static List<ProcInfo> OrphanEngines(
        IReadOnlyList<ProcInfo> processes, int selfPid, uint uid, string helperPath, string enginePath)
    {
        var liveHelpers = processes.Where(p => p.Uid == uid && p.Executable == helperPath)
                                   .Select(p => p.Pid)
                                   .ToHashSet();
        return processes.Where(p => p.Uid == uid && p.Args.Length > 0 && p.Args[0] == enginePath
                                    && p.ParentPid != selfPid && !liveHelpers.Contains(p.ParentPid))
                        .ToList();
    }

    internal static string? NormalizeExecutable(string? path)
        => path is not null && path.EndsWith(DeletedSuffix, StringComparison.Ordinal)
            ? path[..^DeletedSuffix.Length]
            : path;

    private static List<ProcInfo> ReadProcesses()
    {
        var result = new List<ProcInfo>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                continue;
            if (TryReadProcess(pid) is { } info) result.Add(info);
        }
        return result;
    }

    private static ProcInfo? TryReadProcess(int pid)
    {
        try
        {
            var dir = $"/proc/{pid}";
            uint? uid = null;
            int? parent = null;
            foreach (var line in File.ReadLines($"{dir}/status"))
            {
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                    uid = uint.Parse(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
                else if (line.StartsWith("PPid:", StringComparison.Ordinal))
                    parent = int.Parse(line[5..].Trim(), CultureInfo.InvariantCulture);
            }
            if (uid is null || parent is null) return null;

            var args = File.ReadAllText($"{dir}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
            string? executable = null;
            try { executable = new FileInfo($"{dir}/exe").LinkTarget; }
            catch { }
            executable = NormalizeExecutable(executable) ?? (args.Length > 0 ? args[0] : string.Empty);
            return new ProcInfo(pid, parent.Value, uid.Value, executable, args);
        }
        catch
        {
            return null;
        }
    }

    private static void Signal(ProcInfo expected, int signal)
    {
        // Re-read right before signalling so a reused PID is never hit.
        if (TryReadProcess(expected.Pid) is not { } current
            || current.Uid != expected.Uid
            || !current.Args.SequenceEqual(expected.Args))
            return;
        _ = Kill(expected.Pid, signal);
    }

    private static async Task<bool> WaitGoneAsync(IReadOnlyList<ProcInfo> targets, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + budget;
        while (true)
        {
            if (targets.All(t => TryReadProcess(t.Pid) is not { } p || !p.Args.SequenceEqual(t.Args)))
                return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);
}
