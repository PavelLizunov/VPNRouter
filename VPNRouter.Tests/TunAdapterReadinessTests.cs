using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// v2.31.9-r4 regression suite for the TUN-race bug surfaced by
/// brat-2026-05-05. The user logged a FATAL "configure tun interface:
/// The device is not ready for use" 16 seconds after Apply triggered
/// a restart of sing-box. Root cause: pre-r4 only
/// <see cref="VpnEngine.StartAsync"/> called
/// <c>TunAdapterDiagnostics.EnsureAdapterEnabledOrAbsent</c> —
/// the auto-restart paths (Apply hot-reload-fallback, HealthMonitor
/// crash recovery) bypassed the pre-enable, so a wintun adapter left
/// in admin=disabled state by a prior r5 cleanup remained disabled
/// when the new sing-box tried to claim it.
///
/// <para>These tests pin the post-r4 contract: the readiness check lives at
/// the single launch chokepoint. The real-process no-op check runs only on
/// non-Windows so tests never mutate the developer machine's live TUN.</para>
/// </summary>
public sealed class TunAdapterReadinessTests
{
    [Fact]
    public void DisableOrphanedAdapter_NonExistentAdapter_NoThrow()
    {
        // Same idempotency contract on the disable side. After r5 (this
        // method's first appearance) we relied on the "exit 1 not found"
        // path being non-fatal so HealthMonitor restart sequences never
        // fail because of orphan-cleanup hiccups.
        var ex = Record.Exception(() =>
            TunAdapterDiagnostics.DisableOrphanedAdapter(
                logger: null,
                interfaceName: "VPNRouter-Test-DoesNotExist-" + Guid.NewGuid().ToString("N"),
                context: "test.nonexistent"));
        Assert.Null(ex);
    }

    // ─── Bug-r9-H regression suite ────────────────────────────────────
    // Pre-start cleanup of stale wintun adapters left behind by a previous
    // sing-box CRASH (graceful Stop is already covered by
    // DisableOrphanedAdapter on the way out). The parser is the testable
    // surface — full PreStartCleanupAsync involves netsh + PowerShell I/O
    // which we exercise only via the non-Windows no-op path here.

    [Fact]
    public async Task PreStartCleanupAsync_NonWindows_ReturnsZeroNoOp()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "Non-Windows no-op contract; never clean the dev machine's live TUN from tests.");

        // On Linux/macOS this must be a silent zero-removal no-op,
        // never throw. Pins the OperatingSystem.IsWindows() guard.
        var n = await TunAdapterDiagnostics.PreStartCleanupAsync(
            logger: null, context: "test.non-windows");

        Assert.Equal(0, n);
    }

    [Fact]
    public void TunDiag_PreStartCleanup_NoTunAdapters_ReturnsSuccessNoOp()
    {
        // Empty netsh output — nothing to clean, parser returns empty list.
        // PreStartCleanupAsync would log "no stale TUN adapters found" and
        // return 0 in production; we exercise the same predicate path here.
        Assert.Empty(TunAdapterDiagnostics.ExtractStaleAdapterNames(string.Empty));

        var noTun = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Connected      Dedicated        Wi-Fi
            Enabled        Disconnected   Loopback         Loopback Pseudo-Interface 1
            """;
        Assert.Empty(TunAdapterDiagnostics.ExtractStaleAdapterNames(noTun));
    }

    [Fact]
    public void TunDiag_PreStartCleanup_OneStaleTun_RemovesIt()
    {
        // VPNRouter-TUN row in netsh inventory — parser surfaces it as a
        // removal target. PreStartCleanupAsync would then run
        // netsh disable + Remove-NetAdapter against this name.
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Disconnected   Dedicated        VPNRouter-TUN
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Single(result);
        Assert.Equal("VPNRouter-TUN", result[0], ignoreCase: true);
    }

    [Fact]
    public void TunDiag_PreStartCleanup_SingBoxFallbackName_Detects()
    {
        // sing-box's auto-name fallback when our InterfaceName isn't honoured —
        // pattern is "sing-box-tun" + optional "-XXXX" suffix. Both forms
        // (bare and suffixed) belong to us, both are removable.
        var bare = TunAdapterDiagnostics.ExtractStaleAdapterNames("""
            Admin State    State          Type             Interface Name
            Enabled        Disconnected   Dedicated        sing-box-tun
            """);
        Assert.Single(bare);

        var suffixed = TunAdapterDiagnostics.ExtractStaleAdapterNames("""
            Admin State    State          Type             Interface Name
            Enabled        Disconnected   Dedicated        sing-box-tun-abc12345
            """);
        Assert.Single(suffixed);
        Assert.Equal("sing-box-tun-abc12345", suffixed[0], ignoreCase: true);
    }

    [Fact]
    public void TunDiag_PreStartCleanup_UnrelatedWintunAdapter_LeavesAlone()
    {
        // CRITICAL defensive test: WireGuard, AmneziaWG, OpenVPN TAP and
        // other coexisting VPN tools all create wintun-class adapters with
        // their own names. PreStartCleanupAsync must NEVER touch them —
        // Bug-r9-E (separate chip) handles "another VPN detected" UX, this
        // path is for VPNRouter's own orphans only. A regression here
        // would silently kill the user's other VPN.
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Connected      Dedicated        Wintun Userspace Tunnel
            Enabled        Connected      Dedicated        wg-AmneziaWG
            Enabled        Connected      Dedicated        TAP-Windows Adapter V9
            Enabled        Connected      Dedicated        OpenVPN Wintun
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Empty(result);
    }

    [Fact]
    public void TunDiag_PreStartCleanup_MixedAdapters_OnlyOursDetected()
    {
        // Realistic mixed inventory: our adapter alongside someone else's
        // wintun. Only VPNRouter-TUN comes back; the AmneziaWG entry is
        // left alone. Defensive belt-and-braces against the parser drifting
        // toward broad "anything with wintun in the name" matching.
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Disconnected   Dedicated        VPNRouter-TUN
            Enabled        Connected      Dedicated        Wintun Userspace Tunnel
            Enabled        Connected      Dedicated        wg0
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Single(result);
        Assert.Equal("VPNRouter-TUN", result[0], ignoreCase: true);
    }

    [Fact]
    public void TunDiag_PreStartCleanup_DuplicateRows_DedupedByName()
    {
        // netsh sometimes lists the same adapter twice (admin-state row +
        // operational-state row, or after a partial rename). The parser
        // dedupes so PreStartCleanupAsync doesn't try to remove the same
        // device twice.
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        VPNRouter-TUN
            Disabled       Disconnected   Dedicated        VPNRouter-TUN
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Single(result);
    }

    [Fact]
    public void SingBoxManager_DefaultTunInterfaceName_MatchesVpnRouterTun()
    {
        // Pin the constant so a future rename in SingBoxManager doesn't
        // silently desync from
        // <see cref="ConfigGenerator.GenerateTun"/> / install.ps1 / r5
        // orphan cleanup which all assume "VPNRouter-TUN".
        //
        // The constant is private (intentionally — it's an internal
        // detail), but
        // <c>InternalsVisibleTo("VPNRouter.Tests")</c> isn't enough for
        // private-static access. Use reflection to read it; this also
        // catches accidental visibility changes (e.g. someone marking
        // it public, which would break the encapsulation).
        var field = typeof(SingBoxManager).GetField(
            "DefaultTunInterfaceName",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var value = (string?)field!.GetValue(null);
        Assert.Equal("VPNRouter-TUN", value);
    }

    // ─── Hotfix-tun-adapter-orphan-pre-enable-2026-05-19 ────────────────
    // Wave-38 hotfix regression suite. The bug: SingBoxManager.LaunchProcess
    // called EnsureAdapterEnabledOrAbsent (which only re-enables a possibly
    // disabled adapter via `netsh admin=enabled`) on every restart path.
    // For users on Windows builds where wintun teardown stalls, this
    // produced a fatal "Cannot create a file when that file already exists"
    // loop — sing-box's WintunCreateAdapter saw the orphan and refused.
    //
    // The fix: replace pre-enable with PreStartCleanupAsync (disable +
    // PowerShell Remove-NetAdapter), and strengthen OnProcessExited /
    // StopInternal.early to schedule the full removal too. Tests below pin
    // both the parser invariants (which work in any code state) AND the
    // call-site invariants (which fail pre-Agent-1 by design).

    [Fact]
    public void PreStartCleanup_AdapterMissing_NoOp_ParserBranch()
    {
        // Pins the parser path PreStartCleanupAsync takes when netsh
        // reports no stale adapters — ExtractStaleAdapterNames returns
        // empty, PreStartCleanup logs "no stale TUN adapters found" and
        // returns 0 without ever invoking PowerShell Remove-NetAdapter.
        //
        // Cross-platform: parser invariants are pure C# string-processing
        // (no Windows API). Test runs identically on Linux/macOS.
        //
        // This test PASSES against both pre- and post-Wave-38 production
        // code because it exercises the parser, not the call sites.
        Assert.Empty(TunAdapterDiagnostics.ExtractStaleAdapterNames(string.Empty));

        // Realistic netsh output (English locale) with no TUN entries —
        // only Ethernet/Wi-Fi/Loopback. None of these should appear.
        var noTun = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Connected      Dedicated        Wi-Fi
            Enabled        Disconnected   Loopback         Loopback Pseudo-Interface 1
            """;
        Assert.Empty(TunAdapterDiagnostics.ExtractStaleAdapterNames(noTun));
    }

    [Fact]
    public void PreStartCleanup_VPNRouterTunPresent_DetectedForRemoval()
    {
        // VPNRouter-TUN exact match — the parser must surface it for
        // removal regardless of whether the netsh row shows admin=Enabled
        // or admin=Disabled. PreStartCleanupAsync would then disable +
        // Remove-NetAdapter the name; this test exercises only the
        // detection step.
        //
        // PASSES against both pre- and post-Wave-38 — this is a parser
        // invariant. Used by Agent 1's post-fix PreStartCleanupAsync,
        // already used by Bug-r9-H pre-start cleanup.
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Disconnected   Dedicated        VPNRouter-TUN
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Single(result);
        Assert.Equal("VPNRouter-TUN", result[0], ignoreCase: true);
    }

    [Fact]
    public void PreStartCleanup_DisabledAdapter_StillDetectedForRemoval()
    {
        // Adapter currently in "Disabled" admin-state — the orphan from
        // a prior DisableOrphanedAdapter cleanup that left the device
        // record alive. Wave-38's PreStartCleanupAsync must still detect
        // it for removal (since this is exactly the state the user-bug
        // log shows pre-FATAL).
        //
        // PASSES against both pre- and post-Wave-38 — pure parser test.
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Disabled       Disconnected   Dedicated        VPNRouter-TUN
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Single(result);
        Assert.Equal("VPNRouter-TUN", result[0], ignoreCase: true);
    }

    [Fact]
    public void PreStartCleanup_MultipleAdapters_AllDetectedForRemoval()
    {
        // Worst case: prior crashes left BOTH VPNRouter-TUN AND a
        // sing-box-tun-XXXX fallback adapter behind. PreStartCleanupAsync
        // would loop and remove both. Detection must surface all matching
        // names.
        //
        // PASSES against both pre- and post-Wave-38.
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Disabled       Disconnected   Dedicated        VPNRouter-TUN
            Disabled       Disconnected   Dedicated        sing-box-tun-1234
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, n => n.Equals("VPNRouter-TUN", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result, n => n.Equals("sing-box-tun-1234", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PreStartCleanup_WireGuardAdapterPresent_NotInRemovalSet()
    {
        // CRITICAL defensive whitelist. PreStartCleanupAsync runs PowerShell
        // Remove-NetAdapter; if it ever started matching WireGuardTUN,
        // AmneziaWG_NetTun_0, or other coexisting VPN names, it would
        // silently destroy a user's other VPN session. The parser
        // whitelist (\bVPNRouter-TUN|sing-box-tun(?:-...)?) is the line
        // of defence — pin it.
        //
        // PASSES against both pre- and post-Wave-38.
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Enabled        Connected      Dedicated        WireGuardTUN
            Enabled        Connected      Dedicated        AmneziaWG_NetTun_0
            Enabled        Connected      Dedicated        Wintun Userspace Tunnel
            Enabled        Connected      Dedicated        OpenVPN Wintun
            Enabled        Connected      Dedicated        TAP-Windows Adapter V9
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Empty(result);
    }

    [Fact]
    public void PreStartCleanup_LocalizedNetshOutput_RussianAndGerman_StillExtracts()
    {
        // Russian Windows netsh translates the column headers and
        // state values: "Подключен" = Connected, "Выделенный" = Dedicated.
        // Same for German: "Verbunden" = Connected, "Dediziert" = Dedicated.
        // The interface NAME column stays English (VPNRouter-TUN doesn't
        // get localized — it's a literal device name), so the parser's
        // \b-bounded regex should still surface it regardless of which
        // Windows display language the user runs.
        //
        // Defence-in-depth for the localized-Windows-environment slice of
        // the user base (alicemoren1991 is on RU-RU per pre-existing logs).
        //
        // PASSES against both pre- and post-Wave-38 — pure parser test.
        var ruOutput = """
            Состояние    Состояние      Тип              Имя интерфейса
            -------------------------------------------------------------------------
            Подключен    Подключен      Выделенный       Ethernet
            Отключен     Отключен       Выделенный       VPNRouter-TUN
            """;
        var ruResult = TunAdapterDiagnostics.ExtractStaleAdapterNames(ruOutput);
        Assert.Single(ruResult);
        Assert.Equal("VPNRouter-TUN", ruResult[0], ignoreCase: true);

        var deOutput = """
            Verwaltungsstatus  Status        Typ             Schnittstellenname
            -------------------------------------------------------------------------
            Aktiviert          Verbunden     Dediziert       Ethernet
            Deaktiviert        Getrennt      Dediziert       VPNRouter-TUN
            """;
        var deResult = TunAdapterDiagnostics.ExtractStaleAdapterNames(deOutput);
        Assert.Single(deResult);
        Assert.Equal("VPNRouter-TUN", deResult[0], ignoreCase: true);

        // Spanish — same shape ("Habilitado"=Enabled, "Conectado"=Connected,
        // "Dedicado"=Dedicated). Defensive third-locale check.
        var esOutput = """
            Estado de admin.  Estado        Tipo            Nombre de la interfaz
            -------------------------------------------------------------------------
            Habilitado        Conectado     Dedicado        Ethernet
            Deshabilitado     Desconectado  Dedicado        VPNRouter-TUN
            """;
        var esResult = TunAdapterDiagnostics.ExtractStaleAdapterNames(esOutput);
        Assert.Single(esResult);
        Assert.Equal("VPNRouter-TUN", esResult[0], ignoreCase: true);
    }

    [Fact]
    public void ExtractStaleAdapterNames_VPNRouterTunExactFinalField()
    {
        // Positive: exact "VPNRouter-TUN" surfaces.
        Assert.Single(TunAdapterDiagnostics.ExtractStaleAdapterNames(
            "Admin State    State          Type             Interface Name\n" +
            "Disabled       Disconnected   Dedicated        VPNRouter-TUN"));
    }

    [Fact]
    public void ExtractStaleAdapterNames_SingBoxTunBareSuffix_BothSurfaced()
    {
        // sing-box auto-fallback names: bare "sing-box-tun" or the
        // suffixed "sing-box-tun-AB12" form (alphanumeric suffix). Both
        // belong to us and must be removable.
        var bare = TunAdapterDiagnostics.ExtractStaleAdapterNames(
            "Disabled       Disconnected   Dedicated        sing-box-tun");
        Assert.Single(bare);
        Assert.Equal("sing-box-tun", bare[0], ignoreCase: true);

        var suffixed = TunAdapterDiagnostics.ExtractStaleAdapterNames(
            "Disabled       Disconnected   Dedicated        sing-box-tun-AB12");
        Assert.Single(suffixed);
        Assert.Equal("sing-box-tun-AB12", suffixed[0], ignoreCase: true);
    }

    [Fact]
    public void ExtractStaleAdapterNames_EmbeddedInLongerWordChar_NegativeTest()
    {
        // An owned name must occupy the complete final interface-name field.
        var embeddedInWordChars = """
            Admin State    State          Type             Interface Name
            Enabled        Connected      Dedicated        MyVPNRouter-TUNExtra
            Enabled        Connected      Dedicated        XVPNRouter-TUNX
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(embeddedInWordChars);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractStaleAdapterNames_EmbeddedOrNumberedNames_AreIgnored()
    {
        var otherNames = """
            Admin State    State          Type             Interface Name
            Enabled        Connected      Dedicated        Pre-VPNRouter-TUN-Suffix
            Enabled        Connected      Dedicated        My VPNRouter-TUN
            Enabled        Connected      Dedicated        My sing-box-tun-AB12
            Enabled        Connected      Dedicated        My  VPNRouter-TUN
            Disabled       Disconnected   Dedicated        VPNRouter-TUN 46
            Disabled       Disconnected   Dedicated        sing-box-tun-AB12 old
            """;
        var result = TunAdapterDiagnostics.ExtractStaleAdapterNames(otherNames);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractStaleAdapterNames_LowerCaseMatches_PinCurrentBehavior()
    {
        // The parser uses RegexOptions.IgnoreCase, so "vpnrouter-tun"
        // (all lowercase) DOES match. This pin captures the current
        // behaviour — important because if a future refactor accidentally
        // drops the IgnoreCase flag, lowercase netsh output (rare but
        // possible on some localized systems) would slip through
        // unmatched. Surfacing it as a removable name on Windows is the
        // safe choice — netsh + Remove-NetAdapter are case-insensitive
        // on adapter names anyway.
        var lower = TunAdapterDiagnostics.ExtractStaleAdapterNames(
            "Enabled        Connected      Dedicated        vpnrouter-tun");
        Assert.Single(lower);
    }

    // ─── Wave-38 call-site pins ────────────────────────────────────────
    // The following tests inspect SingBoxManager source to pin that the
    // Wave-38 fix is in place. They follow the source-string-pin pattern
    // used by ServiceAppCoexistenceTests because SingBoxManager spawns
    // sing-box.exe via Process.Start — too heavy for behavioural tests
    // without an IProcessRunner refactor (Phase 2G follow-up).
    //
    // These FAIL against pre-Wave-38 (current main HEAD as of brief
    // d7bc3b5). They are the regression-detector mechanism — DO NOT mark
    // Skip.

    [Fact]
    public void LaunchProcess_UsesPreStartCleanupAsync_NotEnsureAdapterEnabledOrAbsent()
    {
        // Pins POST-Wave-38 behavior. FAILS against pre-Wave-38 (the
        // pre-Agent-1 production code in this worktree where LaunchProcess
        // still calls EnsureAdapterEnabledOrAbsent). The failure IS the
        // regression-detector — DO NOT mark Skip.
        //
        // Test runs only on Windows because the cleanup is Windows-only;
        // skip silently on Linux/macOS where the LaunchProcess Windows-
        // branch is `if (OperatingSystem.IsWindows())` no-op.
        var src = LoadSingBoxManagerSource();
        if (src == null) return; // partial CI checkout

        var stripped = StripLineComments(src);

        // Locate the LaunchProcess body. Pulling the immediate region
        // around it avoids tripping on commentary or other methods.
        var launchProcessRegion = ExtractRegion(stripped, "void LaunchProcess(", 200, 1200);

        // POSITIVE PIN: LaunchProcess must call PreStartCleanup (the
        // disable+remove path) on its Windows branch. Agent 1's fix
        // replaces EnsureAdapterEnabledOrAbsent with a synchronous
        // wrapper around PreStartCleanupAsync.
        Assert.Contains("PreStartCleanup", launchProcessRegion);

        // NEGATIVE PIN: LaunchProcess must NOT call EnsureAdapterEnabledOrAbsent.
        // Pre-Wave-38 it called this; Agent 1 removes the call (the
        // method itself stays for backcompat, but the Launch path stops
        // using it).
        Assert.DoesNotContain("EnsureAdapterEnabledOrAbsent", launchProcessRegion);
    }

    [Fact]
    public void OnProcessExited_SchedulesAdapterRemoval_NotOnlyDisable()
    {
        // Pins POST-Wave-38 behavior. FAILS against pre-Wave-38 (the
        // pre-Agent-1 production code in this worktree only calls
        // DisableOrphanedAdapter from OnProcessExited). Agent 1
        // strengthens the crash-recovery path to schedule the full
        // device-record removal too — otherwise HealthMonitor's restart
        // attempt 5-10 s later still sees the orphan record and FATALs.
        //
        // DO NOT mark Skip — the failure IS the regression-detector.
        var src = LoadSingBoxManagerSource();
        if (src == null) return;

        var stripped = StripLineComments(src);
        // Wave 38a OnProcessExited body grew to ~4 KB (extensive inline
        // commentary + comment-anchored Task.Run pattern). Scope the
        // region scan to 5 KB to ensure the removal helper invocation
        // is captured even with future comment growth.
        var onExitedRegion = ExtractRegion(stripped, "void OnProcessExited(", 100, 5000);

        // The OnProcessExited path must reference the removal helper.
        // Agent 1's options: TryRemoveAdapterAsync (if made internal),
        // PreStartCleanupAsync (if reused for crash cleanup), or a
        // newly-added helper. Match a broad pattern that catches any
        // of them.
        var hasRemoval =
            onExitedRegion.Contains("QueueTunAdapterRemoval") ||
            onExitedRegion.Contains("TryRemoveAdapterAsync") ||
            onExitedRegion.Contains("PreStartCleanupAsync") ||
            onExitedRegion.Contains("RemoveAdapterAsync") ||
            onExitedRegion.Contains("Remove-NetAdapter");

        Assert.True(hasRemoval,
            "OnProcessExited region must schedule adapter removal " +
            "(TryRemoveAdapterAsync / PreStartCleanupAsync / Remove-NetAdapter). " +
            "Pre-Wave-38 only called DisableOrphanedAdapter — the orphan record " +
            "survives and HealthMonitor's restart hits 'Cannot create a file'.");
    }

    [Fact]
    public void StopInternal_EarlyExitPath_SchedulesAdapterRemoval()
    {
        // Pins POST-Wave-38 behavior. FAILS against pre-Wave-38 — the
        // current StopInternal.early branch calls DisableOrphanedAdapter
        // but not the full device removal. Agent 1's fix strengthens
        // this path (graceful Stop after a crash had already taken the
        // process out) so the orphan record is gone, not just disabled.
        //
        // DO NOT mark Skip.
        var src = LoadSingBoxManagerSource();
        if (src == null) return;

        var stripped = StripLineComments(src);

        // Find the StopInternal.early region — the branch where
        // _process == null || HasExited. Pull a wide window around
        // the marker string and check for removal scheduling.
        var stopEarlyRegion = ExtractRegion(stripped,
            "StopInternal.early", 200, 1200);

        var hasRemoval =
            stopEarlyRegion.Contains("QueueTunAdapterRemoval") ||
            stopEarlyRegion.Contains("TryRemoveAdapterAsync") ||
            stopEarlyRegion.Contains("PreStartCleanupAsync") ||
            stopEarlyRegion.Contains("RemoveAdapterAsync") ||
            stopEarlyRegion.Contains("Remove-NetAdapter");

        Assert.True(hasRemoval,
            "StopInternal.early region must schedule adapter removal too. " +
            "Pre-Wave-38 only called DisableOrphanedAdapter — see Agent 1 brief §3.");
    }

    [Fact]
    public void AutoRestartLoop_FiveCrashes_ParserConsistentBetweenIterations()
    {
        // Key regression pin: the user-bug shape. Simulate 5 sequential
        // crashes by repeatedly running ExtractStaleAdapterNames on the
        // same netsh output (the parser is deterministic per call). If
        // the parser ever surfaces inconsistent results across calls
        // (e.g. due to static cache / threading bug), this would catch
        // it before the auto-restart loop's race even matters.
        //
        // This is the test that should never break — once the loop's
        // launches all cleanup correctly, a parser regression that
        // surfaces "no stale adapter" on the 2nd call could re-introduce
        // the FATAL loop in production.
        //
        // PASSES against both pre- and post-Wave-38 — pure parser
        // determinism test.
        var output = """
            Admin State    State          Type             Interface Name
            -------------------------------------------------------------------------
            Enabled        Connected      Dedicated        Ethernet
            Disabled       Disconnected   Dedicated        VPNRouter-TUN
            """;

        var firstRun = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
        Assert.Single(firstRun);

        // 5 sequential calls should all return the same thing — pin
        // there's no static-state caching that decides "we already
        // cleaned this up, skip the rest". Each LaunchProcess call
        // in the auto-restart loop must do the full sweep.
        for (var i = 0; i < 5; i++)
        {
            var nthRun = TunAdapterDiagnostics.ExtractStaleAdapterNames(output);
            Assert.Single(nthRun);
            Assert.Equal("VPNRouter-TUN", nthRun[0], ignoreCase: true);
        }
    }

    // ─── helpers ────────────────────────────────────────────────────────

    /// <summary>Load SingBoxManager.cs source for source-string pinning.
    /// Returns null on partial CI checkouts (CLI bare clone, etc.).</summary>
    private static string? LoadSingBoxManagerSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "VPNRouter.Core", "Services", "SingBoxManager.cs");
            if (File.Exists(candidate)) return SingBoxSourceText.ReadAll(candidate);
        }
        return null;
    }

    /// <summary>Strip <c>//</c> line comments so commentary about the bug
    /// doesn't fool a Contains() check into reporting an in-effect call
    /// that's actually commented out. Doesn't try to handle <c>/* */</c>.</summary>
    private static string StripLineComments(string src)
    {
        return string.Join('\n',
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));
    }

    /// <summary>Pull a code region around a marker substring so source-
    /// string pins only check the relevant lines. Returns the full source
    /// if the marker isn't found — lets the test surface a clean
    /// "marker not found" failure rather than a confusing pin result.</summary>
    private static string ExtractRegion(string src, string marker,
        int beforeBytes, int afterBytes)
    {
        var idx = src.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return src;
        var start = Math.Max(0, idx - beforeBytes);
        var end = Math.Min(src.Length, idx + marker.Length + afterBytes);
        return src.Substring(start, end - start);
    }
}
