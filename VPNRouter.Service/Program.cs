using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using VPNRouter.Service;

// ─── Logging ──────────────────────────────────────────────────────────────────

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
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "[Startup] VPNRouter Service terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
