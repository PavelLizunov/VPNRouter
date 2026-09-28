using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class HelperCmdParserGuardTests
{
    [Fact]
    public void HelperScriptTemplate_UsesEnableDelayedExpansion()
    {
        var src = ReadUpdateCheckerSourceOrSkip();
        if (src == null) return;
        Assert.Contains("setlocal EnableDelayedExpansion", src);

        var emittedSrc = StripLineComments(src);

        Assert.DoesNotContain("if %TRIES% ", emittedSrc);
        Assert.DoesNotContain("if %SVC_TRIES% ", emittedSrc);
        Assert.DoesNotContain("if %SVC_WAS_RUNNING% ", emittedSrc);
        Assert.DoesNotContain("if %XCOPY_EXIT% ", emittedSrc);

        Assert.Contains("if !TRIES! gtr", emittedSrc);
        Assert.Contains("if !SVC_TRIES! gtr", emittedSrc);
    }

    [Fact]
    public void HelperScriptTemplate_AllSetLinesAreQuoted()
    {
        var src = ReadUpdateCheckerSourceOrSkip();
        if (src == null) return;

        var emittedSrc = StripLineComments(src);

        var setValueLines = Regex.Matches(
            emittedSrc,
            @"\$?""set\s+([^""]+)""",
            RegexOptions.Multiline);

        Assert.True(setValueLines.Count >= 4,
            $"expected at least 4 quoted SET lines (LOG/PARENT_PID/SRC/DST + " +
            $"runtime SVC_WAS_RUNNING) in helper.cmd template; found " +
            $"{setValueLines.Count}");

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

    [Fact]
    public void HelperScriptTemplate_TimeoutGuardsPresent()
    {
        var src = ReadUpdateCheckerSourceOrSkip();
        if (src == null) return;

        var emittedSrc = StripLineComments(src);

        Assert.Matches(
            new Regex(@"if\s+!TRIES!\s+gtr\s+\d+", RegexOptions.IgnoreCase),
            emittedSrc);
        Assert.Matches(
            new Regex(@"if\s+!SVC_TRIES!\s+gtr\s+\d+", RegexOptions.IgnoreCase),
            emittedSrc);

        Assert.Contains("goto parentgone", emittedSrc);
        Assert.Contains(":wait_service_stop", emittedSrc);
        Assert.Matches(
            new Regex(@"if\s+!SVC_TRIES!\s+gtr\s+\d+\s*\(.*?goto :eof",
                      RegexOptions.IgnoreCase | RegexOptions.Singleline),
            emittedSrc);
    }

    [Fact]
    public void HelperScriptTemplate_WaitsForServiceProcessWithRealDelay()
    {
        var src = ReadUpdateCheckerSourceOrSkip();
        if (src == null) return;

        var emittedSrc = StripLineComments(src);
        var waitCall = emittedSrc.IndexOf("call :wait_service_stop", StringComparison.Ordinal);
        var serviceKill = emittedSrc.IndexOf(
            "taskkill /IM VPNRouter.Service.exe /F", StringComparison.Ordinal);
        var copy = emittedSrc.IndexOf("xcopy ", StringComparison.Ordinal);

        Assert.True(waitCall >= 0 && waitCall < serviceKill && serviceKill < copy,
            "helper must stop and clear VPNRouter.Service.exe before xcopy");
        Assert.DoesNotContain("ping -n 1", emittedSrc);
        Assert.Contains("ping -n 2 127.0.0.1", emittedSrc);
    }

    [Fact]
    public void HelperScriptTemplate_SelfDeletes()
    {
        var src = ReadUpdateCheckerSourceOrSkip();
        if (src == null) return;

        var emittedSrc = StripLineComments(src);

        Assert.Contains(@"del /Q \""%~f0\""", emittedSrc);
    }

    [Fact]
    public void HelperScriptTemplate_LogPathUsesLogsDir()
    {
        var src = ReadUpdateCheckerSourceOrSkip();
        if (src == null) return;

        Assert.Matches(
            new Regex(@"helperLog\s*=\s*Path\.Combine\s*\(\s*logsDir\s*,\s*""update\.log""\s*\)",
                      RegexOptions.IgnoreCase),
            src);

        Assert.Matches(
            new Regex(@"logsDir\s*=\s*AppPaths\.LogsDir", RegexOptions.IgnoreCase),
            src);
    }

    [Fact]
    public void HelperScriptTemplate_ServiceFailureRecoveryRoundtrip()
    {
        var src = ReadUpdateCheckerSourceOrSkip();
        if (src == null) return;

        var emittedSrc = StripLineComments(src);

        Assert.Matches(
            new Regex(
                @"sc\s+failure\s+VPNRouter\s+reset=\s*0\s+actions=\s*\\""\\"""),
            emittedSrc);

        Assert.Contains(
            "sc failure VPNRouter reset= 86400 actions= restart/60000/restart/60000/restart/60000",
            emittedSrc);
    }

    private static string? ReadUpdateCheckerSourceOrSkip()
    {
        var sourcePath = FindUpdateCheckerSource();
        if (sourcePath == null)
            return null;
        return System.IO.File.ReadAllText(sourcePath);
    }

    private static string StripLineComments(string src)
    {
        return string.Join("\n",
            src.Split('\n')
               .Select(l => l.Contains("//") ? l[..l.IndexOf("//")] : l));
    }

    private static string? FindUpdateCheckerSource()
    {
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
