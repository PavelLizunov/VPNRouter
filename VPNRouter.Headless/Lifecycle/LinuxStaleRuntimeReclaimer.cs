using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Services;

namespace VPNRouter.Headless.Lifecycle;

/// <summary>Recovers default-data Linux runtimes left behind by a legacy helper or upgrade.</summary>
internal static class LinuxStaleRuntimeReclaimer
{
    private const string DeletedSuffix = " (deleted)";
    private const int SigTerm = 15;
    private const int SigKill = 9;
    private const long SysPidFdOpen = 434;
    private const long SysPidFdSendSignal = 424;
    private static readonly TimeSpan HelperExitBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan EngineExitBudget = TimeSpan.FromSeconds(5);

    internal readonly record struct ProcInfo(
        int Pid, int ParentPid, uint Uid, long StartedAtUtcTicks, string Executable, string[] Args);

    public static async Task ReclaimAsync(ILogger? logger, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return;
        var helperPath = NormalizeExecutable(Environment.ProcessPath);
        if (string.IsNullOrEmpty(helperPath)) return;
        var selfPid = Environment.ProcessId;

        try
        {
            var processes = ReadProcesses();
            if (!processes.Any(p => p.Pid == selfPid)) return;
            var self = processes.First(p => p.Pid == selfPid);

            var helpers = StaleHelpers(processes, selfPid, self.Uid, helperPath);
            foreach (var helper in helpers)
            {
                ct.ThrowIfCancellationRequested();
                logger?.Warning("[StaleRuntime] Stopping stale Headless helper PID {Pid}", helper.Pid);
                Signal(helper, SigTerm, logger);
            }
            if (helpers.Count > 0)
                await WaitGoneAsync(helpers, HelperExitBudget, ct).ConfigureAwait(false);

            // Preserve a live recorded owner even if it is not a Headless binary.
            processes = ReadProcesses();
            var owner = ProcessOwnership.ReadRuntimeOwnerRecord(Path.Combine(AppPaths.DataDir, "runtime-owner.json"));
            var liveOwnerPid = owner.Kind == RuntimeOwnerRecordKind.CurrentV2 && owner.Record is { } record
                && processes.Any(p => p.Pid == record.OwnerPid && p.Uid == self.Uid
                                      && p.StartedAtUtcTicks == record.OwnerStartedAtUtcTicks)
                    ? record.OwnerPid : 0;
            var engines = OrphanEngines(processes, selfPid, self.Uid, helperPath,
                SingBoxRuntimePolicy.PackagedExecutablePath, AppPaths.CurrentConfigPath, liveOwnerPid);
            foreach (var engine in engines)
            {
                ct.ThrowIfCancellationRequested();
                logger?.Warning("[StaleRuntime] Stopping orphaned sing-box PID {Pid}", engine.Pid);
                Signal(engine, SigTerm, logger);
            }
            if (engines.Count > 0 && !await WaitGoneAsync(engines, EngineExitBudget, ct).ConfigureAwait(false))
            {
                foreach (var engine in engines)
                {
                    ct.ThrowIfCancellationRequested();
                    Signal(engine, SigKill, logger);
                }
                await WaitGoneAsync(engines, TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger?.Warning("[StaleRuntime] Reclaim skipped: {Message}", BoundedTeardown.SanitizeExceptionMessage(ex));
        }
    }

    internal static List<ProcInfo> StaleHelpers(IReadOnlyList<ProcInfo> processes, int selfPid, uint uid, string helperPath)
        => processes.Where(p => p.Pid != selfPid && p.Uid == uid && p.Executable == helperPath
                                && p.Args.Length == 2 && p.Args[1] == "--stdio")
                    .ToList();

    internal static List<ProcInfo> OrphanEngines(
        IReadOnlyList<ProcInfo> processes, int selfPid, uint uid, string helperPath,
        string enginePath, string configPath, int liveOwnerPid)
    {
        var liveHelpers = processes.Where(p => p.Uid == uid && p.Executable == helperPath)
                                   .Select(p => p.Pid)
                                   .ToHashSet();
        return processes.Where(p => p.Uid == uid && p.Args.Length == 4 && p.Args[0] == enginePath
                                    && p.Args[1] == "run" && p.Args[2] == "-c" && p.Args[3] == configPath
                                    && p.ParentPid != selfPid && p.ParentPid != liveOwnerPid
                                    && !liveHelpers.Contains(p.ParentPid))
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

            using var process = Process.GetProcessById(pid);
            var startedAt = process.StartTime.ToUniversalTime().Ticks;
            var args = File.ReadAllText($"{dir}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
            // File-capability sing-box is non-dumpable; its exe link can be unreadable.
            string? executable = null;
            try { executable = new FileInfo($"{dir}/exe").LinkTarget; }
            catch { }
            executable = NormalizeExecutable(executable) ?? (args.Length > 0 ? args[0] : string.Empty);
            return new ProcInfo(pid, parent.Value, uid.Value, startedAt, executable, args);
        }
        catch { return null; }
    }

    private static bool SameIdentity(ProcInfo expected, ProcInfo current)
        => current.Uid == expected.Uid && current.StartedAtUtcTicks == expected.StartedAtUtcTicks
           && current.Executable == expected.Executable && current.Args.SequenceEqual(expected.Args);

    private static void Signal(ProcInfo expected, int signal, ILogger? logger)
    {
        // Pin the process before rechecking identity, including on escalation.
        var rawFd = PidFdOpen(SysPidFdOpen, expected.Pid, 0);
        if (rawFd < 0 || rawFd > int.MaxValue) return;
        using var pidFd = new SafePidFd((int)rawFd);
        if (TryReadProcess(expected.Pid) is not { } current || !SameIdentity(expected, current)) return;
        if (PidFdSendSignal(SysPidFdSendSignal, pidFd, signal, IntPtr.Zero, 0) != 0)
            logger?.Warning("[StaleRuntime] Signal failed for PID {Pid}: errno {Errno}",
                expected.Pid, Marshal.GetLastPInvokeError());
    }

    private static async Task<bool> WaitGoneAsync(IReadOnlyList<ProcInfo> targets, TimeSpan budget, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (targets.All(t => TryReadProcess(t.Pid) is not { } p || !SameIdentity(t, p))) return true;
            if (timer.Elapsed >= budget) return false;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    private sealed class SafePidFd : SafeHandleMinusOneIsInvalid
    {
        internal SafePidFd(int fd) : base(ownsHandle: true) => SetHandle((IntPtr)fd);
        protected override bool ReleaseHandle() => CloseFd(handle.ToInt32()) == 0;
    }

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseFd(int fd);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long PidFdOpen(long number, int pid, uint flags);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long PidFdSendSignal(long number, SafePidFd pidFd, int signal, IntPtr info, uint flags);
}
