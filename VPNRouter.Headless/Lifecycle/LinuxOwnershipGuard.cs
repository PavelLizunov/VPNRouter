#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Services;

namespace VPNRouter.Headless.Lifecycle;

public enum OwnershipStatus
{
    Free,
    HeldByAnother,
    Unavailable
}

public readonly record struct OwnershipCheckResult(
    OwnershipStatus Status,
    int? ConflictingPid = null,
    string? ConflictingProcessName = null,
    string? Details = null)
{
    public static OwnershipCheckResult Free() => new(OwnershipStatus.Free);

    public static OwnershipCheckResult HeldByAnother(int pid, string? name, string? details = null)
        => new(OwnershipStatus.HeldByAnother, pid, name, details);

    public static OwnershipCheckResult Unavailable(string details)
        => new(OwnershipStatus.Unavailable, null, null, details);
}

/// <summary>
/// Safeguards process exclusivity on Linux where TunOwnershipLock's named semaphore fails open.
/// Cross-validates runtime-owner records, live sing-box ownership, and active GUI/CLI processes
/// using exact ProcessOwnership APIs.
///
/// <para><strong>Concurrency Notice</strong>: Point-in-time PID, process name, and file probing is
/// inherently non-atomic and vulnerable to race conditions (TOCTOU). True concurrency guarantees
/// require a shared synchronization lock (e.g. coordinated file lock); this probe does NOT claim
/// an atomic concurrency guarantee.</para>
/// </summary>
public static class LinuxOwnershipGuard
{
    private const string RuntimeOwnerFileName = "runtime-owner.json";

    /// <summary>
    /// Probe whether another VPNRouter process currently owns the TUN or sing-box.
    /// Does not mutate state or acquire locks.
    /// Notice: This check is non-atomic and requires a shared lock for true mutual exclusion.
    /// </summary>
    public static OwnershipCheckResult CheckOwnership(ILogger? logger = null)
    {
        logger?.Debug("[OwnershipGuard] Probing ownership state. Note: Inspection is non-atomic and requires a shared lock; no concurrency guarantee is claimed.");

        // 1. Check native TunOwnershipLock (flock on Linux, semaphore on Windows)
        var lockStatus = TunOwnershipLock.ProbeOwnership();
        if (lockStatus == TunOwnershipStatus.Owned)
        {
            // If the current process already owns the lock, it is self-owned, not held by another.
            var isOwnedBySelf = TunOwnershipLock.Instance().HasOwnership;
            if (!isOwnedBySelf)
            {
                logger?.Information("[OwnershipGuard] TunOwnershipLock is owned by another process");
                return OwnershipCheckResult.HeldByAnother(0, "VPNRouter (TunLock)", "TunOwnershipLock is owned by another process.");
            }
        }
        else if (lockStatus == TunOwnershipStatus.Unavailable)
        {
            logger?.Warning("[OwnershipGuard] TunOwnershipLock probe unavailable; failing closed.");
            return OwnershipCheckResult.Unavailable("TunOwnershipLock could not be verified.");
        }

        // 2. Probe durable runtime-owner.json record via exact ProcessOwnership APIs
        try
        {
            var ownerRecordPath = Path.Combine(AppPaths.DataDir, RuntimeOwnerFileName);
            var ownerRead = ProcessOwnership.ReadRuntimeOwnerRecord(ownerRecordPath);

            switch (ownerRead.Kind)
            {
                case RuntimeOwnerRecordKind.Missing:
                    break;

                case RuntimeOwnerRecordKind.Malformed:
                    // Fail closed on malformed or corrupt record
                    logger?.Warning("[OwnershipGuard] Runtime owner record at {Path} is malformed or unverified; failing closed.", ownerRecordPath);
                    return OwnershipCheckResult.Unavailable("Runtime owner record is malformed or unverified.");

                case RuntimeOwnerRecordKind.CurrentV2:
                    if (ownerRead.Record is { } v2)
                    {
                        // Check if the owner in the record is ourselves
                        bool isSelfOwner = false;
                        if (v2.OwnerPid == Environment.ProcessId)
                        {
                            try
                            {
                                using var selfProc = Process.GetCurrentProcess();
                                var selfIdentity = ProcessOwnership.TryReadProcessIdentity(selfProc);
                                if (selfIdentity is { } self && self.StartedAtUtcTicks == v2.OwnerStartedAtUtcTicks)
                                {
                                    isSelfOwner = true;
                                }
                            }
                            catch { }
                        }

                        if (isSelfOwner)
                        {
                            // The owner record belongs to this exact process instance; do not treat own child as foreign.
                            break;
                        }

                        // Foreign owner: check if foreign owner process is alive
                        if (v2.OwnerPid > 0)
                        {
                            try
                            {
                                using var ownerProc = Process.GetProcessById(v2.OwnerPid);
                                var ownerIdentity = ProcessOwnership.TryReadProcessIdentity(ownerProc);
                                if (ownerIdentity is { } liveOwner
                                    && liveOwner.StartedAtUtcTicks == v2.OwnerStartedAtUtcTicks)
                                {
                                    var procName = Path.GetFileNameWithoutExtension(liveOwner.ExecutablePath);
                                    logger?.Warning(
                                        "[OwnershipGuard] Active runtime owner detected: PID {Pid} ({Name})",
                                        v2.OwnerPid,
                                        procName);
                                    return OwnershipCheckResult.HeldByAnother(
                                        v2.OwnerPid,
                                        procName,
                                        $"Active VPNRouter owner PID {v2.OwnerPid} ({procName}) is alive.");
                                }
                            }
                            catch (ArgumentException) { }
                            catch (InvalidOperationException) { }
                        }

                        // Foreign child: check if active child sing-box process from foreign record is alive
                        if (v2.ChildPid > 0)
                        {
                            try
                            {
                                using var childProc = Process.GetProcessById(v2.ChildPid);
                                var childIdentity = ProcessOwnership.TryReadProcessIdentity(childProc);
                                if (childIdentity is { } liveChild
                                    && liveChild.StartedAtUtcTicks == v2.ChildStartedAtUtcTicks
                                    && ProcessOwnership.IsSamePath(liveChild.ExecutablePath, v2.ExecutablePath))
                                {
                                    var childName = Path.GetFileNameWithoutExtension(liveChild.ExecutablePath);
                                    logger?.Warning(
                                        "[OwnershipGuard] Active child sing-box detected from record: PID {Pid}",
                                        v2.ChildPid);
                                    return OwnershipCheckResult.HeldByAnother(
                                        v2.ChildPid,
                                        childName,
                                        $"sing-box PID {v2.ChildPid} ({childName}) is running from runtime-owner.json.");
                                }
                            }
                            catch (ArgumentException) { }
                            catch (InvalidOperationException) { }
                        }
                    }
                    break;

                case RuntimeOwnerRecordKind.LegacyV1:
                    if (ownerRead.Record is { } v1 && v1.ChildPid > 0 && v1.ChildPid != Environment.ProcessId)
                    {
                        try
                        {
                            using var childProc = Process.GetProcessById(v1.ChildPid);
                            var childIdentity = ProcessOwnership.TryReadProcessIdentity(childProc);
                            if (childIdentity is { } liveChild
                                && ProcessOwnership.IsSamePath(liveChild.ExecutablePath, v1.ExecutablePath))
                            {
                                var childName = Path.GetFileNameWithoutExtension(liveChild.ExecutablePath);
                                logger?.Warning("[OwnershipGuard] Active legacy sing-box detected: PID {Pid}", v1.ChildPid);
                                return OwnershipCheckResult.HeldByAnother(
                                    v1.ChildPid,
                                    childName,
                                    $"Legacy sing-box PID {v1.ChildPid} is running.");
                            }
                        }
                        catch (ArgumentException) { }
                        catch (InvalidOperationException) { }
                    }
                    break;

                default:
                    // Fail closed on unknown record schema kind
                    logger?.Warning("[OwnershipGuard] Unknown runtime owner record kind: {Kind}; failing closed.", ownerRead.Kind);
                    return OwnershipCheckResult.Unavailable($"Unknown runtime owner record kind: {ownerRead.Kind}");
            }

            // Cross-validate with authoritative ProcessOwnership detection
            var ownedSingBox = ProcessOwnership.FindOwnedSingBox(null);
            if (ownedSingBox is { } ownedChild)
            {
                bool isSelfChild = false;
                if (ownerRead.Kind == RuntimeOwnerRecordKind.CurrentV2 && ownerRead.Record is { } currentV2)
                {
                    if (currentV2.OwnerPid == Environment.ProcessId && currentV2.ChildPid == ownedChild.Pid)
                    {
                        isSelfChild = true;
                    }
                }

                if (!isSelfChild && ownedChild.Pid != Environment.ProcessId)
                {
                    var name = Path.GetFileNameWithoutExtension(ownedChild.ExecutablePath);
                    logger?.Warning("[OwnershipGuard] Active foreign owned sing-box detected via ProcessOwnership: PID {Pid}", ownedChild.Pid);
                    return OwnershipCheckResult.HeldByAnother(
                        ownedChild.Pid,
                        name,
                        $"Active owned sing-box process PID {ownedChild.Pid} ({name}) is running.");
                }
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[OwnershipGuard] Error validating runtime owner record; failing closed.");
            return OwnershipCheckResult.Unavailable("Failed to verify runtime owner record.");
        }

        return OwnershipCheckResult.Free();
    }
}
