using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using System.Diagnostics;
using VPNRouter.Service;

const string EventSource = "VPNRouter";
const string EventLogName = "Application";

try
{
    if (!EventLog.SourceExists(EventSource))
        EventLog.CreateEventSource(EventSource, EventLogName);
}
catch {  }

void WriteEvent(string msg, EventLogEntryType type = EventLogEntryType.Information)
{
    try { EventLog.WriteEntry(EventSource, msg, type); } catch { }
}

WriteEvent($"VPNRouter Service process started. PID={Environment.ProcessId}, Args=[{string.Join(", ", args)}], Exe={Environment.ProcessPath}");

try
{
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
                Thread.Sleep(2000);
            }
        }
    }
}
catch (Exception ex)
{
    WriteEvent($"Error during orphan sing-box cleanup: {ex.Message}", EventLogEntryType.Warning);
}

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
    Log.Logger = new LoggerConfiguration().CreateLogger();
}

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

bool isWindowsService = args.Contains("--service");

try
{
    Log.Information("[Startup] VPNRouter Service starting (mode: {Mode})",
        isWindowsService ? "WindowsService" : "Console");

    var builder = Host.CreateApplicationBuilder(args);

    builder.Environment.ApplicationName = "VPNRouter";

    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(Log.Logger);

    builder.Services.AddHostedService<VPNRouterService>();

    if (isWindowsService)
    {
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = ServiceInstaller.ServiceName;
        });
    }

    var host = builder.Build();

    if (!isWindowsService)
    {
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
