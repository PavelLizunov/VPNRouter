using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using System.Diagnostics;
using VPNRouter.Service;

// ─── Early diagnostics to Windows Event Log ──────────────────────────────────
// Event Log works even when Serilog/file logging fails (permissions, disk, etc.)

const string EventSource = "VPNRouter";
const string EventLogName = "Application";

try
{
    if (!EventLog.SourceExists(EventSource))
        EventLog.CreateEventSource(EventSource, EventLogName);
}
catch { /* first run without admin — Event Log source may not exist yet */ }

void WriteEvent(string msg, EventLogEntryType type = EventLogEntryType.Information)
{
    try { EventLog.WriteEntry(EventSource, msg, type); } catch { }
}

WriteEvent($"VPNRouter Service process started. PID={Environment.ProcessId}, Args=[{string.Join(", ", args)}], Exe={Environment.ProcessPath}");

// ─── Kill zombie sing-box processes from previous runs ───────────────────────
// If the service was stopped uncleanly (race condition, power loss, etc.),
// a sing-box process may still be running and holding the TUN interface.
// v2.26.3 fix for Bug A: before killing, reserve TunLock. If another VPNRouter
// instance (App / CLI) legitimately holds the TUN adapter, its sing-box is
// NOT an orphan — it's in active use. Killing it caused the v2.26.1-r1 bug
// where ticking "Enable background service" in Advanced while VPN was up
// instantly dropped the connection. If TUN is held, leave sing-box alone
// and let the owner coordinate (Service will park in watcher mode inside
// VPNRouterService.cs once the owner stops).

try
{
    // Reserve TUN ownership for the full sweep so another VPNRouter instance
    // cannot start a new sing-box between a lock probe and our Kill call.
    using var cleanupLock = new VPNRouter.Core.Services.TunOwnershipLock();
    _ = cleanupLock.TryAcquire();
    if (!cleanupLock.HasOwnership)
    {
        WriteEvent("TUN cleanup reservation is held or unavailable — skipping orphan sing-box cleanup");
    }
    else
    {
        var zombies = Process.GetProcessesByName("sing-box");
        if (zombies.Length > 0)
        {
            WriteEvent($"Found {zombies.Length} sing-box candidate(s), verifying VPNRouter ownership before cleanup");
            var killedOwnedProcess = false;
            foreach (var z in zombies)
            {
                var pid = 0;
                try
                {
                    pid = z.Id;
                    // Retaining the native handle prevents Windows from
                    // recycling this PID between ownership proof and Kill.
                    var pinnedHandle = z.SafeHandle;
                    if (pinnedHandle.IsInvalid || pinnedHandle.IsClosed)
                    {
                        WriteEvent($"Could not pin sing-box PID {pid}; preserving it", EventLogEntryType.Warning);
                        continue;
                    }

                    if (!VPNRouter.Core.Services.ProcessOwnership.IsOwnedSingBox(z))
                    {
                        WriteEvent($"sing-box PID {pid} is not proven VPNRouter-owned; preserving it", EventLogEntryType.Warning);
                        continue;
                    }

                    z.Kill(entireProcessTree: true);
                    var exited = z.WaitForExit(3000);
                    GC.KeepAlive(pinnedHandle);
                    if (!exited)
                    {
                        WriteEvent($"Timed out waiting for owned sing-box PID {pid} to stop", EventLogEntryType.Warning);
                        continue;
                    }

                    killedOwnedProcess = true;
                    WriteEvent($"Killed VPNRouter-owned orphan sing-box PID {pid}");
                }
                catch (InvalidOperationException)
                {
                    WriteEvent($"sing-box PID {pid} already exited before orphan cleanup");
                }
                catch (Exception ex)
                {
                    WriteEvent($"Failed to inspect or stop sing-box PID {pid}: {ex.Message}", EventLogEntryType.Warning);
                }
                finally { z.Dispose(); }
            }

            if (killedOwnedProcess)
            {
                // Give OS time to release TUN after confirmed termination.
                Thread.Sleep(2000);
            }
        }
    }
}
catch (Exception ex)
{
    WriteEvent($"Error during orphan sing-box cleanup: {ex.Message}", EventLogEntryType.Warning);
}

// ─── Logging ──────────────────────────────────────────────────────────────────

try
{
    var logDir = Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter\logs");
    Directory.CreateDirectory(logDir);

    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .WriteTo.File(
            path: Path.Combine(logDir, "vpnrouter.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u5}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();
}
catch (Exception ex)
{
    WriteEvent($"Failed to initialize file logging: {ex.Message}", EventLogEntryType.Error);
    // Continue with a no-op logger — service can still run
    Log.Logger = new LoggerConfiguration().CreateLogger();
}

// ─── Firewall orphan sweep (v2.40.0-r10 #4 + #2 core-audit) ─────────────────────
// The GUI front-end has always swept leftover kill-switch block rules on
// startup; the Service did not, so a crashed Service run could strand the
// user's internet behind block rules until the GUI happened to run. Sweep
// here too — but ONLY when no other VPNRouter instance owns the TUN, mirroring
// the orphan-sing-box guard above: if someone else owns the TUN, the block
// rules are theirs and are NOT orphans. Also wire a process-exit sweep (same
// gate) so an abnormal teardown doesn't leave rules behind.
try
{
    if (!VPNRouter.Core.Services.TunOwnershipLock.IsOwnedByAnyone())
        VPNRouter.Core.Services.FirewallManager.TryCleanupOrphanedRulesSafe(Log.Logger);

    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        try
        {
            if (!VPNRouter.Core.Services.TunOwnershipLock.IsOwnedByAnyone())
                VPNRouter.Core.Services.FirewallManager.TryCleanupOrphanedRulesSafe(Log.Logger);
        }
        catch { }
    };
}
catch (Exception ex)
{
    WriteEvent($"Error during firewall orphan sweep: {ex.Message}", EventLogEntryType.Warning);
}

// ─── Mode detection ───────────────────────────────────────────────────────────
// --service flag is passed by sc.exe binPath when running as Windows Service

bool isWindowsService = args.Contains("--service");

try
{
    Log.Information("[Startup] VPNRouter Service starting (mode: {Mode})",
        isWindowsService ? "WindowsService" : "Console");

    var builder = Host.CreateApplicationBuilder(args);

    // Remove --service from args so the hosted service doesn't see it
    builder.Environment.ApplicationName = "VPNRouter";

    // Serilog as the logging provider
    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(Log.Logger);

    // Register our service
    builder.Services.AddHostedService<VPNRouterService>();

    if (isWindowsService)
    {
        // Run as Windows Service — no console interaction
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = ServiceInstaller.ServiceName;
        });
    }

    var host = builder.Build();

    if (!isWindowsService)
    {
        // Console mode: show startup info and handle Ctrl+C gracefully
        Console.WriteLine($"VPN Router Service — Console Mode");
        Console.WriteLine($"Press Ctrl+C to stop.\n");
    }

    await host.RunAsync();

    Log.Information("[Startup] VPNRouter Service exited cleanly");
    WriteEvent("VPNRouter Service exited cleanly");
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "[Startup] VPNRouter Service terminated unexpectedly");
    WriteEvent($"FATAL: {ex}", EventLogEntryType.Error);
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
