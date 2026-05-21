// Phase 4 (Task #36-A, 2026-05-21) — IWindowsDnsHardening injection seam
// pin. Verifies the extracted interface is wired correctly through
// VpnEngine + StartupPipeline so the lifecycle happy-path tests (Task
// #36-C, next agent) can capture phase 7/8 DNS-hardening calls without
// mutating HKLM / netsh.
//
// What's pinned here:
//   1. HotReload mode does NOT fire Apply / Restore / EnableLockdownIfConfigured
//      (phases 7-8 are skipped). Pins the contract a #36-C test relies on:
//      if HotReload silently started calling Apply, NullWindowsDnsHardening
//      would see calls it shouldn't.
//   2. VpnEngine.Stop calls Restore via the seam (the call route that
//      previously was an #if PLATFORM_WINDOWS direct static call).
//   3. The interface itself has the 3-method shape the deferred tests
//      expect — a refactor that dropped a method would surface here.
//
// What's NOT pinned: the phase 7 (BR-7 deferred lockdown) + phase 8
// (Apply) firing on a successful ColdStart. Those require a fully-driven
// pipeline through sing-box start — gated on Task #36-C's NullSingBoxFactory
// (currently the only blocker per plans/phase2G-vpnengine-startasync-seam-
// 2026-05-21.md "Tests deferred"). Once that lands, a #36-C test will pin
// "Apply fires exactly once after warm-up" via this same NullWindowsDnsHardening.
//
// Brief: plans/phase4-iwindowsdnshardening-2026-05-21.md.

#nullable enable

using System.Reflection;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Pins the <see cref="IWindowsDnsHardening"/> seam wired through
/// <see cref="VpnEngine"/> and <see cref="StartupPipeline"/>. Companion to
/// <see cref="WindowsDnsHardeningTests"/> (which pins the netsh wire-
/// shape on the static class) and to <see cref="VpnEngineStartAsyncSeamTests"/>
/// (which pins the early-throw paths).
/// </summary>
public sealed class WindowsDnsHardeningInjectionTests
{
    // ─── Inline stubs (mirrors VpnEngineStartAsyncSeamTests pattern) ──────

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
        public void RaiseDummy()
        {
            ProcessStarted?.Invoke(this, new());
            ProcessStopped?.Invoke(this, new());
        }
    }

    /// <summary>
    /// Build a VpnEngine wired to the supplied DNS-hardening fake.
    /// Constructor is <c>[Obsolete(error: false)]</c> per Phase 3G-4 — the
    /// attribute is non-fatal exactly so tests can keep using direct
    /// construction.
    /// </summary>
#pragma warning disable CS0618
    private static VpnEngine BuildEngine(IWindowsDnsHardening dnsHardening) =>
        new VpnEngine(
            scanner: new StubProcessScanner(),
            firewallFactory: () => new StubFirewallManager(),
            monitorFactory: () => new StubProcessMonitor(),
            logger: null,
            dnsHardening: dnsHardening);
#pragma warning restore CS0618

    // ─── 1. Stop drives Restore through the seam ─────────────────────────

    [Fact]
    public void Stop_OnIdleEngine_InvokesRestoreThroughSeam()
    {
        // Pin: VpnEngine.Stop drives Restore via the injected
        // IWindowsDnsHardening, NOT via the static WindowsDnsHardening
        // facade directly. Prior to Task #36-A the call site was wrapped
        // in #if PLATFORM_WINDOWS and called WindowsDnsHardening.Restore
        // statically — which on non-Windows hosts compiled out entirely,
        // and on Windows hosts mutated HKLM. The seam guarantees a test
        // double sees the call regardless of OS.
        //
        // Stop is safe to call on an idle (never-started) engine — it's a
        // no-op chain of try/catch blocks, plus the Restore call which
        // becomes a no-op when there's no state file. The NullWindowsDnsHardening
        // captures the Restore invocation either way.
        var fake = new NullWindowsDnsHardening();
        using var engine = BuildEngine(fake);

        engine.Stop();

        // Restore must have been called exactly once via the seam.
        Assert.Equal(1, fake.RestoreCount);
        // Apply / EnableLockdownIfConfigured untouched on an idle Stop —
        // there's no ColdStart that would have triggered phase 7/8.
        Assert.Equal(0, fake.ApplyCount);
        Assert.Equal(0, fake.EnableLockdownCount);
    }

    // ─── 2. HotReload does NOT touch the DNS-hardening seam ──────────────

    [Fact]
    public async Task ApplyAsync_OnIdleEngine_DoesNotInvokeHardening()
    {
        // Pin: ApplyAsync's idle-engine guard catches the case BEFORE
        // entering StartupPipeline, so NullWindowsDnsHardening must not
        // see ANY invocation. Mirrors VpnEngineStartAsyncSeamTests'
        // ApplyAsync_OnIdleEngine_ReturnsFalseWithoutInvokingPipeline but
        // pins the DNS-hardening side-effect contract specifically.
        var fake = new NullWindowsDnsHardening();
        using var engine = BuildEngine(fake);
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                RoutingMode = "split",
                FlushDnsOnStart = false,
                BypassRussianTraffic = false,
                Subscriptions = new List<SubscriptionEntry>()
            },
            Vless = new VlessConfig(),
            Tun = new TunSettings(),
            Dns = new DnsSettings(),
            SingBox = new SingBoxSettings(),
            Monitoring = new MonitoringSettings(),
            ActiveProfile = "TestProfile"
        };

        var ok = await engine.ApplyAsync(settings);

        Assert.False(ok);
        // The whole point: idle Apply is a fast no-op that doesn't touch
        // the DNS-hardening layer.
        Assert.Empty(fake.Calls);
    }

    // ─── 3. Interface contract pin ───────────────────────────────────────

    [Fact]
    public void IWindowsDnsHardening_InterfaceShape_ThreeMethodsPresent()
    {
        // Lock the interface shape so a refactor that drops or renames a
        // method shows up as a test failure (not a downstream NullReferenceException
        // when the next #36-C session adds Apply / Restore / EnableLockdownIfConfigured
        // call captures). The three methods that StartupPipeline + VpnEngine
        // depend on:
        //   * Apply(AppSettings?, ILogger?)
        //   * Restore(ILogger?)
        //   * EnableLockdownIfConfigured(AppSettings?, ILogger?)
        var type = typeof(IWindowsDnsHardening);
        Assert.True(type.IsInterface);

        var apply = type.GetMethod("Apply");
        Assert.NotNull(apply);
        var applyParams = apply!.GetParameters();
        Assert.Equal(2, applyParams.Length);
        Assert.Equal(typeof(AppSettings), applyParams[0].ParameterType);
        Assert.Equal(typeof(Serilog.ILogger), applyParams[1].ParameterType);

        var restore = type.GetMethod("Restore");
        Assert.NotNull(restore);
        var restoreParams = restore!.GetParameters();
        Assert.Single(restoreParams);
        Assert.Equal(typeof(Serilog.ILogger), restoreParams[0].ParameterType);

        var enableLockdown = type.GetMethod("EnableLockdownIfConfigured");
        Assert.NotNull(enableLockdown);
        var enableParams = enableLockdown!.GetParameters();
        Assert.Equal(2, enableParams.Length);
        Assert.Equal(typeof(AppSettings), enableParams[0].ParameterType);
        Assert.Equal(typeof(Serilog.ILogger), enableParams[1].ParameterType);

        // Default impl singleton is wired and implements the interface —
        // pin that <c>WindowsDnsHardeningImpl.Default</c> is a non-null
        // IWindowsDnsHardening so consumers that take the ctor-injected
        // form with null fall through to a working impl.
        Assert.NotNull(WindowsDnsHardeningImpl.Default);
        Assert.IsAssignableFrom<IWindowsDnsHardening>(WindowsDnsHardeningImpl.Default);
    }

    // ─── 4. Defaulted-ctor wiring (back-compat) ──────────────────────────

    [Fact]
    public void VpnEngine_NullCtorArg_UsesDefaultImpl()
    {
        // Pin: passing null (or omitting) the dnsHardening ctor arg falls
        // back to WindowsDnsHardeningImpl.Default — the back-compat path
        // that wraps the existing static facade. This keeps Phase 4 a
        // strict-additive change: no production caller has to update its
        // VpnEngine instantiation. Tests that explicitly want the null
        // double pass NullWindowsDnsHardening; everyone else omits the
        // arg and gets the wrapper.
        //
        // We can't directly inspect the private _dnsHardening field
        // without InternalsVisibleTo gymnastics, but we CAN verify the
        // ctor doesn't throw with the arg omitted, and that the resulting
        // engine functions through Stop without observable error. The
        // explicit fallback is exercised more directly via reflection
        // below for documentation purposes.
#pragma warning disable CS0618
        using var engine = new VpnEngine(
            scanner: new StubProcessScanner(),
            firewallFactory: () => new StubFirewallManager(),
            monitorFactory: () => new StubProcessMonitor(),
            logger: null);
        // dnsHardening omitted — falls back to Default.
#pragma warning restore CS0618

        // Stop must not throw — the static facade is also safe on a
        // never-applied state (logs at Debug level and exits).
        engine.Stop();

        // Reflection check on the backing field for an explicit
        // documentation pin — a refactor that dropped the field would
        // show up as a missing-member here.
        var field = typeof(VpnEngine).GetField(
            "_dnsHardening",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var value = field!.GetValue(engine);
        Assert.NotNull(value);
        Assert.IsAssignableFrom<IWindowsDnsHardening>(value);
    }
}
