#nullable enable

using System.Runtime.CompilerServices;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

internal static class TestEnvironmentSafety
{
    private static string? _testDataDir;

    [ModuleInitializer]
    internal static void IsolateTunCleanupFromHostDevices()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var testDataDir = Path.GetFullPath(Path.Combine(
            tempRoot,
            $"vpnrouter-testhost-{Environment.ProcessId}-{Guid.NewGuid():N}"));
        if (!testDataDir.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The test data directory escaped the OS temp root.");

        Directory.CreateDirectory(testDataDir);
        _testDataDir = testDataDir;
        AppContext.SetSwitch("VPNRouter.Tests.DisableBackgroundServices", true);
        VPNRouter.Core.AppPaths.OverrideDataDir(testDataDir);
        VPNRouter.Core.AppPaths.EnsureDirectories();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteTestDataDirectory();

        // Unit tests must never inspect or mutate a developer/CI machine's
        // physical VPNRouter-TUN device. Tests that exercise cleanup replace
        // these seams with their own deterministic fakes and restore this
        // process-wide safe baseline afterwards.
        TunAdapterDiagnostics.Runner = new FakeProcessRunner().OnRun(
            _ => true,
            new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero, false));
        TunAdapterDiagnostics.RemovalDelayAsync = static (_, _) => Task.CompletedTask;
        TunAdapterDiagnostics.RequiresNativePnpApi = static () => false;
        TunAdapterDiagnostics.RemoveNativePnpDevice =
            _ => new NativePnpRemovalResult(false, false, 5);
        TunAdapterDiagnostics.QueryNativePnpPresence =
            _ => new NativePnpPresenceResult(NativePnpPresence.Absent, 0x0D);
        TunAdapterDiagnostics.ResolveNativePnpDeviceIds =
            _ => new NativePnpLookupResult(true, Array.Empty<string>(), null);
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(false);
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
    }

    private static void DeleteTestDataDirectory()
    {
        var path = _testDataDir;
        if (string.IsNullOrWhiteSpace(path)) return;
        try { Directory.Delete(path, recursive: true); } catch { /* process-exit best effort */ }
    }
}
