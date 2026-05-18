// Phase 3 — 3G-1 (v3.0 refactor): contract tests for ISettingsStore.
//
// Pins the expected behaviour of both implementations:
// 1. RealSettingsStore (production) — delegates to SettingsLoader.* static
//    facade. Smoke-tested here against a temp file so the wrapper itself
//    isn't a black-box; broad filesystem edge cases stay in
//    SettingsLoaderRobustnessTests (the existing SR-4 suite).
// 2. InMemorySettingsStore (test double) — full contract + the parallelism
//    pin that's the real reason we built this fake (the rename-to-
//    `.unloadable-{ts}` race documented in VPNRouter.Tests/CLAUDE.md
//    "Headless tests — known issues").
//
// Brief: plans/phase3-3G-service-polish-2026-05-18.md §3G-1.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Contract tests for <see cref="ISettingsStore"/>. Both <see cref="RealSettingsStore"/>
/// and <see cref="InMemorySettingsStore"/> must satisfy these.
///
/// <para>3G-1: joined <see cref="SafeModeStateCollection"/> so the
/// RealSettingsStore branches (which drive SettingsLoader and read the
/// global static SafeMode early-return at Load()) can't race the
/// SafeMode-flipping classes.</para>
/// </summary>
[Collection(SafeModeStateCollection.Name)]
public sealed class ISettingsStoreContractTests : IDisposable
{
    private readonly string _tempDir;
    private readonly bool _wasSafeMode;

    public ISettingsStoreContractTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "VPNRouter.3G1." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        // SafeMode bypasses real loader saves so RealSettingsStore tests
        // don't write to %ProgramData% when the caller omits a path.
        _wasSafeMode = SafeMode.Enabled;
    }

    public void Dispose()
    {
        SafeMode.Enabled = _wasSafeMode;
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string PathFor(string name) => Path.Combine(_tempDir, name);

    // ─── InMemorySettingsStore contract ────────────────────────────────────

    [Fact]
    public void InMemory_Load_OnEmpty_ReturnsSaneDefaults()
    {
        var store = new InMemorySettingsStore();

        var s = store.Load();

        Assert.NotNull(s);
        Assert.NotNull(s.App);
        Assert.NotNull(s.Vless);
        Assert.NotNull(s.Vless.Servers);
        Assert.Equal(AppSettings.CurrentSchemaVersion, s.SchemaVersion);
    }

    [Fact]
    public void InMemory_SaveThenLoad_RoundTrips()
    {
        var store = new InMemorySettingsStore();
        var s = new AppSettings();
        s.App.Theme = "dark";
        s.Vless.Server = "round.trip.example";

        store.Save(s);
        var reloaded = store.Load();

        Assert.Equal("dark", reloaded.App.Theme);
        Assert.Equal("round.trip.example", reloaded.Vless.Server);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public void InMemory_SaveAtCustomPath_DoesNotClobberDefault()
    {
        var store = new InMemorySettingsStore();
        var s1 = new AppSettings { App = new AppConfig { Theme = "default-store" } };
        var s2 = new AppSettings { App = new AppConfig { Theme = "custom-path" } };

        store.Save(s1);
        store.Save(s2, "alt-path");

        Assert.Equal("default-store", store.Load().App.Theme);
        Assert.Equal("custom-path", store.Load("alt-path").App.Theme);
    }

    [Fact]
    public void InMemory_SafeMode_BlocksSave()
    {
        var store = new InMemorySettingsStore();
        var wasEnabled = SafeMode.Enabled;
        try
        {
            SafeMode.Enabled = true;
            store.Save(new AppSettings { App = new AppConfig { Theme = "should-not-persist" } });
            // SaveCount still 0 — SafeMode is a hard guard.
            Assert.Equal(0, store.SaveCount);
        }
        finally { SafeMode.Enabled = wasEnabled; }
    }

    [Fact]
    public void InMemory_ResetToDefaults_ReturnsBackupPathThenLoadsClean()
    {
        var store = new InMemorySettingsStore();
        var dirty = new AppSettings { App = new AppConfig { Theme = "dirty" } };
        store.Save(dirty);

        var backupPath = store.ResetToDefaults();

        Assert.NotNull(backupPath);
        var after = store.Load();
        // Defaults restored — theme cleared to the AppConfig default.
        Assert.NotEqual("dirty", after.App.Theme);
    }

    [Fact]
    public void InMemory_ConsumeRecoveryNotice_ReturnsThenClears()
    {
        var store = new InMemorySettingsStore();
        store.SeedRecoveryNotice("config.yaml was invalid; restored defaults.");

        var first = store.ConsumeRecoveryNotice();
        var second = store.ConsumeRecoveryNotice();

        Assert.Equal("config.yaml was invalid; restored defaults.", first);
        Assert.Null(second);
    }

    [Fact]
    public void InMemory_Watcher_TriggerFiresCallback()
    {
        var store = new InMemorySettingsStore();
        AppSettings? received = null;
        store.StartWatching(onReload: s => received = s);

        var updated = new AppSettings { App = new AppConfig { Theme = "watcher-fired" } };
        store.TriggerWatcher(updated);

        Assert.NotNull(received);
        Assert.Equal("watcher-fired", received!.App.Theme);
    }

    [Fact]
    public void InMemory_Watcher_StopSuppressesCallback()
    {
        var store = new InMemorySettingsStore();
        var calls = 0;
        store.StartWatching(onReload: _ => Interlocked.Increment(ref calls));
        store.StopWatching();

        store.TriggerWatcher(new AppSettings());

        Assert.Equal(0, calls);
    }

    // ─── Parallelism flake pin ─────────────────────────────────────────────
    //
    // SettingsLoaderRobustnessTests stamps backup files with
    // DateTime.Now.ToString("yyyyMMdd-HHmmss") — second-granularity. Parallel
    // xUnit cases that land on the same second race on File.Move(..., overwrite:false).
    // The fix is the InMemorySettingsStore which has no filesystem at all;
    // this test pins that property by running 200 concurrent Save/Load/Reset
    // calls and asserting we never throw.

    [Fact]
    public async Task InMemory_ParallelSaveLoadReset_IsRaceFree()
    {
        var store = new InMemorySettingsStore();
        const int iterations = 200;
        var exceptions = new List<Exception>();
        var lockObj = new object();

        var tasks = Enumerable.Range(0, iterations)
            .Select(i => Task.Run(() =>
            {
                try
                {
                    var pathSuffix = (i % 5).ToString(); // 5 paths, lots of contention.
                    var path = $"flake-pin-{pathSuffix}";
                    var settings = new AppSettings();
                    settings.App.Theme = $"thread-{i}";
                    store.Save(settings, path);
                    var loaded = store.Load(path);
                    Assert.NotNull(loaded);
                    if (i % 7 == 0)
                        store.ResetToDefaults(path);
                }
                catch (Exception ex)
                {
                    lock (lockObj) { exceptions.Add(ex); }
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Empty(exceptions);
    }

    // ─── RealSettingsStore contract (smoke; full coverage in SettingsLoaderRobustnessTests) ──

    [Fact]
    public void Real_Load_OnMissingFile_ReturnsSaneDefaults()
    {
        var path = PathFor("does-not-exist.yaml");

        var s = RealSettingsStore.Instance.Load(path);

        Assert.NotNull(s);
        Assert.NotNull(s.App);
        Assert.Equal(AppSettings.CurrentSchemaVersion, s.SchemaVersion);
    }

    [Fact]
    public void Real_SaveThenLoad_RoundTrips_ViaTempFile()
    {
        var path = PathFor("roundtrip.yaml");
        var wasSafeMode = SafeMode.Enabled;
        try
        {
            SafeMode.Enabled = false; // Save() no-ops in SafeMode.
            var s = new AppSettings();
            s.App.Theme = "dark";
            RealSettingsStore.Instance.Save(s, path);
            var reloaded = RealSettingsStore.Instance.Load(path);
            Assert.Equal("dark", reloaded.App.Theme);
        }
        finally { SafeMode.Enabled = wasSafeMode; }
    }

    [Fact]
    public void Real_Instance_IsSingleton()
    {
        // The wrapper aliases the global static SettingsLoader state, so all
        // accesses must hit the same instance to share LastRecoveryNotice +
        // the file-watcher with the legacy static API.
        Assert.Same(RealSettingsStore.Instance, RealSettingsStore.Instance);
    }
}
