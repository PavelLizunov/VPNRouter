using System;
using System.IO;
using System.Linq;
using VPNRouter.Core.Services;
using Xunit.Abstractions;

namespace VPNRouter.Tests;

/// <summary>
/// v2.32.0 — pin contract for the startup-loop recovery counter.
///
/// <para>The counter persists strikes across launches and escalates
/// through SelfRepair → config-reset → Safe-Mode prompt. If a future
/// refactor weakens any invariant — increment ordering, MarkStable
/// reset, threshold boundaries, per-tier cooldown — chronic startup
/// loops would no longer self-heal. These tests pin the contract.</para>
///
/// <para>Each test uses an isolated temp file path so concurrent runs
/// don't trample each other's state. <see cref="LaunchFailureCounter.ResetCooldown"/>
/// drives the cooldown window to a small value so cooldown-elapsed
/// scenarios don't have to wait real minutes.</para>
/// </summary>
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

    // ─── increment / MarkStable / Read round-trips ───────────────────

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
    public void RecordFailureType_DoesNotChangeCounter()
    {
        var path = NewTempPath();
        try
        {
            LaunchFailureCounter.IncrementOnStartup(path: path);
            LaunchFailureCounter.IncrementOnStartup(path: path);

            LaunchFailureCounter.RecordFailureType("OutOfMemoryException", path);

            var s = LaunchFailureCounter.Read(path);
            Assert.Equal(2, s.ConsecutiveFailures);
            Assert.Equal("OutOfMemoryException", s.LastFailureType);
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
            // Last failure type is informational — keep it for diagnostics.
            Assert.Equal("Crash1", s.LastFailureType);
        }
        finally { CleanUp(path); }
    }

    // ─── threshold / RecommendAction ───────────────────────────────

    [Fact]
    public void RecommendAction_BelowThreshold_ReturnsNone()
    {
        var path = NewTempPath();
        try
        {
            LaunchFailureCounter.ResetCooldown(10);
            LaunchFailureCounter.IncrementOnStartup(path: path); // 1
            Assert.Equal("none", LaunchFailureCounter.RecommendAction(path));
            LaunchFailureCounter.IncrementOnStartup(path: path); // 2
            Assert.Equal("none", LaunchFailureCounter.RecommendAction(path));
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

            // First call at 5 strikes hits self-repair tier first…
            // …but the spec says higher tiers take precedence when
            // their threshold is met. So at 5, config-reset should win.
            Assert.Equal("config-reset", LaunchFailureCounter.RecommendAction(path));
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void RecommendAction_AtThreshold7_ReturnsSafeModePrompt()
    {
        var path = NewTempPath();
        try
        {
            LaunchFailureCounter.ResetCooldown(10);
            for (int i = 0; i < 7; i++)
                LaunchFailureCounter.IncrementOnStartup(path: path);

            Assert.Equal("safe-mode-prompt", LaunchFailureCounter.RecommendAction(path));
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

    // ─── cooldown / loop guard ─────────────────────────────────────

    [Fact]
    public void RecommendAction_WithinCooldown_ReturnsNoneEvenWhenThresholdMet()
    {
        var path = NewTempPath();
        try
        {
            LaunchFailureCounter.ResetCooldown(10);
            for (int i = 0; i < 3; i++)
                LaunchFailureCounter.IncrementOnStartup(path: path);

            // First call stamps the cooldown.
            Assert.Equal("self-repair", LaunchFailureCounter.RecommendAction(path));

            // Bump counter a couple more — still below 5, so no escalation.
            LaunchFailureCounter.IncrementOnStartup(path: path); // 4
            Assert.Equal("none", LaunchFailureCounter.RecommendAction(path));

            // Even repeating doesn't refire self-repair — cooldown blocks.
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
            // Use a 0-minute cooldown so it elapses immediately.
            LaunchFailureCounter.ResetCooldown(0);
            for (int i = 0; i < 3; i++)
                LaunchFailureCounter.IncrementOnStartup(path: path);

            Assert.Equal("self-repair", LaunchFailureCounter.RecommendAction(path));

            // Cooldown=0 means "any time elapsed > 0 is past cooldown"
            // which is technically anything > 0 ticks. The next call
            // happens microseconds later, so window-of-zero still fires.
            // (Counter is still at 3 since RecommendAction doesn't bump
            // it — only IncrementOnStartup does.)
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

            // 3 strikes → self-repair, stamps cooldown.
            for (int i = 0; i < 3; i++)
                LaunchFailureCounter.IncrementOnStartup(path: path);
            Assert.Equal("self-repair", LaunchFailureCounter.RecommendAction(path));

            // Climb to 5 strikes. Self-repair cooldown still active; but
            // config-reset is a different tier with its own (fresh) cooldown.
            LaunchFailureCounter.IncrementOnStartup(path: path); // 4
            LaunchFailureCounter.IncrementOnStartup(path: path); // 5
            Assert.Equal("config-reset", LaunchFailureCounter.RecommendAction(path));

            // Climb to 7. Both lower tiers on cooldown; safe-mode-prompt fresh.
            LaunchFailureCounter.IncrementOnStartup(path: path); // 6
            LaunchFailureCounter.IncrementOnStartup(path: path); // 7
            Assert.Equal("safe-mode-prompt", LaunchFailureCounter.RecommendAction(path));

            // All tiers on cooldown now → "none" until cooldown elapses.
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
            // Write garbage bytes so JSON parse fails.
            File.WriteAllText(path, "{not valid json");

            var s = LaunchFailureCounter.Read(path);
            Assert.Equal(0, s.ConsecutiveFailures);
            Assert.Equal(string.Empty, s.LastFailureType);

            // Subsequent IncrementOnStartup must overwrite cleanly.
            var n = LaunchFailureCounter.IncrementOnStartup(path: path);
            Assert.Equal(1, n);
        }
        finally { CleanUp(path); }
    }

    [Fact]
    public void Threshold_ConstantsMatchSpec()
    {
        // Pin the 3 / 5 / 7 boundary contract so a refactor that
        // accidentally bumps them surfaces here.
        Assert.Equal(3, LaunchFailureCounter.SelfRepairThreshold);
        Assert.Equal(5, LaunchFailureCounter.ConfigResetThreshold);
        Assert.Equal(7, LaunchFailureCounter.SafeModePromptThreshold);
    }

    // ─── source-pin: Program.cs wiring ─────────────────────────────

    /// <summary>
    /// Pin: <c>Program.Main</c> calls <c>RecommendAction</c> AND
    /// <c>IncrementOnStartup</c>, AND <c>RecommendAction</c> appears
    /// before <c>IncrementOnStartup</c>. If a future refactor reorders
    /// or drops either call, this test fires.
    /// </summary>
    [Fact]
    public void ProgramCs_WiresLaunchFailureCounter()
    {
        var sourcePath = FindRepoFile(Path.Combine("VPNRouter.App", "Program.cs"));
        if (sourcePath == null) return; // partial CI checkout — skip

        var src = StripLineComments(File.ReadAllText(sourcePath));

        var recommendIdx = src.IndexOf("LaunchFailureCounter.RecommendAction", StringComparison.Ordinal);
        var incrementIdx = src.IndexOf("LaunchFailureCounter.IncrementOnStartup", StringComparison.Ordinal);

        Assert.True(recommendIdx > 0,
            "Program.Main must call LaunchFailureCounter.RecommendAction — not found in source");
        Assert.True(incrementIdx > 0,
            "Program.Main must call LaunchFailureCounter.IncrementOnStartup — not found in source");
        Assert.True(recommendIdx < incrementIdx,
            "RecommendAction must precede IncrementOnStartup so a triggered recovery " +
            "doesn't double-count the current launch as another strike");

        // Also pin the dispatch helper exists.
        Assert.Contains("DispatchLaunchRecovery", src);
    }

    /// <summary>
    /// Pin: <c>MainWindow</c> code-behind subscribes <c>Opened</c> to
    /// call <c>LaunchFailureCounter.MarkStable</c>. If a refactor moves
    /// the wiring or drops the call, the strikes counter wouldn't
    /// reset on successful launches and would eventually fire false-
    /// positive recoveries on healthy installs.
    /// </summary>
    [Fact]
    public void MainWindowCs_WiresMarkStable()
    {
        var sourcePath = FindRepoFile(Path.Combine("VPNRouter.App", "Views", "MainWindow.axaml.cs"));
        if (sourcePath == null) return; // partial CI checkout — skip

        var src = StripLineComments(File.ReadAllText(sourcePath));

        Assert.Contains("Opened +=", src);
        Assert.Contains("LaunchFailureCounter.MarkStable", src);

        // Order: the Opened subscription should come before the
        // LaunchFailureCounter.MarkStable call (they're on adjacent
        // lines in the lambda, but defensive ordering check).
        var openedIdx = src.IndexOf("Opened +=", StringComparison.Ordinal);
        var markStableIdx = src.IndexOf("LaunchFailureCounter.MarkStable", StringComparison.Ordinal);
        Assert.True(openedIdx < markStableIdx,
            "MarkStable must be called from inside the Opened handler");
    }

    // ─── manual-repro emitter ──────────────────────────────────────

    /// <summary>
    /// Manual-repro evidence: prints a launch-by-launch trace of the
    /// escalation pattern. Mirrors the "deliberately throw in
    /// <c>Program.Main</c> before UI ready" flow without actually
    /// modifying Program.cs — every iteration calls
    /// <c>RecommendAction</c> + <c>IncrementOnStartup</c> in the same
    /// order Main does, and we print what the dispatch would do.
    ///
    /// <para>RecommendAction reads the counter as it stood AT THE
    /// START of this launch (i.e. the strike count from the previous
    /// failed launches). IncrementOnStartup then bumps it for THIS
    /// launch. So at strike #4 the recommendation reads counter=3
    /// (3 prior failures) and returns self-repair; THIS launch's
    /// increment then bumps to 4.</para>
    ///
    /// <para>The counter keeps climbing every launch in this trace,
    /// which models the worst case where the dispatched recovery
    /// action fails to actually take effect (e.g. SelfRepair.Plan
    /// declines to run because of its own marker). In a real Main
    /// flow, a successful self-repair dispatch calls Environment.Exit
    /// before increment runs.</para>
    ///
    /// <para>Run with <c>--logger "console;verbosity=normal"</c> to
    /// see the trace output. The test asserts the expected
    /// recommendation at each strike so it doubles as both repro
    /// and regression pin.</para>
    /// </summary>
    [Fact]
    public void Repro_EightStrikeLoop_PrintsEscalationTrace()
    {
        var path = NewTempPath();
        try
        {
            LaunchFailureCounter.ResetCooldown(10);
            LaunchFailureCounter.Reset(path);

            // Index = launch number (1-based). The "counter snapshot"
            // recorded by RecommendAction equals the strike count
            // BEFORE this launch's own increment runs.
            var expected = new[]
            {
                "none",              // launch #1, prior=0
                "none",              // launch #2, prior=1
                "none",              // launch #3, prior=2
                "self-repair",       // launch #4, prior=3 — tier 1 fires; cooldown stamped
                "none",              // launch #5, prior=4 — self-repair on cooldown, below 5
                "config-reset",      // launch #6, prior=5 — tier 2 fires
                "none",              // launch #7, prior=6
                "safe-mode-prompt",  // launch #8, prior=7 — tier 3 fires
            };

            _output.WriteLine("=== v2.32.0 launch-failure-counter manual repro ===");
            _output.WriteLine($"State file: {path}");
            _output.WriteLine("");

            for (int i = 0; i < expected.Length; i++)
            {
                // Mirror Program.Main: RecommendAction first, then Increment.
                var priorCount = LaunchFailureCounter.Read(path).ConsecutiveFailures;
                var action = LaunchFailureCounter.RecommendAction(path);
                var newCount = LaunchFailureCounter.IncrementOnStartup(path: path);

                _output.WriteLine(
                    $"launch #{i + 1,-2} : prior={priorCount,-2} action='{action,-16}' new-counter={newCount}");

                Assert.Equal(expected[i], action);
            }

            _output.WriteLine("");
            _output.WriteLine("Final state:");
            _output.WriteLine(File.ReadAllText(path));
        }
        finally { CleanUp(path); }
    }

    // ─── source-pin helpers ───────────────────────────────────────

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
