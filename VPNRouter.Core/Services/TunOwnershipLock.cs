using System;
using System.Threading;
using Serilog;

namespace VPNRouter.Core.Services;

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
            _logger.Information("[TunLock] Acquired (process owns sing-box)");
        else
            _logger.Information("[TunLock] Held by another VPNRouter instance");

        return _owned;
    }

    public void Release()
    {
        if (!_owned || _semaphore == null) return;
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
