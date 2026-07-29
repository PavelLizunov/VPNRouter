using System;
using System.IO;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

// P05 (DATA-1): pins the atomic-save contract of SettingsLoader.Save.
// Conventions mirror SettingsLoaderRobustnessTests (unique temp dir,
// IDisposable cleanup, SafeModeStateCollection).

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public class SettingsLoaderAtomicSaveTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsLoaderAtomicSaveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "VPNRouter.P05." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string PathFor(string filename) => Path.Combine(_tempDir, filename);

    private static AppSettings NewSettings(string server) => new AppSettings
    {
        Vless = new VlessConfig
        {
            Servers = { new VlessServerEntry { Server = server, Port = 443 } }
        }
    };

    // A failed temp write must leave the previous config bytes intact.
    // Sabotage: create the temp path as a DIRECTORY so FileStream.Create
    // throws identically on Windows (access-denied) and Linux (EISDIR)
    // before the rename ever runs. Deterministic on both CI platforms.
    [Fact]
    public void Save_FailedTempWrite_LeavesPreviousConfigIntact()
    {
        var path = PathFor("interrupted.yaml");
        SettingsLoader.Save(NewSettings("good.example.com"), path);
        var originalBytes = File.ReadAllBytes(path);

        Directory.CreateDirectory(path + ".tmp"); // sabotage — intentionally remains

        Assert.ThrowsAny<Exception>(
            () => SettingsLoader.Save(NewSettings("bad.example.com"), path));

        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        var reloaded = SettingsLoader.Load(path);
        Assert.DoesNotContain(reloaded.Vless.Servers, e => e.Server == "bad.example.com");
    }

    // A successful Save must not leave its temp file behind.
    [Fact]
    public void Save_Success_LeavesNoTempFile()
    {
        var path = PathFor("clean.yaml");

        SettingsLoader.Save(NewSettings("node.example.com"), path);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }
}
