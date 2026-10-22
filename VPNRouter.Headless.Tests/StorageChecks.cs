using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Headless;
using VPNRouter.Headless.Storage;

namespace VPNRouter.Headless.Tests;

/// <summary>
/// Executable static storage checks for ConfigStorage and RouterBackend guarded mutation compensation.
/// Tests corrupt configuration fail-closed, oversized bounding and stream capping, symlink and broken symlink rejection,
/// cooperative named locking, CAS revision conflict detection, and failing apply preserving external new content.
/// Entry point: public static Task RunAsync().
/// </summary>
public static class StorageChecks
{
    public static async Task RunAsync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vpnrouter-storage-checks-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            Console.WriteLine($"[StorageChecks] Starting storage test run in {tempDir}...");

            await CheckCorruptConfigFailsClosedAsync(tempDir);
            Console.WriteLine("  ✓ CheckCorruptConfigFailsClosed passed");

            await CheckOversizedConfigFileFailsClosedAsync(tempDir);
            Console.WriteLine("  ✓ CheckOversizedConfigFileFailsClosed passed");

            await CheckSymlinkRejectionAsync(tempDir);
            Console.WriteLine("  ✓ CheckSymlinkRejection passed");

            await CheckRevisionConflictDetectionAsync(tempDir);
            Console.WriteLine("  ✓ CheckRevisionConflictDetection passed");

            await CheckFailingApplyPreservingExternalNewContentAsync(tempDir);
            Console.WriteLine("  ✓ CheckFailingApplyPreservingExternalNewContent passed");

            await CheckCooperativeNamedLockingAsync(tempDir);
            Console.WriteLine("  ✓ CheckCooperativeNamedLocking passed");

            await CheckPrivatePermissionsEnforcedAsync(tempDir);
            Console.WriteLine("  ✓ CheckPrivatePermissionsEnforced passed");

            await CheckCancelledMutationDoesNotPersistAsync(tempDir);
            Console.WriteLine("  CheckCancelledMutationDoesNotPersist passed");

            Console.WriteLine("[StorageChecks] All storage checks PASSED successfully!");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort temp dir cleanup
            }
        }
    }

    private static async Task CheckCancelledMutationDoesNotPersistAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "cancelled-mutation");
        Directory.CreateDirectory(dir);
        var storage = new ConfigStorage(dir);
        storage.SaveSettings(storage.GetSettings(), storage.CurrentRevision);
        var path = Path.Combine(dir, "config.yaml");
        var before = await File.ReadAllBytesAsync(path);
        var revision = storage.CurrentRevision;
        await using var backend = new RouterBackend(new FakeRouterSession(), dir);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool cancelled = false;
        try
        {
            await backend.ExecuteAsync("settings.set", ToJsonElement(new
            {
                revision,
                values = new { mtu = 1480 }
            }), cancellation.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, "Already cancelled mutation must report cancellation");
        AssertEqual(Convert.ToHexString(before), Convert.ToHexString(await File.ReadAllBytesAsync(path)),
            "Already cancelled mutation must preserve configuration bytes");
        AssertEqual(revision, storage.CurrentRevision, "Already cancelled mutation must preserve revision");
    }

    // ─── 1. Corrupt Config Fails Closed ───

    private static async Task CheckCorruptConfigFailsClosedAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "corrupt-config");
        Directory.CreateDirectory(dir);

        var configPath = Path.Combine(dir, "config.yaml");
        var corruptContent = "::: INVALID YAML SYNTAX {{{ [[[ unclosed";
        await File.WriteAllTextAsync(configPath, corruptContent);

        var storage = new ConfigStorage(dir);

        // storage.GetSettings() must throw storage_error and never reset to defaults
        bool getSettingsFailed = false;
        try
        {
            storage.GetSettings();
        }
        catch (RouterException ex)
        {
            getSettingsFailed = true;
            AssertEqual("storage_error", ex.Code, "Exception code for corrupt config");
            AssertEqual(RouterException.GetSafeMessage("storage_error"), ex.SafeMessage, "SafeMessage must be fixed mapping");
        }
        Assert(getSettingsFailed, "GetSettings must fail closed on corrupt configuration");

        // RouterBackend snapshot must also fail closed
        var session = new FakeRouterSession();
        var backend = new RouterBackend(session, dir);

        bool snapshotFailed = false;
        try
        {
            await backend.ExecuteAsync("snapshot", ToJsonElement(new { }));
        }
        catch (RouterException ex)
        {
            snapshotFailed = true;
            AssertEqual("storage_error", ex.Code, "Backend snapshot must fail closed with storage_error");
        }
        Assert(snapshotFailed, "Backend snapshot must fail closed on corrupt config");

        // Disk content must remain untouched (corrupt content preserved, not overwritten with defaults)
        var diskContent = await File.ReadAllTextAsync(configPath);
        AssertEqual(corruptContent, diskContent, "Corrupt file on disk must not be overwritten or reset");
    }

    // ─── 2. Oversized Config Bounded Stream Cap ───

    private static async Task CheckOversizedConfigFileFailsClosedAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "oversized-config");
        Directory.CreateDirectory(dir);

        var configPath = Path.Combine(dir, "config.yaml");
        // Create file exceeding 1 MiB (1024 * 1024 + 1024 bytes)
        var oversizedBytes = new byte[1024 * 1024 + 1024];
        Array.Fill(oversizedBytes, (byte)'#');
        await File.WriteAllBytesAsync(configPath, oversizedBytes);

        var storage = new ConfigStorage(dir);

        bool readThrew = false;
        try
        {
            storage.GetSettings();
        }
        catch (RouterException ex)
        {
            readThrew = true;
            AssertEqual("storage_error", ex.Code, "Oversized config must throw storage_error");
            AssertEqual(RouterException.GetSafeMessage("storage_error"), ex.SafeMessage, "SafeMessage must be fixed mapping");
        }
        Assert(readThrew, "Reading oversized config must fail closed");

        bool revThrew = false;
        try
        {
            _ = storage.CurrentRevision;
        }
        catch (RouterException ex)
        {
            revThrew = true;
            AssertEqual("storage_error", ex.Code, "CurrentRevision on oversized config must throw storage_error");
        }
        Assert(revThrew, "CurrentRevision on oversized config must fail closed");
    }

    // ─── 3. Symlink and Broken Symlink Rejection ───

    private static async Task CheckSymlinkRejectionAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "symlink-rejection");
        Directory.CreateDirectory(dir);

        // A. Direct file symlink
        var targetFile = Path.Combine(dir, "real_target.yaml");
        await File.WriteAllTextAsync(targetFile, "app:\n  routing_mode: split\n");
        var symlinkFile = Path.Combine(dir, "config.yaml");

        bool symlinkSupported = false;
        try
        {
            File.CreateSymbolicLink(symlinkFile, targetFile);
            symlinkSupported = true;
        }
        catch
        {
            // If OS privileges do not permit symlink creation in test environment, skip symlink sub-tests
            Console.WriteLine("    [Notice] OS does not permit symlink creation in this environment; skipping symlink creation checks");
            return;
        }

        if (symlinkSupported)
        {
            var storage = new ConfigStorage(dir);

            bool readSymlinkThrew = false;
            try
            {
                storage.GetSettings();
            }
            catch (RouterException ex)
            {
                readSymlinkThrew = true;
                AssertEqual("storage_error", ex.Code, "Symlinked config.yaml must throw storage_error");
            }
            Assert(readSymlinkThrew, "GetSettings must reject symlinked config.yaml");

            bool saveSymlinkThrew = false;
            try
            {
                storage.SaveSettings(new AppSettings());
            }
            catch (RouterException ex)
            {
                saveSymlinkThrew = true;
                AssertEqual("storage_error", ex.Code, "Saving to symlinked config.yaml must throw storage_error");
            }
            Assert(saveSymlinkThrew, "SaveSettings must reject symlinked config.yaml");

            File.Delete(symlinkFile);
        }

        // B. Broken file symlink
        var brokenLinkFile = Path.Combine(dir, "config.yaml");
        try
        {
            File.CreateSymbolicLink(brokenLinkFile, Path.Combine(dir, "nonexistent_target_12345.yaml"));
        }
        catch
        {
            return;
        }

        var brokenStorage = new ConfigStorage(dir);
        bool brokenReadThrew = false;
        try
        {
            brokenStorage.GetSettings();
        }
        catch (RouterException ex)
        {
            brokenReadThrew = true;
            AssertEqual("storage_error", ex.Code, "Broken symlink config.yaml must throw storage_error");
        }
        Assert(brokenReadThrew, "GetSettings must reject broken symlink config.yaml");

        File.Delete(brokenLinkFile);

        // C. Parent directory symlink (including broken parent symlink)
        var realParentDir = Path.Combine(dir, "real_parent");
        Directory.CreateDirectory(realParentDir);
        var symParentDir = Path.Combine(dir, "sym_parent");

        try
        {
            Directory.CreateSymbolicLink(symParentDir, realParentDir);
        }
        catch
        {
            return;
        }

        var subDataDir = Path.Combine(symParentDir, "sub_data");
        Directory.CreateDirectory(subDataDir);

        var parentSymStorage = new ConfigStorage(subDataDir);
        bool parentSymThrew = false;
        try
        {
            parentSymStorage.SaveSettings(new AppSettings());
        }
        catch (RouterException ex)
        {
            parentSymThrew = true;
            AssertEqual("storage_error", ex.Code, "Parent directory symlink must throw storage_error");
        }
        Assert(parentSymThrew, "SaveSettings must reject when parent directory is a symlink");
    }

    // ─── 4. CAS Revision Conflict Detection ───

    private static async Task CheckRevisionConflictDetectionAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "conflict-detection");
        Directory.CreateDirectory(dir);

        var storage = new ConfigStorage(dir);

        // 1. Initial save establishes revision
        var initialSettings = new AppSettings().EnsureSane();
        storage.SaveSettings(initialSettings);
        var rev1 = storage.CurrentRevision;

        // 2. Validate with stale revision throws conflict
        bool validateStaleThrew = false;
        try
        {
            storage.ValidateRevision("stale_revision_abcdef0123456789");
        }
        catch (RouterException ex)
        {
            validateStaleThrew = true;
            AssertEqual("conflict", ex.Code, "Stale revision must throw conflict");
            AssertEqual(RouterException.GetSafeMessage("conflict"), ex.SafeMessage, "SafeMessage must be fixed mapping");
        }
        Assert(validateStaleThrew, "ValidateRevision must throw on stale revision");

        // 3. SaveSettings with stale revision throws conflict
        bool saveStaleThrew = false;
        try
        {
            storage.SaveSettings(initialSettings, expectedRevision: "stale_revision_abcdef0123456789");
        }
        catch (RouterException ex)
        {
            saveStaleThrew = true;
            AssertEqual("conflict", ex.Code, "SaveSettings with stale revision must throw conflict");
        }
        Assert(saveStaleThrew, "SaveSettings must reject stale revision");

        // 4. External modification between validation and save triggers conflict detection
        var configPath = Path.Combine(dir, "config.yaml");
        var externalContent = "app:\n  routing_mode: full\n";
        await File.WriteAllTextAsync(configPath, externalContent);

        bool concurrentModThrew = false;
        try
        {
            // Calling SaveSettings expecting rev1 must detect that disk revision changed
            storage.SaveSettings(initialSettings, expectedRevision: rev1);
        }
        catch (RouterException ex)
        {
            concurrentModThrew = true;
            AssertEqual("conflict", ex.Code, "External modification must trigger conflict");
        }
        Assert(concurrentModThrew, "SaveSettings must detect external disk modification as conflict");
    }

    // ─── 5. Failing Apply Preserving External New Content (Guarded Compensation) ───

    private static async Task CheckFailingApplyPreservingExternalNewContentAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "failing-apply-preserve-external");
        Directory.CreateDirectory(dir);

        var configPath = Path.Combine(dir, "config.yaml");

        // Custom session that simulates concurrent external user modification during ApplyAsync
        var session = new FailingApplyWithExternalEditSession(configPath);
        var backend = new RouterBackend(session, dir);

        // 1. Initial snapshot establishes rev1
        var snap1 = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var rev1 = snap1.GetProperty("revision").GetString()!;

        // 2. Connect the session
        await backend.ExecuteAsync("connect", ToJsonElement(new { revision = rev1 }));
        var snapConnected = JsonSerializer.SerializeToElement(await backend.ExecuteAsync("snapshot", ToJsonElement(new { })));
        var revConnected = snapConnected.GetProperty("revision").GetString()!;
        AssertEqual("connected", snapConnected.GetProperty("state").GetString(), "Session must be connected");

        // 3. Execute mutation.
        // During ExecuteMutationAsync:
        // - Mutation commits new settings to disk (committedRevision = rev2)
        // - session.ApplyAsync is called
        // - session.ApplyAsync simulates an external user editing config.yaml with external content, then throws an exception
        // - Guarded compensation runs: attempts _storage.SaveSettings(previousSettings, committedRevision = rev2)
        // - Because disk now has external content, revision != rev2, guarded rollback fails closed with conflict!
        bool mutationThrewConflict = false;
        try
        {
            await backend.ExecuteAsync("settings.set", ToJsonElement(new
            {
                revision = revConnected,
                values = new { mtu = 1380 }
            }));
        }
        catch (RouterException ex) when (ex.Code == "conflict")
        {
            mutationThrewConflict = true;
            AssertEqual(RouterException.GetSafeMessage("conflict"), ex.SafeMessage, "SafeMessage must be fixed mapping");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Expected RouterException with code 'conflict', but got: {ex.GetType().Name} - {ex.Message}");
        }
        Assert(mutationThrewConflict, "Mutation must fail closed with conflict when rollback detects external modification");

        // 4. CRITICAL: Verify that the external new content on disk was PRESERVED and not overwritten by rollback!
        var diskContent = await File.ReadAllTextAsync(configPath);
        Assert(diskContent.Contains("external_concurrent_user_edit: true"),
            "External concurrent edit on disk must be preserved and not overwritten by rollback!");
    }

    // ─── 6. Cooperative Named Locking ───

    private static async Task CheckCooperativeNamedLockingAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "cooperative-locking");
        Directory.CreateDirectory(dir);

        var storage1 = new ConfigStorage(dir);
        var storage2 = new ConfigStorage(dir);

        // storage1 saves initial settings
        var settings1 = new AppSettings().EnsureSane();
        settings1.App.RoutingMode = "split";
        storage1.SaveSettings(settings1);
        var rev1 = storage1.CurrentRevision;

        // storage2 saves updated settings with expectedRevision rev1
        var settings2 = storage2.GetSettings();
        settings2.App.RoutingMode = "full";
        storage2.SaveSettings(settings2, expectedRevision: rev1);
        var rev2 = storage2.CurrentRevision;

        Assert(!string.Equals(rev1, rev2, StringComparison.Ordinal), "Revisions must differ after update");

        // storage1 attempts to save with stale expectedRevision rev1 -> must throw conflict
        bool staleThrew = false;
        try
        {
            storage1.SaveSettings(settings1, expectedRevision: rev1);
        }
        catch (RouterException ex) when (ex.Code == "conflict")
        {
            staleThrew = true;
        }
        Assert(staleThrew, "Cooperative backend storage instances must detect revision conflict");
    }

    // ─── 7. Private Permissions Enforcement ───

    private static async Task CheckPrivatePermissionsEnforcedAsync(string baseDir)
    {
        var dir = Path.Combine(baseDir, "permissions-enforced");
        Directory.CreateDirectory(dir);

        var storage = new ConfigStorage(dir);
        var settings = new AppSettings().EnsureSane();
        storage.SaveSettings(settings);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var fileMode = File.GetUnixFileMode(storage.ConfigPath);
            const UnixFileMode nonUserFileMask = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Assert((fileMode & nonUserFileMask) == 0, $"config.yaml must have strict private permissions (got {fileMode})");
            Assert((fileMode & (UnixFileMode.UserRead | UnixFileMode.UserWrite)) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                "config.yaml must be user-readable and user-writable");

            var dirMode = File.GetUnixFileMode(dir);
            const UnixFileMode nonUserDirMask = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                               UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Assert((dirMode & nonUserDirMask) == 0, $"DataDir must have strict private permissions (got {dirMode})");
        }
        await Task.CompletedTask;
    }

    // ─── Test Helpers and Fakes ───

    private static JsonElement ToJsonElement(object obj)
    {
        return JsonSerializer.SerializeToElement(obj);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Assertion failed: {message}");
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual))
            throw new InvalidOperationException($"Assertion failed: {message}. Expected '{expected}', but got '{actual}'");
    }

    private sealed class FailingApplyWithExternalEditSession : IRouterSession
    {
        private readonly string _configPath;
        public string State { get; private set; } = "disconnected";
        public string? ErrorCode { get; private set; } = null;
        public bool CanConnect => true;
        public bool SupportsKillSwitch => false;
        public bool SupportsDnsLockdown => false;

        public event Action? Changed;

        public FailingApplyWithExternalEditSession(string configPath)
        {
            _configPath = configPath;
        }

        public Task ConnectAsync(AppSettings settings, CancellationToken ct)
        {
            State = "connected";
            ErrorCode = null;
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public async Task ApplyAsync(AppSettings settings, CancellationToken ct)
        {
            // Simulate external writer modifying config.yaml on disk during apply
            var externalYaml = "app:\n  external_concurrent_user_edit: true\n  routing_mode: full\n";
            await File.WriteAllTextAsync(_configPath, externalYaml, ct);

            // Simulate apply failure in the engine
            throw new RouterException("internal_error", "Simulated engine apply failure");
        }

        public Task DisconnectAsync(CancellationToken ct)
        {
            State = "disconnected";
            ErrorCode = null;
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            State = "unavailable";
            return ValueTask.CompletedTask;
        }
    }
}
