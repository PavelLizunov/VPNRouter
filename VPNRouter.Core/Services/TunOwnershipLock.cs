using System;
using System.Threading;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>Tri-state semaphore observation. Unavailable is distinct from Free
/// so status detection can preserve its process-only fail-open behaviour when
/// the platform cannot inspect the named semaphore.</summary>
public enum TunOwnershipStatus
{
    Free,
    Owned,
    Unavailable
}

/// <summary>
/// System-wide named mutex that guarantees only ONE process owns the
/// VPNRouter-TUN adapter at a time.
///
/// Why: Windows allows only a single TUN adapter with a given name.
/// If both VPNRouter.App.exe (desktop UI) and VPNRouter.Service.exe
/// (Windows Service) try to start sing-box concurrently, the second
/// crashes with "Cannot create a file when that file already exists"
/// and the user sees: connections drop, ping spikes to 5000+ on voice,
/// HTTPS sessions get torn down every ~20 seconds.
///
/// This lock is acquired before sing-box.Start() and held until Stop()
/// or process exit. If another instance already owns it, callers should
/// wait or report a friendly error to the user.
///
/// Released automatically by the Windows kernel when the holding process
/// dies (graceful or crash), so an unclean shutdown won't deadlock the
/// next instance.
/// </summary>
public sealed class TunOwnershipLock : IDisposable
{
    // Global\ prefix makes the mutex visible across user sessions and
    // services. Requires admin (we're already running as admin for TUN).
    private const string MutexName = @"Global\VPNRouter-SingBox-Owner";

    private readonly ILogger _logger;
    private Semaphore? _semaphore;
    private bool _owned;
    private bool _disposed;
    private CancellationTokenSource? _ownerRecordMonitorCts;
    private readonly object _ownerRecordMonitorGate = new();

    // Singleton: one lock per process. Prevents orphaned locks when
    // VpnEngine creates a new SingBoxManager for each connection.
    private static TunOwnershipLock? _instance;
    public static TunOwnershipLock Instance(ILogger? logger = null)
    {
        _instance ??= new TunOwnershipLock(logger);
        return _instance;
    }

    public TunOwnershipLock(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    /// <summary>
    /// Try to acquire ownership immediately. Returns true if we got it,
    /// false if another process already owns it.
    /// </summary>
    public bool TryAcquire()
    {
        if (_owned) return true;

        try
        {
            // Named Semaphore with max 1 — works like a mutex but can be
            // released from any thread (unlike Mutex which is thread-affine
            // and throws ApplicationException if released from wrong thread).
            _semaphore = new Semaphore(1, 1, MutexName, out _);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[TunLock] Failed to create semaphore (continuing without lock)");
            return true; // fail-open
        }

        try
        {
            _owned = _semaphore.WaitOne(TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[TunLock] WaitOne failed");
            return true; // fail-open
        }

        if (_owned)
        {
            _logger.Information("[TunLock] Acquired (process owns sing-box)");
        }
        else
            _logger.Information("[TunLock] Held by another VPNRouter instance");

        return _owned;
    }

    public void Release()
    {
        if (!_owned || _semaphore == null) return;
        StopOwnerRecordMonitor();
        try
        {
            _semaphore.Release();
            _logger.Information("[TunLock] Released");
        }
        catch (SemaphoreFullException)
        {
            // Already released — harmless
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[TunLock] Release failed");
        }
        finally
        {
            _owned = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
        _semaphore?.Dispose();
        _semaphore = null;
    }

    /// <summary>
    /// Called only by <see cref="ProcessOwnership.ConfiguredExePath"/>, which
    /// SingBoxManager sets for the executable it is about to launch. The process
    /// that actually owns the TUN semaphore watches for that exact image and
    /// publishes PID + start identity. Merely reading or rewriting config.yaml
    /// never reaches this path and therefore cannot overwrite durable owner A.
    /// </summary>
    internal static void RegisterExecutablePath(string executablePath)
    {
        var instance = _instance;
        if (instance is null || !instance._owned || instance._disposed) return;
        instance.StartOwnerRecordMonitor(executablePath);
    }

    private void StartOwnerRecordMonitor(string executablePath)
    {
        CancellationTokenSource cts;
        lock (_ownerRecordMonitorGate)
        {
            StopOwnerRecordMonitorUnderLock();
            cts = new CancellationTokenSource();
            _ownerRecordMonitorCts = cts;
        }

        var notBeforeUtcTicks = DateTime.UtcNow.Ticks;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested && _owned)
                {
                    try
                    {
                        // A verifier in this owner process uses the same image path.
                        // Do not publish while its explicit scope is active. Cross-
                        // process verifiers are rejected later by the exact v2 child
                        // identity rather than by a process-local flag.
                        if (!DeepVerifyProbe.AnyProbeInFlight)
                        {
                            var child = ProcessOwnership.FindProcessAtPath(
                                executablePath,
                                notBeforeUtcTicks,
                                Environment.ProcessId);
                            if (child is { } identity)
                            {
                                var existing = ProcessOwnership.ReadRuntimeOwnerRecord(
                                    Path.Combine(AppPaths.DataDir, "runtime-owner.json"));
                                var alreadyPublished = existing.Kind == RuntimeOwnerRecordKind.CurrentV2
                                                       && existing.Record is { } record
                                                       && record.OwnerPid == Environment.ProcessId
                                                       && record.ChildPid == identity.Pid
                                                       && record.ChildStartedAtUtcTicks == identity.StartedAtUtcTicks
                                                       && ProcessOwnership.IsSamePath(
                                                           record.ExecutablePath,
                                                           identity.ExecutablePath);
                                if (!alreadyPublished)
                                    ProcessOwnership.WriteRuntimeOwnerRecord(identity);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "[TunLock] Runtime owner record monitor iteration failed");
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(200), cts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                cts.Dispose();
            }
        });
    }

    private void StopOwnerRecordMonitor()
    {
        lock (_ownerRecordMonitorGate)
            StopOwnerRecordMonitorUnderLock();
    }

    private void StopOwnerRecordMonitorUnderLock()
    {
        var cts = _ownerRecordMonitorCts;
        _ownerRecordMonitorCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// v2.26.1 — peek whether ANY process currently owns the TUN semaphore
    /// without disrupting them. Used by the Service's startup flow to
    /// decide "should I try to start sing-box right now?" and by the App
    /// to distinguish "sing-box running & I own it" from "sing-box running
    /// but someone else owns it — adopt its state without racing".
    ///
    /// Unlike the process-name check it catches ALL sing-box owners
    /// (Service, App, CLI, debugger) and it catches them atomically — no
    /// polling race where the process is gone by the time we query it.
    ///
    /// Implementation: create the named Semaphore (count 1), try to
    /// acquire with zero timeout. If we get it → nobody had it → release
    /// immediately so we don't accidentally become the owner. If we
    /// don't get it → someone else holds it → return true.
    ///
    /// Fail-safe: any exception returns false ("assume free"), matching
    /// the fail-open posture of <see cref="TryAcquire"/>.
    /// </summary>
    public static bool IsOwnedByAnyone()
        => ProbeOwnership() == TunOwnershipStatus.Owned;

    /// <summary>
    /// Observe the global semaphore without taking ownership. Unlike the legacy
    /// bool API, failures are returned as <see cref="TunOwnershipStatus.Unavailable"/>
    /// so runtime status can fail open only when observation is genuinely
    /// unavailable, not when the semaphore is positively free.
    /// </summary>
    public static TunOwnershipStatus ProbeOwnership()
    {
        try
        {
            using var probe = new Semaphore(1, 1, MutexName, out _);
            var gotIt = probe.WaitOne(0);
            if (gotIt)
            {
                // Release immediately — we were just peeking, not acquiring.
                try { probe.Release(); } catch { /* already released, fine */ }
                return TunOwnershipStatus.Free;
            }
            return TunOwnershipStatus.Owned;
        }
        catch
        {
            return TunOwnershipStatus.Unavailable;
        }
    }
}

/// <summary>
/// Thrown when sing-box can't start because another VPNRouter instance
/// already owns the TUN adapter. Callers should catch this and either
/// retry later (service) or show a friendly UI message (desktop).
/// </summary>
public class TunOwnershipException : Exception
{
    public TunOwnershipException(string message) : base(message) { }
}
