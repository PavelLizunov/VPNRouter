using Serilog;
using Spectre.Console.Cli;
using VPNRouter.CLI.Commands;

if (VPNRouter.Core.Services.UnixOwnedProcessSignal.TryHandleHelper(args, out var helperExitCode))
    return helperExitCode;

var logDir = Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter\logs");
Directory.CreateDirectory(logDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: Path.Combine(logDir, "vpnrouter.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level:u5}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var registrar = new VPNRouter.CLI.SettingsAwareTypeRegistrar();
registrar.RegisterInstance(typeof(VPNRouter.Core.Services.ISettingsStore),
    VPNRouter.Core.Services.RealSettingsStore.Instance);
var app = new CommandApp(registrar);

app.Configure(config =>
{
    config.SetApplicationName("vpnrouter");
    config.SetApplicationVersion(VPNRouter.Core.AppVersion.Version);
    config.ValidateExamples();

    config.AddCommand<StartCommand>("start")
        .WithDescription("Start VPN routing with a profile")
        .WithExample("start", "--profile", "Gaming_Full")
        .WithExample("start", "--profile", "Discord_Privacy,Work_Suite")
        .WithExample("start", "--profile", "Gaming_Full", "--dry-run");

    config.AddCommand<StopCommand>("stop")
        .WithDescription("Stop VPN routing");

    config.AddCommand<StatusCommand>("status")
        .WithDescription("Show current VPN router status");

    config.AddCommand<DoctorCommand>("doctor")
        .WithDescription("Health check: config, catalogue, binaries, state. Exit 0 = OK, 1 = warnings, 2 = errors");

    config.AddCommand<TestUpdateCommand>("test-update")
        .WithDescription("CI-only: trigger the auto-update flow programmatically (gated by VPNROUTER_CI=1)")
        .IsHidden();

    config.AddBranch("service", svc =>
    {
        svc.SetDescription("Manage VPN Router as a Windows Service");

        svc.AddCommand<ServiceInstallCommand>("install")
            .WithDescription("Install as a Windows Service (auto-start)")
            .WithExample("service", "install")
            .WithExample("service", "install", "--exe", "C:\\path\\to\\VPNRouter.Service.exe");

        svc.AddCommand<ServiceUninstallCommand>("uninstall")
            .WithDescription("Uninstall the Windows Service")
            .WithExample("service", "uninstall");

        svc.AddCommand<ServiceStartCommand>("start")
            .WithDescription("Start the Windows Service")
            .WithExample("service", "start");

        svc.AddCommand<ServiceStopCommand>("stop")
            .WithDescription("Stop the Windows Service")
            .WithExample("service", "stop");

        svc.AddCommand<ServiceStatusCommand>("status")
            .WithDescription("Show Windows Service status")
            .WithExample("service", "status");
    });

    config.AddBranch("profiles", profiles =>
    {
        profiles.SetDescription("Manage VPN profiles");

        profiles.AddCommand<ProfilesListCommand>("list")
            .WithDescription("List all available profiles")
            .WithExample("profiles", "list");

        profiles.AddCommand<ProfilesShowCommand>("show")
            .WithDescription("Show details of a specific profile")
            .WithExample("profiles", "show", "Gaming_Full");

        profiles.AddCommand<ProfilesUpdateCommand>("update")
            .WithDescription("Update profiles from GitHub")
            .WithExample("profiles", "update");
    });
});

try
{
    return await app.RunAsync(args);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled exception");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
