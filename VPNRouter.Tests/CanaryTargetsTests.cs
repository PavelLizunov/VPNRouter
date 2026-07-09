#nullable enable

using System.Text.Json;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins <see cref="CanaryTargets"/> (urltest R4): sane lightweight built-ins,
/// user-override file replaces them, corrupt override falls back gracefully.
/// </summary>
public class CanaryTargetsTests : IDisposable
{
    private readonly string _prevDataDir;
    private readonly string _tempDir;

    public CanaryTargetsTests()
    {
        _prevDataDir = AppPaths.DataDir;
        _tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-ct-{Guid.NewGuid():N}");
        AppPaths.OverrideDataDir(_tempDir);
    }

    public void Dispose()
    {
        AppPaths.OverrideDataDir(_prevDataDir);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void BuiltIn_AreLightweightPopularBlockedTargets()
    {
        var list = CanaryTargets.BuiltIn;
        Assert.True(list.Count >= 2);
        Assert.All(list, t =>
        {
            Assert.Equal(CanaryTier.PopularBlocked, t.Tier);
            Assert.StartsWith("https://", t.Url);
        });
        Assert.Contains(list, t => t.Url.Contains("generate_204"));   // zero-payload endpoint
    }

    [Fact]
    public void Load_NoOverride_ReturnsBuiltIn()
        => Assert.Equal(CanaryTargets.BuiltIn.Count, CanaryTargets.Load().Count);

    [Fact]
    public void Load_UserOverride_ReplacesDefaults()
    {
        Directory.CreateDirectory(AppPaths.CacheDir);
        var custom = new List<CanaryTarget>
        {
            new("https://example.org/check", CanaryTier.LessPopularBlocked, "misc",
                new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)),
        };
        File.WriteAllText(Path.Combine(AppPaths.CacheDir, "canary_targets.json"),
            JsonSerializer.Serialize(custom));

        var loaded = CanaryTargets.Load();
        Assert.Single(loaded);
        Assert.Equal("https://example.org/check", loaded[0].Url);
    }

    [Fact]
    public void Load_CorruptOverride_FallsBackToBuiltIn()
    {
        Directory.CreateDirectory(AppPaths.CacheDir);
        File.WriteAllText(Path.Combine(AppPaths.CacheDir, "canary_targets.json"), "{ nope");
        Assert.Equal(CanaryTargets.BuiltIn.Count, CanaryTargets.Load().Count);
    }
}
