using System.Runtime.InteropServices;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using VPNRouter.Headless.Protocol;

namespace VPNRouter.Headless;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        bool stdioMode = false;
        string? dataDir = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--stdio")
            {
                stdioMode = true;
            }
            else if (arg == "--data-dir")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Error: --data-dir requires an absolute path argument.");
                    return 1;
                }

                dataDir = args[++i];
                if (!Path.IsPathRooted(dataDir))
                {
                    Console.Error.WriteLine("Error: --data-dir must be an absolute path.");
                    return 1;
                }
            }
            else
            {
                // Must not echo secret or untrusted input from argv
                Console.Error.WriteLine("Error: Unknown argument.");
                PrintUsage();
                return 1;
            }
        }

        if (!stdioMode)
        {
            PrintUsage();
            return 1;
        }

        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        PosixSignalRegistration? sigtermRegistration = null;
        PosixSignalRegistration? sigintRegistration = null;
        PosixSignalRegistration? sigquitRegistration = null;

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                sigtermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
                {
                    context.Cancel = true;
                    cts.Cancel();
                });
                sigintRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
                {
                    context.Cancel = true;
                    cts.Cancel();
                });
                sigquitRegistration = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, context =>
                {
                    context.Cancel = true;
                    cts.Cancel();
                });
            }
            catch (PlatformNotSupportedException)
            {
                // PosixSignalRegistration not supported on this platform
            }
        }

        using var sigtermScope = sigtermRegistration;
        using var sigintScope = sigintRegistration;
        using var sigquitScope = sigquitRegistration;
        using var policyScope = OperatingSystem.IsLinux()
            ? SingBoxRuntimePolicy.EnterScope(SingBoxRuntimePolicy.Current ?? SingBoxRuntimePolicy.DefaultProduction)
            : null;

        try
        {
            // Override AppPaths before creating session as required by lifecycle and storage contracts
            if (!string.IsNullOrWhiteSpace(dataDir))
            {
                AppPaths.OverrideDataDir(Path.GetFullPath(dataDir));
            }

            // Wire up lifecycle session and backend as defined in protocol v1 contract
            await using var session = new RouterSession();
            await using var backend = new RouterBackend(session, dataDir);
            await using var handler = new RouterBackendHandler(backend);
            await using var server = new ProtocolServer(handler, Console.OpenStandardInput(), Console.OpenStandardOutput());

            return await server.RunAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error in headless service: {ex.GetType().Name}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: VPNRouter.Headless --stdio [--data-dir ABSOLUTE_PATH]");
        Console.Error.WriteLine("Runs the headless VPNRouter protocol service over standard I/O.");
    }
}
