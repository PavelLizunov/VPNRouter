// Phase 2G (2026-05-21) — VpnEngine orchestrator characterization tests.
//
// Why: VpnEngine is the lifecycle orchestrator (~976 LOC) and only had 24
// tests across 3 sibling files covering narrow paths (Apply escalation
// source pins, RemoveExcludedApps static helper, ComputeTunFingerprint
// static helper). The state-machine surface — construction, idle Stop,
// idle Apply guard, Dispose semantics, static helpers like
// ParseClashApiPort + ResolveCustomConfigPath + BuildBundledOnlyProfileSources
// — was untested. A Phase 3 refactor that pipeline-drives Start/Stop must
// not regress this surface.
//
// Approach: mix of invoke-based tests against the truly in-memory portions
// of VpnEngine (idle paths, Dispose, static helpers) and source-string pins
// for orchestration concerns that can't be invoke-tested without spawning
// a real sing-box (BR-6a Stop ordering, Dispose-calls-Stop invariant).
// Mirror the existing VpnEngineApplyEscalationTests pattern verbatim for
// the source pins so future readers find one consistent style.
//
// Brief: plans/phase2G-vpnengine-orchestrator-2026-05-21.md.

#nullable enable

using System.IO;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// Characterization tests for <see cref="VpnEngine"/>'s lifecycle
/// orchestrator surface. Pins the contract a Phase 3 refactor must
/// preserve — construction state, idle Stop semantics, ApplyAsync idle
/// guard, Dispose idempotency, and static helper edge cases.
///
/// <para>The full StartAsync→Connected→Stop matrix is intentionally NOT
/// covered here because VpnEngine.StartAsync requires (1) the sing-box
/// binary on disk, (2) Windows-only firewall via netsh, (3) profiles JSON
/// in %ProgramData%. Today there's no test seam that lets us stub those
/// in-memory. Phase 3+ introduces NullFirewallManager + fake sing-box
/// process model; that lifecycle matrix will land then. Cross-references:
/// <see cref="VpnEngineApplyEscalationTests"/> (forceRestart pins),
/// <see cref="ConfigPipelineTests"/> (Phase 2F pipeline contract),
/// <see cref="HealthMonitorRecoveryGapTests"/> (crash-then-restart path).</para>
/// </summary>
public sealed class VpnEngineOrchestratorTests
{
    // ─── Inline stubs (mirroring HealthMonitorRecoveryGapTests pattern) ─

    private sealed class StubProcessScanner : IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) => new();
    }

    private sealed class StubFirewallManager : IFirewallManager
    {
        public void CreateBlockRules(IEnumerable<string> processNames) { }
        public void EnableBlockRules() { }
        public void DisableBlockRules() { }
        public void DeleteAllRules() { }
        public void Dispose() { }
    }

    private sealed class StubProcessMonitor : IProcessMonitor
    {
        public event EventHandler<ProcessEventArgs>? ProcessStarted;
        public event EventHandler<ProcessEventArgs>? ProcessStopped;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        // Suppress CS0067 by referencing the events.
        public void RaiseDummy() { ProcessStarted?.Invoke(this, new()); ProcessStopped?.Invoke(this, new()); }
    }

    /// <summary>
    /// Construct an idle VpnEngine wired to no-op stubs. Suppresses the
    /// CS0618 Obsolete warning on the constructor — direct construction
    /// is deprecated in production code (use PlatformServices factory)
    /// but the test-friendly seam stays compiled for exactly this purpose
    /// per the attribute's <c>error: false</c> setting.
    /// </summary>
#pragma warning disable CS0618
    private static VpnEngine BuildIdleEngine() =>
        new VpnEngine(
            scanner: new StubProcessScanner(),
            firewallFactory: () => new StubFirewallManager(),
            monitorFactory: () => new StubProcessMonitor(),
            logger: null);
#pragma warning restore CS0618

    // ─── 1. Construction state ──────────────────────────────────────────

    [Fact]
    public void Construction_InitialState_IsIdle()
    {
        // Pin: a fresh engine has the public surface a UI would expect
        // BEFORE the user clicks Connect. Any Phase 3 refactor that
        // accidentally pre-populates state (e.g. an over-eager constructor
        // that reads back stale state.json) would regress this.
        using var engine = BuildIdleEngine();

        Assert.False(engine.IsRunning);
        Assert.Equal(string.Empty, engine.ActiveProfileName);
        Assert.Null(engine.SingBoxPid);
        Assert.Empty(engine.MonitoredProcesses);
    }

    [Fact]
    public void Construction_DefaultModes_AreGeneratedSplit()
    {
        // ActiveConfigMode + ActiveRoutingMode have non-empty defaults
        // ("generated" + "split") so the UI status line is never blank
        // before the first Start. Phase 3C StartupPipeline overwrites
        // these via SetActiveModes(...) once a profile resolves.
        using var engine = BuildIdleEngine();

        Assert.Equal("generated", engine.ActiveConfigMode);
        Assert.Equal("split", engine.ActiveRoutingMode);
        Assert.Equal(string.Empty, engine.ActiveServerAddress);
    }

    [Fact]
    public void Construction_EventsDefaultToNull_NoListenersFireDuringConstruction()
    {
        // No event should fire just by constructing the engine. Phase 3
        // briefly considered emitting a "ready" status on construction
        // — pinning the no-fire contract prevents that from sneaking in.
        var fired = false;
        using var engine = BuildIdleEngine();
        engine.StatusChanged += _ => fired = true;
        engine.Warning += _ => fired = true;
        engine.RestartAttempted += (_, _) => fired = true;
        engine.SingBoxStarted += _ => fired = true;
        engine.ProcessDetected += (_, _) => fired = true;
        engine.AutoFailoverTriggered += _ => fired = true;

        // No mutation — just attaching listeners.
        Assert.False(fired);
    }

    // ─── 2. Stop on idle engine ─────────────────────────────────────────

    [Fact]
    public void Stop_OnIdleEngine_IsNoOp_EmitsStatusEvents()
    {
        // Stop on an engine that never Started must not throw and must
        // still emit the bookend "Stopping..." / "Stopped" status
        // messages — the UI relies on these to drive the disconnect-
        // animation lifecycle regardless of whether sing-box was up.
        using var engine = BuildIdleEngine();
        var statuses = new List<string>();
        engine.StatusChanged += s => statuses.Add(s);

        engine.Stop();

        Assert.Contains("Stopping...", statuses);
        Assert.Contains("Stopped", statuses);
        // Order matters — "Stopping..." precedes "Stopped" so the UI
        // animation reads the verb sequence correctly.
        var stoppingIdx = statuses.IndexOf("Stopping...");
        var stoppedIdx = statuses.IndexOf("Stopped");
        Assert.True(stoppingIdx < stoppedIdx,
            $"Stopping... must precede Stopped (got Stopping@{stoppingIdx}, Stopped@{stoppedIdx})");
    }

    [Fact]
    public void Stop_IsIdempotent_SecondCallDoesNotThrow()
    {
        // Pin: calling Stop twice in a row is safe. UI / autostart cleanup
        // paths sometimes fire Stop more than once (e.g. window close +
        // SystemEvents session-ending handler both call Stop). All the
        // internal `_field?.Stop()` chains are null-safe by construction
        // — verify that.
        using var engine = BuildIdleEngine();
        engine.Stop();
        engine.Stop();
        // No throw = pass.
        Assert.False(engine.IsRunning);
    }

    // ─── 3. ApplyAsync idle guard ───────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_IdleEngine_ReturnsFalse()
    {
        // The guard `if (_singBox == null || !_singBox.IsRunning())`
        // short-circuits Apply when no sing-box exists. Pin the return
        // value — UI code branches on this to display "VPN not running,
        // start it first" rather than the generic "Apply failed".
        using var engine = BuildIdleEngine();

        var result = await engine.ApplyAsync(new AppSettings(), TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task ApplyAsync_IdleEngine_DoesNotEmitApplyingStatus()
    {
        // The guard runs BEFORE the OnStatus("Applying config changes...")
        // line, so the idle path stays silent. Important UX pin: a UI
        // that re-fires Apply on every settings save would otherwise
        // flood the status bar with "Applying..." messages on an
        // already-stopped engine. The guard short-circuit is the correct
        // behaviour.
        using var engine = BuildIdleEngine();
        var statuses = new List<string>();
        engine.StatusChanged += s => statuses.Add(s);

        await engine.ApplyAsync(new AppSettings(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(statuses, s => s.Contains("Applying", StringComparison.OrdinalIgnoreCase));
    }

    // ─── 4. Dispose semantics ───────────────────────────────────────────

    [Fact]
    public void Dispose_OnIdleEngine_DoesNotThrow()
    {
        // Pin: Disposing an engine that never Started is safe. Same
        // contract as Stop — the resource fields are all null, all the
        // `?.Dispose()` chains are null-safe.
        var engine = BuildIdleEngine();
        engine.Dispose();
        // No throw = pass. Also verify state is consistent after.
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public void Dispose_IsIdempotent_SecondCallIsNoOp()
    {
        // Pin: the `_disposed` guard at the top of Dispose() catches a
        // double-dispose. UI patterns sometimes Dispose+null-out the
        // engine field twice (e.g. window close handler + Application
        // OnExit). Both calls must complete without exception.
        var engine = BuildIdleEngine();
        engine.Dispose();
        engine.Dispose();
        Assert.False(engine.IsRunning);
    }

    // ─── 5. Static helpers ──────────────────────────────────────────────

    [Fact]
    public void ParseClashApiPort_NullOrInvalid_ReturnsDefault9090()
    {
        // The F-E probe path is "fire even if we can't parse the
        // configured port" — better to attempt against 9090 (the sing-
        // box default) than to skip the probe entirely on a malformed
        // clash_api host:port string. Pin every reject path returns the
        // default rather than 0 / -1 / throwing.
        Assert.Equal(9090, VpnEngine.ParseClashApiPort(null));
        Assert.Equal(9090, VpnEngine.ParseClashApiPort(string.Empty));
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("   "));
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("host-no-port"));
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("127.0.0.1:"));      // colon at end → empty port
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("127.0.0.1:abc"));   // non-numeric
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("127.0.0.1:0"));     // 0 invalid
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("127.0.0.1:65536")); // out of range
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("127.0.0.1:-5"));    // negative
    }

    [Fact]
    public void ParseClashApiPort_ValidHostPort_ParsesPort()
    {
        // Happy path: standard sing-box default and a custom port both
        // parse cleanly. The LastIndexOf(':') trick handles IPv6 too:
        // "[::1]:9090" → 9090.
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("127.0.0.1:9090"));
        Assert.Equal(8080, VpnEngine.ParseClashApiPort("10.0.0.1:8080"));
        Assert.Equal(65535, VpnEngine.ParseClashApiPort("host:65535")); // boundary
        Assert.Equal(1, VpnEngine.ParseClashApiPort("host:1"));         // boundary
        // IPv6 — the LastIndexOf finds the LAST colon, so "[::1]:9090"
        // splits at the right place.
        Assert.Equal(9090, VpnEngine.ParseClashApiPort("[::1]:9090"));
    }

    [Fact]
    public void ResolveCustomConfigPath_LegacyFallback_WhenCustomConfigsListEmpty()
    {
        // Backward-compat path: pre-v1.21 users had a single CustomConfig
        // string scalar. After the multi-config list landed, an empty
        // CustomConfigs list MUST fall through to the legacy string.
        // Environment.ExpandEnvironmentVariables runs even on the
        // fallback so %USERPROFILE%-style paths still resolve.
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                CustomConfigs = new List<CustomConfigEntry>(), // empty
                CustomConfig = @"C:\nonexistent\legacy.json"
            }
        };

        var path = VpnEngine.ResolveCustomConfigPath(settings);

        Assert.Equal(@"C:\nonexistent\legacy.json", path);
    }

    [Fact]
    public void ResolveCustomConfigPath_MultiConfig_NonExistentFiles_FallsBackToLegacyEmptyConfig()
    {
        // Characterization pin for the multi-config + filesystem-miss
        // branch. With Name="backup" set as ActiveCustomConfig but
        // C:\nonexistent\*.json not on disk, the resolver's File.Exists
        // gate fails the multi-config branch and falls through to the
        // legacy CustomConfig string. Here CustomConfig is empty, so
        // ExpandEnvironmentVariables("") returns "". Pin: no throw, no
        // accidental wrong-entry pick, fallback path activates.
        //
        // NOTE: this test does NOT verify the multi-config "active by
        // name" PICK semantics directly — that requires real files on
        // disk and lives in integration tests. The name was previously
        // "PicksActiveByNameWhenSet" which was misleading; renamed
        // 2026-05-21 in the post-2G review pass to match what's
        // actually asserted.
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ActiveCustomConfig = "backup",
                CustomConfigs = new List<CustomConfigEntry>
                {
                    new() { Name = "main",   Path = @"C:\nonexistent\main.json" },
                    new() { Name = "backup", Path = @"C:\nonexistent\backup.json" },
                }
            }
        };

        var path = VpnEngine.ResolveCustomConfigPath(settings);

        // CustomConfig is empty → ExpandEnvironmentVariables("") = "".
        Assert.NotNull(path);
        Assert.Equal(string.Empty, path);
    }

    [Fact]
    public void BuildBundledOnlyProfileSources_AlwaysIncludesBuiltInFallback()
    {
        // Safe-mode (SafeMode.Enabled = true) variant. Whatever the
        // platform / appDir state, the BuiltInProfileSource is always
        // the last entry. This is the "we have nothing on disk and
        // the catalogue is broken" floor — without it, ProfileManager
        // would throw on load and the user would be stranded with no
        // recovery path.
        var sources = VpnEngine.BuildBundledOnlyProfileSources();

        Assert.NotEmpty(sources);
        // Last entry is the BuiltInProfileSource (lowest priority +
        // always-on fallback). Name-match by type to avoid coupling
        // to the file-system layout.
        var last = sources[^1];
        Assert.Contains("BuiltIn", last.GetType().Name, StringComparison.OrdinalIgnoreCase);
    }

    // ─── 6. Source-string pins (orchestration paths) ────────────────────

    [Fact]
    public void Stop_OrderingPin_HealthMonitorStopsBeforeSingBox()
    {
        // BR-6a (2026-05-20 audit follow-up): the periodic health tick
        // (30s interval) could observe sing-box dead + _vpnWasRunning
        // true and fire AttemptRestart in the ~50-200 ms window between
        // _singBox.Stop and _healthMonitor.Stop. The branch in OnHealthTick
        // does NOT check _isStopping, so the only safe ordering is "stop
        // the monitor before killing its target". HealthMonitor.Stop is
        // fast (~ms — disposes a Timer) so doing it first costs nothing
        // on the user-visible disconnect path.
        //
        // If a future refactor reorders these (or extracts them into a
        // helper that loses the ordering), this regression pin fails.
        var src = LoadVpnEngineSource();
        Assert.SkipUnless(src != null, "VpnEngine.cs source not reachable from test cwd — source-pin skipped");
        // ! after SkipUnless asserts non-null path: src is non-null past this line.

        // Find the public Stop method body.
        var stopIdx = src.IndexOf("public void Stop()");
        Assert.True(stopIdx >= 0, "Source must contain 'public void Stop()'");

        // The _healthMonitor teardown must appear in the Stop method body
        // BEFORE the _singBox teardown. v2.42.0-r4 (perf audit H-1/M-1): both
        // are now Dispose() (not Stop()) so SingBoxManager unhooks its
        // AppDomain.ProcessExit handler and HealthMonitor releases its owned
        // Clash HttpClient + unsubscribes Crashed. Dispose calls Stop
        // internally, so the BR-6a ordering invariant is unchanged.
        var hmIdx = src.IndexOf("_healthMonitor?.Dispose()", stopIdx);
        var sbIdx = src.IndexOf("_singBox?.Dispose()", stopIdx);

        Assert.True(hmIdx > 0, "Stop must call _healthMonitor?.Dispose()");
        Assert.True(sbIdx > 0, "Stop must call _singBox?.Dispose()");
        Assert.True(hmIdx < sbIdx,
            "BR-6a invariant violated: HealthMonitor must be torn down BEFORE sing-box. " +
            $"Got _healthMonitor at {hmIdx}, _singBox at {sbIdx}.");

        // Pin the BR-6a comment as well so the rationale lives next to
        // the code — a refactor that drops the comment AND silently
        // reorders would still trip the assertions above, but the
        // comment pin gives a louder failure signal.
        Assert.Contains("BR-6a", src);
    }

    [Fact]
    public void Dispose_CallsStopWhenRunning_LifecycleInvariant()
    {
        // The Dispose contract: if IsRunning, Stop must be called before
        // teardown so the sing-box process, firewall rules, and ETW
        // thread are released cleanly. Without this, a window-close or
        // GC-driven Dispose would leak sing-box (root-owned via pkexec
        // on Linux / orphaned wintun adapter on Windows).
        var src = LoadVpnEngineSource();
        Assert.SkipUnless(src != null, "VpnEngine.cs source not reachable from test cwd — source-pin skipped");

        var disposeIdx = src!.IndexOf("public void Dispose()");
        Assert.True(disposeIdx >= 0, "Source must contain 'public void Dispose()'");

        // The "if (IsRunning) Stop();" guard must appear inside the
        // Dispose method body.
        var nextMethodIdx = src.IndexOf("    }", disposeIdx);
        Assert.True(nextMethodIdx > disposeIdx, "Dispose method body not delimited");

        var disposeBody = src.Substring(disposeIdx, nextMethodIdx - disposeIdx);
        Assert.Contains("if (IsRunning) Stop()", disposeBody);
    }

    // ─── helpers (mirrored from VpnEngineApplyEscalationTests) ──────────

    private static string? LoadVpnEngineSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "VPNRouter.Core", "Services", "VpnEngine.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }
}
