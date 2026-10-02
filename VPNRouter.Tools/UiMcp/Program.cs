using Avalonia;
using Avalonia.Headless;
using VPNRouter.Core;
using VPNRouterApp = VPNRouter.App.App;

namespace VPNRouter.Tools.UiMcp;

public static class ProbeApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<VPNRouterApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public static class Program
{
    public static int Main(string[] args)
    {
        // The protocol owns stdout. Anything the application prints (its logger writes to the console) goes to stderr.
        var protocolOut = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);

        // The view model writes logs and settings under the app data folder: keep it away from the real one.
        var dataDir = Path.Combine(Path.GetTempPath(), "vpnrouter-ui-mcp-" + Environment.ProcessId);
        Directory.CreateDirectory(dataDir);
        AppPaths.OverrideDataDir(dataDir);

        using var session = HeadlessUnitTestSession.StartNew(typeof(ProbeApp));
        var server = new McpServer(session, protocolOut);
        try
        {
            return server.Run(Console.In);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { }
        }
    }
}
