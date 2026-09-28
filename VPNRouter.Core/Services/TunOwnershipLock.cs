using System;
using System.Threading;
using Serilog;

namespace VPNRouter.Core.Services;

public enum TunOwnershipStatus
{
    Free,
    Owned,
    Unavailable
}

public sealed class TunOwnershipLock : IDisposable
{
    private const string MutexName = @"Global\VPNRouter-SingBox-Owner";

    private readonly ILogger _logger;
    private Semaphore? _semaphore;
    private bool _owned;
    private bool _disposed;
    private CancellationTokenSource? _ownerRecordMonitorCts;
    private readonly object _ownerRecordMonitorGate = new();

    private static readonly object InstanceGate = new();
    private static TunOwnershipLock? _instance;
    public static TunOwnershipLock Instance(ILogger? logger = null)
    {
        lock (InstanceGate)
        {
            if (_instance is null || _instance._disposed)
                _instance = new TunOwnershipLock(logger);
            return _instance;
        }
    }

    public TunOwnershipLock(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    internal bool HasOwnership => _owned;

    public bool TryAcquire()
    {
        if (_owned) return true;

        try
        {
            _semaphore ??= new Semaphore(1, 1, MutexName, out _);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[TunLock] Failed to create semaphore (continuing without lock)");
            return true;
        }

        try
        {
            _owned = _semaphore.WaitOne(TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[TunLock] WaitOne failed");
            return true;
        }

        if (_owned)
        {
            _logger.Information("[TunLock] Acquired (process owns sing-box)");
        }
        else
            _logger.Information("[TunLock] Held by another VPNRouter instance");

        return _owned;
    }

    internal bool TryAcquireExclusive()
    {
        lock (InstanceGate)
        {
            if (_owned) return false;
            return TryAcquire();
        }
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
        lock (InstanceGate)
        {
            if (_disposed) return;
            _disposed = true;
            Release();
            _semaphore?.Dispose();
            _semaphore = null;
            if (ReferenceEquals(_instance, this))
                _instance = null;
        }
    }

    internal static void RegisterExecutablePath(string executablePath)
    {
        lock (InstanceGate)
        {
            var instance = _instance;
            if (instance is null || !instance._owned || instance._disposed) return;
            instance.StartOwnerRecordMonitor(executablePath);
        }
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

    public static bool IsOwnedByAnyone()
        => ProbeOwnership() == TunOwnershipStatus.Owned;

    public static TunOwnershipStatus ProbeOwnership()
    {
        try
        {
            using var probe = new Semaphore(1, 1, MutexName, out _);
            var gotIt = probe.WaitOne(0);
            if (gotIt)
            {
                try { probe.Release(); } catch { }
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

public class TunOwnershipException : Exception
{
    public TunOwnershipException(string message) : base(message) { }
}
