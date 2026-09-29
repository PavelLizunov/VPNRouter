using System;
using System.IO;
using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class LaunchFailureCounterTests
{
    private readonly ITestOutputHelper _output;

    public LaunchFailureCounterTests(ITestOutputHelper output) => _output = output;

    private static string NewTempPath() =>
        Path.Combine(Path.GetTempPath(),
            $"vpnrouter-launch-counter-tests-{Guid.NewGuid():N}.json");

    private static void CleanUp(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); } catch { }
    }

    [Fact]
    public void IncrementOnStartup_StartsAtOneOnFreshFile()
    {
        var path = NewTempPath();
        try
        {
            var n = LaunchFailureCounter.IncrementOnStartup(path: path);
            Assert.Equal(1, n);

            var s = LaunchFailureCounter.Read(path);
            Assert.Equal(1, s.ConsecutiveFailures);
            Assert.False(string.IsNullOrEmpty(s.LastFailureUtc),
                "LastFailureUtc must be stamped on increment");
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void IncrementOnStartup_AccumulatesAcrossCalls()
    {
        var path = NewTempPath();
        try
        {
            Assert.Equal(1, LaunchFailureCounter.IncrementOnStartup(path: path));
            Assert.Equal(2, LaunchFailureCounter.IncrementOnStartup(path: path));
            Assert.Equal(3, LaunchFailureCounter.IncrementOnStartup(path: path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void IncrementOnStartup_PersistsFailureType()
    {
        var path = NewTempPath();
        try
        {
            LaunchFailureCounter.IncrementOnStartup("InvalidDataException", path);
            var s = LaunchFailureCounter.Read(path);
            Assert.Equal("InvalidDataException", s.LastFailureType);
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void MarkStable_ZerosCounterAndStampsSuccess()
    {
        var path = NewTempPath();
        try
        {
            LaunchFailureCounter.IncrementOnStartup(path: path);
            LaunchFailureCounter.IncrementOnStartup(path: path);
            LaunchFailureCounter.IncrementOnStartup(path: path);

            LaunchFailureCounter.MarkStable(path);

            var s = LaunchFailureCounter.Read(path);
            Assert.Equal(0, s.ConsecutiveFailures);
            Assert.False(string.IsNullOrEmpty(s.LastSuccessUtc),
                "LastSuccessUtc must be stamped on MarkStable");
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void MarkStable_PreservesLastFailureType()
    {
        var path = NewTempPath();
        try
        {
            LaunchFailureCounter.IncrementOnStartup("Crash1", path);
            LaunchFailureCounter.MarkStable(path);

            var s = LaunchFailureCounter.Read(path);
            Assert.Equal(0, s.ConsecutiveFailures);
            Assert.Equal("Crash1", s.LastFailureType);
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void RecommendAction_AtThreshold3_ReturnsSelfRepair()
    {
        var path = NewTempPath();
        try
        {
            LaunchFailureCounter.ResetCooldown(10);
            for (int i = 0; i < 3; i++)
                LaunchFailureCounter.IncrementOnStartup(path: path);

            Assert.Equal("self-repair", LaunchFailureCounter.RecommendAction(path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void RecommendAction_AtThreshold5_PrefersConfigResetOverSelfRepairCooldown()
    {
        var path = NewTempPath();
        try
        {
            LaunchFailureCounter.ResetCooldown(10);
            for (int i = 0; i < 5; i++)
                LaunchFailureCounter.IncrementOnStartup(path: path);

            Assert.Equal("config-reset", LaunchFailureCounter.RecommendAction(path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void RecommendAction_StampsCooldownOnNonNoneAction()
    {
        var path = NewTempPath();
        try
        {
            LaunchFailureCounter.ResetCooldown(10);
            for (int i = 0; i < 3; i++)
                LaunchFailureCounter.IncrementOnStartup(path: path);

            var beforeStamp = LaunchFailureCounter.Read(path).LastSelfRepairUtc;
            Assert.True(string.IsNullOrEmpty(beforeStamp),
                "no LastSelfRepairUtc before any RecommendAction");

            LaunchFailureCounter.RecommendAction(path);

            var afterStamp = LaunchFailureCounter.Read(path).LastSelfRepairUtc;
            Assert.False(string.IsNullOrEmpty(afterStamp),
                "LastSelfRepairUtc must be stamped after RecommendAction returns self-repair");
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void RecommendAction_WithinCooldown_ReturnsNoneEvenWhenThresholdMet()
    {
        var path = NewTempPath();
        try
        {
            LaunchFailureCounter.ResetCooldown(10);
            for (int i = 0; i < 3; i++)
                LaunchFailureCounter.IncrementOnStartup(path: path);

            Assert.Equal("self-repair", LaunchFailureCounter.RecommendAction(path));

            LaunchFailureCounter.IncrementOnStartup(path: path);
            Assert.Equal("none", LaunchFailureCounter.RecommendAction(path));

            for (int i = 0; i < 3; i++)
                Assert.Equal("none", LaunchFailureCounter.RecommendAction(path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void RecommendAction_AfterCooldownElapses_RefiresSelfRepair()
    {
        var path = NewTempPath();
        try
        {
            LaunchFailureCounter.ResetCooldown(0);
            for (int i = 0; i < 3; i++)
                LaunchFailureCounter.IncrementOnStartup(path: path);

            Assert.Equal("self-repair", LaunchFailureCounter.RecommendAction(path));

            Assert.Equal("self-repair", LaunchFailureCounter.RecommendAction(path));
        }
        finally
        {
            LaunchFailureCounter.ResetCooldown(10);
            CleanUp(path);
        }
    }

    [Fact]
    public void RecommendAction_EscalatesAcrossTiersWhenLowerCooldownActive()
    {
        var path = NewTempPath();
        try
        {
            LaunchFailureCounter.ResetCooldown(10);

            for (int i = 0; i < 3; i++)
                LaunchFailureCounter.IncrementOnStartup(path: path);
            Assert.Equal("self-repair", LaunchFailureCounter.RecommendAction(path));

            LaunchFailureCounter.IncrementOnStartup(path: path);
            LaunchFailureCounter.IncrementOnStartup(path: path);
            Assert.Equal("config-reset", LaunchFailureCounter.RecommendAction(path));

            LaunchFailureCounter.IncrementOnStartup(path: path);
            LaunchFailureCounter.IncrementOnStartup(path: path);
            Assert.Equal("safe-mode-prompt", LaunchFailureCounter.RecommendAction(path));

            Assert.Equal("none", LaunchFailureCounter.RecommendAction(path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void Reset_DeletesStateFile()
    {
        var path = NewTempPath();
        try
        {
            LaunchFailureCounter.IncrementOnStartup(path: path);
            Assert.True(File.Exists(path));

            LaunchFailureCounter.Reset(path);
            Assert.False(File.Exists(path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void TryLoad_OnCorruptedFile_ReturnsFreshState()
    {
        var path = NewTempPath();
        try
        {
            File.WriteAllText(path, "{not valid json");

            var s = LaunchFailureCounter.Read(path);
            Assert.Equal(0, s.ConsecutiveFailures);
            Assert.Equal(string.Empty, s.LastFailureType);

            var n = LaunchFailureCounter.IncrementOnStartup(path: path);
            Assert.Equal(1, n);
        }
        finally { CleanUp(path); }
    }

    private static string StripLineComments(string src)
    {
        return string.Join("\n",
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));
    }

    private static string? FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
