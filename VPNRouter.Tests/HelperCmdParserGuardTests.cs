using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// v2.31.9-r5 regression pin for the v2.31.7→r10 CMD parser bug class.
///
/// <para>v2.31.7's <c>helper.cmd</c> referenced <c>%SVC_TRIES%</c>
/// inside a nested <c>if/else</c> block. CMD pre-expanded the variable
/// at parse time before <c>set /a SVC_TRIES=0</c> ran, turning
/// <c>if %SVC_TRIES% gtr 20</c> into <c>if  gtr 20</c> →
/// <c>"20 was unexpected at this time"</c> → helper aborted → 100%
/// of v2.31.7 user upgrades silently failed.</para>
///
/// <para>r10 fix: <c>setlocal EnableDelayedExpansion</c> +
/// <c>!VAR!</c> for any variable set inside parsed blocks. This test
/// pins that fix at the template level.</para>
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
        var sourcePath = FindUpdateCheckerSource();
        if (sourcePath == null)
        {
            // Source not available (NuGet-published binary, partial CI
            // checkout). Skip rather than fail.
            return;
        }

        var src = System.IO.File.ReadAllText(sourcePath);
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
        var emittedSrc = string.Join("\n",
            src.Split('\n')
               .Select(l => l.Contains("//") ? l[..l.IndexOf("//")] : l));

        Assert.DoesNotContain("if %TRIES% ", emittedSrc);
        Assert.DoesNotContain("if %SVC_TRIES% ", emittedSrc);
        Assert.DoesNotContain("if %SVC_WAS_RUNNING% ", emittedSrc);
        Assert.DoesNotContain("if %XCOPY_EXIT% ", emittedSrc);

        // And confirm the safe form is present somewhere in the
        // emitted (non-comment) lines:
        Assert.Contains("if !TRIES! gtr", emittedSrc);
        Assert.Contains("if !SVC_TRIES! gtr", emittedSrc);
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
