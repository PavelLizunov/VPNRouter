#nullable enable
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Baseline-compatible SOURCE-WIRING regression for NIGHT-12.
/// Source-only inspection verifying ClashLogStream construction passes the ClashApiSecret.
/// Strictly SOURCE-WIRING only: no live WebSocket, behavior, engine init, env globals, or task start.
/// </summary>
public sealed class NightBaselineTelemetryWiringTests
{
    [Fact]
    public void VpnEngine_TryStartConnectionHealthStream_PassesClashApiSecret()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "VPNRouter.Core")))
            dir = dir.Parent;

        if (dir == null)
        {
            dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "VPNRouter.Core")))
                dir = dir.Parent;
        }
        Assert.NotNull(dir);

        var vpnEnginePath = Path.Combine(dir!.FullName, "VPNRouter.Core", "Services", "VpnEngine.cs");
        Assert.True(File.Exists(vpnEnginePath), $"VpnEngine.cs not found at {vpnEnginePath}");

        var fullSrc = File.ReadAllText(vpnEnginePath);
        const string methodSignature = "void TryStartConnectionHealthStream(AppSettings settings)";
        var methodIdx = fullSrc.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(methodIdx >= 0, "TryStartConnectionHealthStream signature not found in VpnEngine.cs");

        var openBraceIdx = fullSrc.IndexOf('{', methodIdx);
        Assert.True(openBraceIdx > methodIdx, "Opening brace for TryStartConnectionHealthStream not found");

        var depth = 0;
        var closeBraceIdx = -1;
        for (var i = openBraceIdx; i < fullSrc.Length; i++)
        {
            if (fullSrc[i] == '{') depth++;
            else if (fullSrc[i] == '}' && --depth == 0)
            {
                closeBraceIdx = i;
                break;
            }
        }
        Assert.True(closeBraceIdx > openBraceIdx, "Closing brace for TryStartConnectionHealthStream not found");

        // Scoped to method body only (not whole file) to eliminate dummy comments elsewhere
        var methodSrc = fullSrc.Substring(methodIdx, closeBraceIdx - methodIdx + 1);

        // Strip comments (line and block) while preserving string literals
        var commentsStripped = Regex.Replace(
            methodSrc,
            @"(@""(?:""""|[^""])*""|""(?:\\.|[^""\\])*"")|(/\*[\s\S]*?\*/|//.*$)",
            m => m.Groups[1].Success ? m.Groups[1].Value : string.Empty,
            RegexOptions.Multiline);

        var ctorIdx = commentsStripped.IndexOf("new ClashLogStream(", StringComparison.Ordinal);
        Assert.True(ctorIdx >= 0, "new ClashLogStream constructor call not found in stripped method");

        var openParenIdx = commentsStripped.IndexOf('(', ctorIdx);
        var pDepth = 0;
        var closeParenIdx = -1;
        for (var i = openParenIdx; i < commentsStripped.Length; i++)
        {
            if (commentsStripped[i] == '(') pDepth++;
            else if (commentsStripped[i] == ')' && --pDepth == 0)
            {
                closeParenIdx = i;
                break;
            }
        }
        Assert.True(closeParenIdx > openParenIdx, "Closing parenthesis for constructor call not found");

        var ctorArgs = commentsStripped.Substring(openParenIdx + 1, closeParenIdx - openParenIdx - 1);
        Assert.Matches(@"secret\s*:\s*settings\.SingBox\.ClashApiSecret", ctorArgs);
    }
}
