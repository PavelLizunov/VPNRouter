using System;
using System.IO;
using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// P1.4 Runtime Status Adoption (audit handoff 2026-07-09). Load-bearing
/// source-string pins that the desktop UI never marks itself "Connected via
/// service" from a WEAK process-name signal.
///
/// <para><b>Finding A</b> — the one-shot startup adoption path
/// <c>MainWindowViewModel.DetectServiceManagedVpn()</c> used
/// <c>ProcessQuery.AnyAlive("sing-box")</c>, a bare name probe that ANY
/// third-party / dev / CTF sing-box satisfies. It now uses the
/// ownership-filtered <see cref="RuntimeStatusDetector.IsVpnRunning"/>
/// (→ <c>ProcessOwnership.AnySingBoxOwned</c>: image path under our bin dir or
/// the registered custom exe, unverifiable ⇒ not-owned, fail-closed), matching
/// the 2-second runtime poll which already did.</para>
///
/// <para><b>Finding B</b> — the runtime poll <c>SyncConnectedWithVpnRuntime</c>
/// must not promote a GUI-managed warmup-pending start to Connected on
/// process-presence alone. In this codebase that window is ALREADY closed by
/// the <c>IsConnecting</c> guard, which P1.3's <c>TwoPhaseStartCoordinator</c>
/// holds across BOTH phases (A: sing-box launch, B: TUN warmup probe) and only
/// releases after an awaited <c>_engine.Stop()</c> on every failure branch — so
/// a redundant <c>_guiManagedStartWarmupPending</c> flag was deliberately NOT
/// added. These pins keep a future refactor from silently reopening the gap
/// (flipping IsConnecting false before the warmup outcome, or dropping the
/// guard).</para>
///
/// <para>Behaviour-testing these paths needs process-image ownership mocking +
/// a live engine, so — like <see cref="ServiceAppCoexistenceTests"/> and
/// <c>OrphanCleanupGuardTests</c> — these are source pins.</para>
/// </summary>
public sealed class RuntimeStatusAdoptionTests
{
    // ── Finding A — startup adoption is ownership-filtered ──

    [Fact]
    public void DetectServiceManagedVpn_UsesOwnershipFilteredDetector_NotBareProcessName()
    {
        var body = LoadMethodBody(
            new[] { "VPNRouter.App", "ViewModels", "MainWindowViewModel.cs" },
            "DetectServiceManagedVpn");
        if (body == null) return; // partial CI checkout / shape changed

        var stripped = StripLineComments(body);

        // The fix: the ownership-filtered status seam.
        Assert.Contains("RuntimeStatusDetector.IsVpnRunning", stripped);

        // The regression: a bare name probe adopts ANY sing-box. This exact
        // call sat here pre-P1.4; it must never return (comments stripped so the
        // "supersedes ..." note doesn't fool the check).
        Assert.DoesNotContain("ProcessQuery.AnyAlive(\"sing-box\")", stripped);
        Assert.DoesNotContain("GetProcessesByName(\"sing-box\")", stripped);
    }

    [Fact]
    public void RuntimeStatusDetector_IsVpnRunning_DelegatesToOwnershipFilter()
    {
        var src = LoadSource("VPNRouter.Core", "Services", "RuntimeStatusDetector.cs");
        if (src == null) return;

        // IsVpnRunning is the single named public status seam Finding A routes
        // through; VPN-ownership must resolve via ProcessOwnership, not a name
        // probe. Point it back at ProcessQuery.AnyAlive and startup weakens.
        var stripped = StripLineComments(src);
        Assert.Contains("AnySingBoxOwned", stripped);
        Assert.DoesNotContain("ProcessQuery.AnyAlive(\"sing-box\")", stripped);
    }

    // ── Finding B — runtime poll can't promote a warmup-pending start ──

    [Fact]
    public void SyncConnectedWithVpnRuntime_ShortCircuitsWhileConnecting()
    {
        var body = LoadMethodBody(
            new[] { "VPNRouter.App", "ViewModels", "MainWindowViewModel.RuntimeStatus.cs" },
            "SyncConnectedWithVpnRuntime");
        if (body == null) return;

        var stripped = StripLineComments(body);

        // The warmup-pending guard: during a GUI-managed connect (IsConnecting
        // held true across P1.3 Phase A launch + Phase B TUN warmup) the poll
        // must bail before it can flip IsConnected from mere process-presence.
        Assert.Matches(@"if\s*\(\s*IsConnecting\s*\)\s*return", stripped);

        // And it must not re-introduce a bare name probe inside the method
        // (the caller feeds it the ownership-filtered runtime signal).
        Assert.DoesNotContain("ProcessQuery.AnyAlive(\"sing-box\")", stripped);
    }

    [Fact]
    public void RuntimePoll_FeedsSyncFromOwnershipFilteredDetector()
    {
        var body = LoadMethodBody(
            new[] { "VPNRouter.App", "ViewModels", "MainWindowViewModel.RuntimeStatus.cs" },
            "UpdateRuntimeStatus");
        // UpdateRuntimeStatus is the poll tick that computes vpnRunning and
        // hands it to SyncConnectedWithVpnRuntime. Fall back to whole-file if
        // the method was renamed so the pin still asserts something real.
        var src = body ?? LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.RuntimeStatus.cs");
        if (src == null) return;

        var stripped = StripLineComments(src);
        Assert.Contains("RuntimeStatusDetector.IsVpnRunning", stripped);
        Assert.Contains("SyncConnectedWithVpnRuntime", stripped);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static string? LoadSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }

    /// <summary>Extract a single method body: find the DEFINITION of
    /// <paramref name="methodName"/> (its param-list <c>)</c> is followed by
    /// <c>{</c> — a call site is followed by <c>;</c>, so early call sites like
    /// <c>DetectServiceManagedVpn();</c> are skipped) and brace-match to the
    /// matching close.
    /// ponytail: naive brace count — assumes braces inside string/interpolation
    /// literals in the method are balanced (true for the methods pinned here);
    /// upgrade to a real lexer only if a pinned method grows an unbalanced
    /// in-string brace.</summary>
    private static string? LoadMethodBody(string[] relativeParts, string methodName)
    {
        var src = LoadSource(relativeParts);
        if (src == null) return null;

        var needle = methodName + "(";
        for (var from = 0; ; )
        {
            var sigIdx = src.IndexOf(needle, from, StringComparison.Ordinal);
            if (sigIdx < 0) return null;
            from = sigIdx + needle.Length;

            // Match the param-list close paren.
            var paren = 0;
            var close = -1;
            for (var i = sigIdx + methodName.Length; i < src.Length; i++)
            {
                if (src[i] == '(') paren++;
                else if (src[i] == ')') { if (--paren == 0) { close = i; break; } }
            }
            if (close < 0) return null;

            // Definition ⇒ next non-ws char after ')' is '{'. Call ⇒ ';'.
            var j = close + 1;
            while (j < src.Length && char.IsWhiteSpace(src[j])) j++;
            if (j >= src.Length || src[j] != '{') continue; // call site — keep looking

            var depth = 0;
            for (var i = j; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}')
                {
                    depth--;
                    if (depth == 0) return src.Substring(j, i - j + 1);
                }
            }
            return null; // unbalanced — treat as not-found
        }
    }

    private static string StripLineComments(string src)
        => string.Join('\n',
            src.Split('\n').Select(l => l.Contains("//") ? l[..l.IndexOf("//", StringComparison.Ordinal)] : l));
}
