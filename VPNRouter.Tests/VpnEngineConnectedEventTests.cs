// Task #41 Stage 1 (PinkuDani 2026-05-21) — VpnEngine.Connected event
// characterization tests.
//
// Why: a prior "Fix #2" attempt wanted to split the App-side 30s VM Start
// timer into Phase A (start budget — wait for sing-box to come up) +
// Phase B (TUN warm-up budget — wait for confirmed routability).
// The implementation was correctly REFUSED because the only existing
// "Connected" signal was the StatusChanged string "Connected (PID N)",
// which StartupPipeline.ScheduleWarmupProbe emits on BOTH the success
// branch (line ~1073, gstatic probe succeeded) AND the failure branch
// (line ~1101, 15-attempt loop expired). Phase B sniffing on that
// string would accept warmup failure as success.
//
// Stage 1 (this file's subject under test) adds a typed
// VpnEngine.Connected event that fires ONLY on actual TUN-ready
// confirmation. Stage 2 (App-side two-phase VM timer in
// MainWindowViewModel) will subscribe to this event for the unambiguous
// Phase B completion signal.
//
// ── Scope realised vs scope deferred ──────────────────────────────────────
//
// What this file delivers (4 tests):
//
//   1. Connected_SuccessBranchOnly_FiresViaHostAdapter — drives
//      VpnEngineStartupHost.OnConnected (the nested adapter that
//      StartupPipeline calls) via reflection and pins that the engine's
//      public Connected event fires with the right PID. This is the
//      success-branch wiring contract.
//
//   2. Connected_FailureBranchSilent_SourcePin — defence pin via
//      File.ReadAllText on StartupPipeline.cs source. Verifies the
//      ScheduleWarmupProbe method has EXACTLY ONE call site for
//      _host.OnConnected and that call is BEFORE the for-loop exit
//      (i.e. the success branch only). The failure branch's symmetric
//      OnStatus("Connected (PID ...") emission still exists for back-
//      compat but does NOT call OnConnected.
//
//   3. Connected_FiresOncePerLifecycle_TwoCallsTwoEvents — pins the
//      per-lifecycle semantic: a fresh Start → success → second Start
//      → success fires Connected twice (not deduplicated). The engine
//      itself does no de-dup; that's the host adapter's contract.
//
//   4. Connected_NullSubscription_DoesNotThrow — defensive: invoking
//      the host's OnConnected with no Connected subscription on the
//      engine must not NRE (mirrors the C# event invocation idiom
//      "event?.Invoke(...)" — pin so a future refactor that drops
//      the null-conditional silently regresses).
//
// What's intentionally NOT here:
//
//   • End-to-end "real warmup probe succeeds → Connected fires" via
//     a full ColdStart through StartupPipeline. ScheduleWarmupProbe
//     instantiates `new HttpClient` inline (not via IHttpClientFactory)
//     and probes https://www.gstatic.com/generate_204 against the real
//     internet. Driving it deterministically needs an IHttpClient seam
//     that's separate to Stage 1. The same gap is documented in
//     VpnEngineLifecycleTests's "Group 5" file-header — once that seam
//     lands, Stage 1's Test 1 can be upgraded from "drive the adapter
//     directly" to "drive the full warmup loop."
//
// Cross-references:
//   • plans/phase2G-vpnengine-startasync-seam-2026-05-21.md (Agent B's
//     refusal that motivated Stage 1)
//   • plans/phase4-vpnengine-connected-event-stage1-2026-05-21.md
//     (this brief)
//   • StartupPipeline.cs:1088 (success branch — _host.OnConnected call)
//   • StartupPipeline.cs:1120 (failure branch — OnStatus emission only)
//   • VpnEngine.cs:Connected event field
//   • VpnEngine.VpnEngineStartupHost.OnConnected (event-raising adapter)

#nullable enable

using System.IO;
using System.Reflection;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// Characterization tests for the <see cref="VpnEngine.Connected"/> event
/// (Task #41 Stage 1).
///
/// <para>The event is a typed, success-branch-only signal that App-side
/// consumers use to detect actual TUN-ready confirmation (vs. the
/// ambiguous <c>"Connected (PID N)"</c> <c>StatusChanged</c> string
/// which is emitted on BOTH success and failure of the warmup probe
/// for pre-#41 back-compat).</para>
/// </summary>
public sealed class VpnEngineConnectedEventTests
{
    // ─── Inline stubs (mirrors VpnEngineOrchestratorTests pattern) ──────

    private sealed class StubProcessScanner : IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) => new();
    }

    private sealed class StubFirewallManager : IFirewallManager
    {
        public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true) { }
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
        public void RaiseDummy()
        {
            ProcessStarted?.Invoke(this, new());
            ProcessStopped?.Invoke(this, new());
        }
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

    /// <summary>
    /// Construct VpnEngine's nested <c>VpnEngineStartupHost</c> adapter via
    /// reflection. The host is the implementation of <c>IStartupHost</c>
    /// that <c>StartupPipeline</c> calls; its <c>OnConnected(int pid)</c>
    /// method is what fires the engine's public <c>Connected</c> event.
    ///
    /// <para>We poke the adapter directly (rather than driving a full
    /// ColdStart) because <c>ScheduleWarmupProbe</c> probes the real
    /// internet via an inline <c>new HttpClient</c> — see file-header
    /// scope notes for why an end-to-end test is deferred.</para>
    /// </summary>
    private static object BuildHostAdapter(VpnEngine engine)
    {
        var hostType = typeof(VpnEngine).GetNestedType(
            "VpnEngineStartupHost",
            BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "VpnEngine.VpnEngineStartupHost nested type not found. " +
                "Has it been renamed in a refactor?");
        return Activator.CreateInstance(
            hostType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object?[] { engine },
            culture: null)
            ?? throw new InvalidOperationException(
                "Could not construct VpnEngineStartupHost via reflection.");
    }

    private static void InvokeOnConnected(object host, int pid)
    {
        var method = host.GetType().GetMethod("OnConnected",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(int) },
            modifiers: null)
            ?? throw new InvalidOperationException(
                "VpnEngineStartupHost.OnConnected(int) not found.");
        method.Invoke(host, new object?[] { pid });
    }

    // ─── Test 1: Success branch fires Connected via host adapter ────────

    [Fact]
    public void Connected_SuccessBranchOnly_FiresViaHostAdapter()
    {
        // The pipeline's success branch calls _host.OnConnected(pidSnapshot).
        // VpnEngineStartupHost.OnConnected raises the engine's public
        // Connected event. Drive that adapter call directly and pin the
        // event fires once with the supplied PID.
        using var engine = BuildIdleEngine();
        var captured = new List<int>();
        engine.Connected += pid => captured.Add(pid);

        var host = BuildHostAdapter(engine);
        InvokeOnConnected(host, pid: 31415);

        Assert.Single(captured);
        Assert.Equal(31415, captured[0]);
    }

    // ─── Test 2: Failure branch is silent (source-string defence pin) ───

    [Fact]
    public void Connected_FailureBranchSilent_SourcePin()
    {
        // Defence pin: ScheduleWarmupProbe in StartupPipeline.cs has TWO
        // sites that emit the "Connected (PID N)" StatusChanged string —
        // the success branch (after http.GetStringAsync succeeded) and
        // the failure branch (after the 15-attempt for-loop expired).
        // ONLY the success branch must call _host.OnConnected; the
        // failure branch must NOT, otherwise Stage 2's App-side two-phase
        // VM timer would accept warmup failure as success.
        //
        // We pin this by source-scanning StartupPipeline.cs and asserting
        // ScheduleWarmupProbe has EXACTLY ONE _host.OnConnected call site.
        // Locating the source file via repo-root walk so the test works
        // from both `dotnet test` and `dotnet test --no-build` cwd.
        var sourcePath = LocateStartupPipelineSource();
        var source = File.ReadAllText(sourcePath);

        // Total _host.OnConnected sites across the whole pipeline must
        // be exactly 1 (the success branch). If a refactor accidentally
        // adds a second one — especially inside the failure branch — the
        // count breaks and this test fires.
        var totalSites = CountSubstring(source, "_host.OnConnected(");
        Assert.True(totalSites == 1,
            $"Expected exactly 1 _host.OnConnected call site in " +
            $"StartupPipeline.cs, found {totalSites}. If the new site is " +
            $"intentional (e.g. Stage 3+ migration), update this test to " +
            $"reflect the new contract.");

        // Locate the ScheduleWarmupProbe method body and the failure branch
        // within it. The failure branch sits AFTER the for-loop closing
        // brace — we slice the method body and assert OnConnected is NOT
        // mentioned in the failure half.
        var methodStart = source.IndexOf(
            "private void ScheduleWarmupProbe(",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0,
            "Could not locate ScheduleWarmupProbe in StartupPipeline.cs " +
            "source. Has the method been renamed?");

        // The failure branch begins with the Logger?.Warning call about
        // "TUN warm-up failed after". Everything between that anchor and
        // the lambda's closing "}, ct);" is the failure path.
        var failureBranchStart = source.IndexOf(
            "TUN warm-up failed after",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(failureBranchStart > methodStart,
            "Could not locate failure-branch anchor 'TUN warm-up failed " +
            "after' inside ScheduleWarmupProbe.");

        var failureBranchEnd = source.IndexOf("}, ct);",
            failureBranchStart, StringComparison.Ordinal);
        Assert.True(failureBranchEnd > failureBranchStart,
            "Could not locate failure-branch terminator '}, ct);'.");

        var failureBranch = source.Substring(
            failureBranchStart, failureBranchEnd - failureBranchStart);
        Assert.DoesNotContain("_host.OnConnected(", failureBranch);

        // Defence-in-depth: the failure branch must still emit the
        // back-compat OnStatus string so pre-#41 consumers that scan
        // StatusChanged for "Connected (PID" aren't broken. If a future
        // change strips both, that's a separate migration that needs to
        // touch every StatusChanged consumer — this test fires until
        // they do.
        Assert.Contains("OnStatus($\"Connected (PID {pidSnapshot})", failureBranch);
    }

    // ─── Test 3: Two lifecycles fire Connected twice (no de-dup) ────────

    [Fact]
    public void Connected_FiresOncePerLifecycle_TwoCallsTwoEvents()
    {
        // The host adapter does NOT de-duplicate Connected invocations:
        // each call to OnConnected raises the event. Stage 2's App-side
        // VM is responsible for any per-lifecycle gating (e.g. unsubscribe
        // after first fire) — Stage 1 just wires the raw signal.
        //
        // Pin this by calling OnConnected twice on the SAME host adapter
        // and asserting the event fires twice. Two different PIDs (matching
        // a hypothetical Start → Stop → Start sequence in production).
        using var engine = BuildIdleEngine();
        var captured = new List<int>();
        engine.Connected += pid => captured.Add(pid);

        var host = BuildHostAdapter(engine);
        InvokeOnConnected(host, pid: 11111);
        InvokeOnConnected(host, pid: 22222);

        Assert.Equal(2, captured.Count);
        Assert.Equal(11111, captured[0]);
        Assert.Equal(22222, captured[1]);
    }

    // ─── Test 4: Null subscription tolerated (no NRE) ───────────────────

    [Fact]
    public void Connected_NullSubscription_DoesNotThrow()
    {
        // C# event invocation idiom: `event?.Invoke(args)`. If a refactor
        // drops the null-conditional (writes `Connected.Invoke(pid)`) and
        // there's no subscriber, an NRE propagates out of the warmup
        // probe's success branch into the fire-and-forget Task.Run —
        // which would surface as an unobserved task exception (silent on
        // .NET 8 default unless TaskScheduler.UnobservedTaskException is
        // wired). The defensive pin: with NO subscriber, invoking
        // OnConnected on the host must NOT throw.
        using var engine = BuildIdleEngine();
        // Explicitly do NOT subscribe.
        var host = BuildHostAdapter(engine);

        // Should be a clean no-op.
        var ex = Record.Exception(() => InvokeOnConnected(host, pid: 99999));
        Assert.Null(ex);
    }

    // ─── Test helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Locate <c>StartupPipeline.cs</c> by walking up from the test binary
    /// directory until a folder containing <c>VPNRouter.sln</c> is found.
    /// Works from both <c>dotnet test</c> (cwd = build output) and the
    /// repo-root run patterns the CI uses.
    /// </summary>
    private static string LocateStartupPipelineSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var slnCandidate = Path.Combine(dir.FullName, "VPNRouter.sln");
            if (File.Exists(slnCandidate))
            {
                var srcPath = Path.Combine(
                    dir.FullName,
                    "VPNRouter.Core", "Services", "StartupPipeline.cs");
                if (File.Exists(srcPath)) return srcPath;
                throw new FileNotFoundException(
                    $"Found repo root at {dir.FullName} but " +
                    $"StartupPipeline.cs missing: {srcPath}");
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate repo root (no VPNRouter.sln found in " +
            $"any parent of {AppContext.BaseDirectory})");
    }

    private static int CountSubstring(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0;
        int idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
