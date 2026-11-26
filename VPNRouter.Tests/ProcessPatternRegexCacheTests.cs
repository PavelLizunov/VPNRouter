#nullable enable
using System;
using System.Text.RegularExpressions;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ProcessPatternRegexCacheTests : IDisposable
{
    public ProcessPatternRegexCacheTests() => ProcessPatternRegexCache.ResetForTests();

    public void Dispose() => ProcessPatternRegexCache.ResetForTests();

    [Fact]
    public void SamePattern_ReturnsSameInstance()
    {
        var first = ProcessPatternRegexCache.Get("Discord*.exe");
        var second = ProcessPatternRegexCache.Get("Discord*.exe");
        Assert.Same(first, second);
    }

    [Fact]
    public void CaseVariant_ReturnsSameInstance()
    {
        var first = ProcessPatternRegexCache.Get("Discord*.exe");
        var second = ProcessPatternRegexCache.Get("discord*.EXE");
        Assert.Same(first, second);
    }

    [Fact]
    public void OptionsAndTimeout_AreUnchanged()
    {
        var regex = ProcessPatternRegexCache.Get("chrome*.exe");
        Assert.True(regex.Options.HasFlag(RegexOptions.IgnoreCase));
        Assert.True(regex.Options.HasFlag(RegexOptions.Compiled));
        Assert.Equal(
            TimeSpan.FromMilliseconds(ProcessPatternRegexCache.MatchTimeoutMilliseconds),
            regex.MatchTimeout);
    }

    [Theory]
    [InlineData("Discord*.exe", "Discord.exe", true)]
    [InlineData("Discord*.exe", "DiscordUpdate.exe", true)]
    [InlineData("Discord*.exe", "discord.exe", true)]
    [InlineData("Discord*.exe", "Telegram.exe", false)]
    [InlineData("app?.exe", "app1.exe", true)]
    [InlineData("app?.exe", "app12.exe", false)]
    public void WildcardSemantics_ArePreserved(
        string pattern, string processName, bool expected)
    {
        Assert.Equal(expected, ProcessPatternRegexCache.Get(pattern).IsMatch(processName));
    }

    [Fact]
    public void Cache_StaysWithinBound()
    {
        for (var i = 0; i < ProcessPatternRegexCache.MaxCachedPatterns + 128; i++)
            _ = ProcessPatternRegexCache.Get($"pattern-{i}.exe");

        Assert.True(ProcessPatternRegexCache.Count <= ProcessPatternRegexCache.MaxCachedPatterns);
    }

    [Fact]
    public void EvictedPattern_CanBeRecreated()
    {
        for (var i = 0; i < ProcessPatternRegexCache.MaxCachedPatterns + 32; i++)
            _ = ProcessPatternRegexCache.Get($"pattern-{i}.exe");

        var recreated = ProcessPatternRegexCache.Get("pattern-0.exe");
        Assert.True(recreated.IsMatch("pattern-0.exe"));
        Assert.True(ProcessPatternRegexCache.Count <= ProcessPatternRegexCache.MaxCachedPatterns);
    }

    [Fact]
    public void SharedCache_ServesBothScannerEntryPoints()
    {
        var direct = ProcessPatternRegexCache.Get("Telegram*.exe");

        Assert.True(ProcessScanner.MatchesPattern("TelegramDesktop.exe", "Telegram*.exe"));
        Assert.Same(direct, ProcessPatternRegexCache.Get("Telegram*.exe"));
    }
}
