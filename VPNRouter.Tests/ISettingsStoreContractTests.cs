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
        _wasSafeMode = SafeMode.Enabled;
    }

    public void Dispose()
    {
        SafeMode.Enabled = _wasSafeMode;
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string PathFor(string name) => Path.Combine(_tempDir, name);

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
                    var pathSuffix = (i % 5).ToString();
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
            SafeMode.Enabled = false;
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
        Assert.Same(RealSettingsStore.Instance, RealSettingsStore.Instance);
    }
}
