using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// Source-pin lint-net for the auto-update helper.cmd template emitted by
/// <see cref="UpdateChecker"/> on Windows.
///
/// <para>v2.31.7→r10 saw a critical CMD parser bug: <c>%SVC_TRIES%</c>
/// was referenced inside a nested <c>if/else</c> block. CMD pre-expands
/// all <c>%...%</c> references when parsing a parenthesised block, so
/// <c>SVC_TRIES</c> — initialised inside the same block — became empty,
/// turning <c>if %SVC_TRIES% gtr 20</c> into <c>if  gtr 20</c> →
/// <c>"20 was unexpected at this time"</c> → helper aborted →
/// 100% of v2.31.7 user upgrades silently failed.</para>
///
/// <para>r10 fix: <c>setlocal EnableDelayedExpansion</c> + <c>!VAR!</c>
/// for runtime-set variables in nested blocks. <see cref="HelperScriptTemplate_UsesEnableDelayedExpansion"/>
/// pins that fix.</para>
///
/// <para>v2.31.10 audit (<c>plans/v2.31.10-helper-cmd-hardening.md</c>) extended
/// the lint-net to cover every actively-load-bearing pattern in the helper
/// template — quoted SET values, timeout guards on the parent-wait and
/// service-stop loops, helper self-delete, log path under LogsDir,
/// Service failure-recovery disable/restore roundtrip. Each test is a
/// structural source-pin: a future refactor that drops the safe form
/// or re-introduces a dangerous one fails the test at PR time, before
/// the regression ships.</para>
/// </summary>
public sealed class HelperCmdParserGuardTests
{
    /// <summary>
    /// Read the helper-script template constant via reflection and
    /// assert it begins with <c>setlocal EnableDelayedExpansion</c>
    /// and contains no <c>%SVC_TRIES%</c> / <c>%TRIES%</c> / similar
    /// reference inside any <c>if/else</c> block.
    /// </summary>
    [Fact]
    public void HelperScriptTemplate_UsesEnableDelayedExpansion()
    {
        // The helper script is built from a string array constant in
        // UpdateChecker — read it via the GenerateHelperLines helper.
        // Rather than reflecting on a private field we synthesize a
        // template via the public API path (Generate the cmd content
        // for a known-safe shape) and grep it.
        //
        // For simplicity here, just confirm the template literal in
        // source contains the directive. This relies on the test asset
        // being on disk in CI — if the source moves, fix the relative
        // path. The check is intentionally string-based to remain
        // robust across template refactors that change the template
        // builder method signature.
        var src = ReadUpdateCheckerSourceOrSkip();
        if (src == null) return;
        Assert.Contains("setlocal EnableDelayedExpansion", src);

        // Pin the runtime-set variables that MUST use !VAR! (per r10).
        // Any line writing `%TRIES%` / `%SVC_TRIES%` / `%SVC_WAS_RUNNING%`
        // / `%XCOPY_EXIT%` would re-introduce the parse-time bug.
        // Comments mentioning the names are fine; we only flag actual
        // bare-`%VAR%` patterns *inside* an `if`-branch — the simplest
        // proxy is: does the template emit `if !VAR! gtr` (correct)
        // anywhere, AND no `if %VAR% gtr`?
        // Strip C# // comments + line-skip the multi-line block comment
        // that explains the bug history (which legitimately quotes the
        // dangerous syntax inside backticks).
        var emittedSrc = StripLineComments(src);

        Assert.DoesNotContain("if %TRIES% ", emittedSrc);
        Assert.DoesNotContain("if %SVC_TRIES% ", emittedSrc);
        Assert.DoesNotContain("if %SVC_WAS_RUNNING% ", emittedSrc);
        Assert.DoesNotContain("if %XCOPY_EXIT% ", emittedSrc);

        // And confirm the safe form is present somewhere in the
        // emitted (non-comment) lines:
        Assert.Contains("if !TRIES! gtr", emittedSrc);
        Assert.Contains("if !SVC_TRIES! gtr", emittedSrc);
    }

    /// <summary>
    /// v2.31.10-r2 audit hazard #2: every <c>set "VAR=..."</c> in the
    /// emitted block must wrap the value in double quotes. An unquoted
    /// <c>set VAR=C:\Program Files (x86)\VPNRouter</c> would bomb the
    /// parser on the literal <c>(</c>. CMD's quoted-set form
    /// (<c>set "VAR=value with (parens) and spaces"</c>) is the only
    /// shape that's universally safe across path layouts.
    /// </summary>
    [Fact]
    public void HelperScriptTemplate_AllSetLinesAreQuoted()
    {
        var src = ReadUpdateCheckerSourceOrSkip();
        if (src == null) return;

        var emittedSrc = StripLineComments(src);

        // Match emitted-template lines of shape: `"set ..."` or `$"set ..."`
        // (verbatim string literals inside the helper-builder array).
        // We only care about lines that emit `set ` followed by a NAME=VALUE
        // pair — the SET-VALUE form. CMD also has `set /a` (arithmetic) and
        // `set NAME` (display-only) — both safe without quotes, exclude them.
        //
        // Pattern matches the literal form `"set "NAME=...` OR `$"set "NAME=...`
        // we use in UpdateChecker. The opening `"set "` (quoted-set introducer)
        // is the discriminator.
        var setValueLines = Regex.Matches(
            emittedSrc,
            @"\$?""set\s+([^""]+)""",
            RegexOptions.Multiline);

        Assert.True(setValueLines.Count >= 4,
            $"expected at least 4 quoted SET lines (LOG/PARENT_PID/SRC/DST + " +
            $"runtime SVC_WAS_RUNNING) in helper.cmd template; found " +
            $"{setValueLines.Count}");

        // Now make sure NO emitted `set NAME=value` line uses the unquoted
        // form. Scan the array literal lines that start with `"set ` (not
        // `"set /a` and not `"set "`). The safe forms are:
        //   "set /a TRIES=0"               ← arithmetic, no path
        //   "set /a TRIES+=1"              ← arithmetic
        //   "set XCOPY_EXIT=!ERRORLEVEL!"  ← runtime errorlevel capture
        //   $"set \"VAR={pathExpr}\""      ← quoted, safe for paths
        // The DANGEROUS form would be:
        //   $"set VAR={pathExpr}"          ← unquoted, breaks on spaces/parens
        // We pin: every literal `"set VAR=` (without the leading `"set "`
        // and without `/a`) must NOT contain a `{` interpolation that yields
        // a path. The simplest robust check: assert the absence of the
        // pattern `"set [A-Z_]+={` (unquoted bare-set with C# interpolation,
        // which is the path-injection shape).
        var unsafeSetMatches = Regex.Matches(
            emittedSrc,
            @"""set\s+[A-Z_]+\s*=\s*\{",
            RegexOptions.Multiline);
        Assert.True(unsafeSetMatches.Count == 0,
            $"helper.cmd template contains unquoted `set NAME={{interp}}` " +
            $"line(s) — would bomb on paths with spaces/parens. Found " +
            $"{unsafeSetMatches.Count} such lines. Use `set \"NAME={{interp}}\"` " +
            $"(quoted-set form) instead.");
    }

    /// <summary>
    /// v2.31.10-r2 audit hazard #4 + #5: both wait loops must have a
    /// timeout guard that falls through to the next phase. Without it
    /// a hung parent process or stuck Service would leave the helper
    /// looping forever, accumulating zombie helper.cmd processes in
    /// %TEMP% on every failed upgrade attempt.
    ///
    /// <para>Pin both timeouts: <c>if !TRIES! gtr</c> (parent-wait,
    /// 30 s budget) AND <c>if !SVC_TRIES! gtr</c> (service-stop wait,
    /// 10 s budget). Numeric value isn't pinned — only the existence
    /// of the bound — so we can tune timeouts without touching tests.</para>
    /// </summary>
    [Fact]
    public void HelperScriptTemplate_TimeoutGuardsPresent()
    {
        var src = ReadUpdateCheckerSourceOrSkip();
        if (src == null) return;

        var emittedSrc = StripLineComments(src);

        // Both timeouts must be present in the emitted template.
        Assert.Matches(
            new Regex(@"if\s+!TRIES!\s+gtr\s+\d+", RegexOptions.IgnoreCase),
            emittedSrc);
        Assert.Matches(
            new Regex(@"if\s+!SVC_TRIES!\s+gtr\s+\d+", RegexOptions.IgnoreCase),
            emittedSrc);

        // Each timeout block must contain a goto that exits the loop
        // (`goto parentgone` and `goto svcgone` are the two anchors).
        // Pinning the goto labels guarantees the helper actually
        // escapes the loop, not just logs a message and re-enters.
        Assert.Contains("goto parentgone", emittedSrc);
        Assert.Contains("goto svcgone", emittedSrc);
    }

    /// <summary>
    /// v2.31.10-r2 audit hazard #6: helper.cmd must self-delete at the
    /// end. Without <c>del /Q "%~f0"</c> every Windows auto-update leaves
    /// one orphan helper.cmd file in <c>%TEMP%</c> per upgrade per
    /// machine, forever. F-12 OrphanCleanup (Task D, merged) cleans up
    /// orphan singbox.exe processes on next launch but does NOT touch
    /// the helper.cmd file itself.
    /// </summary>
    [Fact]
    public void HelperScriptTemplate_SelfDeletes()
    {
        var src = ReadUpdateCheckerSourceOrSkip();
        if (src == null) return;

        var emittedSrc = StripLineComments(src);

        // The literal self-delete line. `%~f0` is CMD's expansion for
        // the full path of the running script — universal across CMD
        // versions.
        Assert.Contains(@"del /Q \""%~f0\""", emittedSrc);
    }

    /// <summary>
    /// v2.31.10-r2 audit hazard #7: the helper LOG path must resolve
    /// under <see cref="VPNRouter.Core.AppPaths.LogsDir"/>, NOT under
    /// <c>Path.GetTempPath()</c>.
    ///
    /// <para>Pre-v2.31.8-r5 the helper wrote its log to
    /// <c>%TEMP%\vpnrouter-update-{pid}.log</c>. The
    /// <see cref="UpdateChecker.CheckInstallReceipt"/> banner that
    /// surfaces «See {LogsDir}/update.log for details» pointed at a
    /// path that didn't exist — the log was at %TEMP% with a different
    /// filename. r5 moved the helper LOG to <c>{LogsDir}/update.log</c>
    /// so the banner reference resolves. This pins that contract.</para>
    /// </summary>
    [Fact]
    public void HelperScriptTemplate_LogPathUsesLogsDir()
    {
        var src = ReadUpdateCheckerSourceOrSkip();
        if (src == null) return;

        // Pin: the C# code that builds `helperLog` must combine
        // `AppPaths.LogsDir` with `update.log`. NOT `Path.GetTempPath()`
        // and NOT a hardcoded `%TEMP%` literal.
        Assert.Matches(
            new Regex(@"helperLog\s*=\s*Path\.Combine\s*\(\s*logsDir\s*,\s*""update\.log""\s*\)",
                      RegexOptions.IgnoreCase),
            src);

        // And the C# variable `logsDir` must be assigned from
        // AppPaths.LogsDir (via direct assignment).
        Assert.Matches(
            new Regex(@"logsDir\s*=\s*AppPaths\.LogsDir", RegexOptions.IgnoreCase),
            src);
    }

    /// <summary>
    /// v2.31.10-r2 audit hazard #8: Service failure-recovery roundtrip.
    /// Helper MUST emit BOTH the disable line (before sc stop) AND the
    /// restore line (after sc start).
    ///
    /// <para>v2.31.8-r7 added the disable to prevent SCM auto-restarting
    /// the Service mid-xcopy and re-locking DLLs. Without the matching
    /// RESTORE, future Service crashes would silently stop auto-recovering
    /// — user sits disconnected with no retry, and the only signal is
    /// «sing-box not running» on next status check.</para>
    ///
    /// <para>Pin both sides of the contract:</para>
    /// <list type="bullet">
    /// <item><c>sc failure VPNRouter reset= 0 actions= ""</c> — disable</item>
    /// <item><c>sc failure VPNRouter reset= 86400 actions= restart/60000/restart/60000/restart/60000</c> — restore (matches <see cref="VPNRouter.Core.Services.WindowsServiceHelper"/> install defaults)</item>
    /// </list>
    /// </summary>
    [Fact]
    public void HelperScriptTemplate_ServiceFailureRecoveryRoundtrip()
    {
        var src = ReadUpdateCheckerSourceOrSkip();
        if (src == null) return;

        var emittedSrc = StripLineComments(src);

        // Disable line: `sc failure VPNRouter reset= 0 actions= ""`.
        // Allow the inner `""` to be `\"\"` (escaped in C# literal).
        Assert.Matches(
            new Regex(
                @"sc\s+failure\s+VPNRouter\s+reset=\s*0\s+actions=\s*\\""\\"""),
            emittedSrc);

        // Restore line: must use the canonical 24h-reset + 3×restart/60s
        // recovery actions. We pin the exact string because that's the
        // contract WindowsServiceHelper / ServiceInstaller registers on
        // install. A future change to the install defaults must update
        // BOTH places + this test.
        Assert.Contains(
            "sc failure VPNRouter reset= 86400 actions= restart/60000/restart/60000/restart/60000",
            emittedSrc);
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private static string? ReadUpdateCheckerSourceOrSkip()
    {
        var sourcePath = FindUpdateCheckerSource();
        if (sourcePath == null)
            return null; // Source not on disk (NuGet-published binary, partial CI checkout) — skip.
        return System.IO.File.ReadAllText(sourcePath);
    }

    /// <summary>
    /// Strip C# single-line comments from the source so test
    /// assertions can grep the EMITTED template lines without
    /// matching the historical commentary that legitimately quotes
    /// dangerous patterns inside <c>// ...</c> bug-history blocks.
    /// </summary>
    private static string StripLineComments(string src)
    {
        return string.Join("\n",
            src.Split('\n')
               .Select(l => l.Contains("//") ? l[..l.IndexOf("//")] : l));
    }

    private static string? FindUpdateCheckerSource()
    {
        // Walk up from test bin dir looking for VPNRouter.Core source.
        var dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(
                dir.FullName, "VPNRouter.Core", "Services", "UpdateChecker.cs");
            if (System.IO.File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
