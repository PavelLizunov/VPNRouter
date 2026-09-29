using System;
using System.Diagnostics;
using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class CoreAuditPhaseBTests
{
    [Fact]
    public void MatchesPattern_CatastrophicWildcard_FailsFastInsteadOfHanging()
    {
        var pattern = string.Concat(Enumerable.Repeat("a*", 20)) + "b.exe";
        var input = new string('a', 60) + ".exe";

        var sw = Stopwatch.StartNew();
        var matched = ProcessScanner.MatchesPattern(input, pattern);
        sw.Stop();

        Assert.False(matched);
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"match took {sw.ElapsedMilliseconds}ms — the 250ms matchTimeout is not " +
            "enforced (B3-1 ReDoS regression: an untrusted scan_pattern can wedge the " +
            "routing engine).");
    }

    [Theory]
    [InlineData("Discord.exe", "Discord.exe", true)]
    [InlineData("evilDiscord.exe", "Discord.exe", false)]
    [InlineData("Discord.exe.bak", "Discord.exe", false)]
    [InlineData("Discordxexe", "Discord.exe", false)]
    [InlineData("a.b.exe", "a.b.exe", true)]
    [InlineData("axbxexe", "a.b.exe", false)]
    [InlineData("Discord.exe", "Disc*", true)]
    [InlineData("Telegram.exe", "Disc*", false)]
    [InlineData("Discord.exe", "Discord.?xe", true)]
    [InlineData("Discord.xe", "Discord.?xe", false)]
    public void MatchesPattern_AnchoredAndEscaped(string processName, string pattern, bool expected)
    {
        Assert.Equal(expected, ProcessScanner.MatchesPattern(processName, pattern));
    }

    [Fact]
    public void MatchesPattern_NormalMiss_ReturnsFalseFast()
    {
        var sw = Stopwatch.StartNew();
        var matched = ProcessScanner.MatchesPattern("chrome.exe", "Discord*");
        sw.Stop();
        Assert.False(matched);
        Assert.True(sw.ElapsedMilliseconds < 500);
    }
}
