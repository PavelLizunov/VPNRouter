using System.Collections.Generic;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// F3 (v2.45.0): the longest-suffix matcher that maps a urltest member tag back
/// to a subscription row (server names can contain '-', so the longest matching
/// suffix wins). Replaces a per-poll LINQ sort with a single pass.
/// </summary>
public sealed class SuffixMatchTests
{
    private static int Match(IReadOnlyList<string?> names, string? tag)
        => SuffixMatch.LongestSuffixIndex(names, n => n, tag);

    [Fact]
    public void LongestSuffix_Wins()
    {
        // tag "vless-Iceland VLESS ~main-brat"; both "main-brat" and the full
        // "Iceland VLESS ~main-brat" are suffixes — the longer one wins.
        var names = new string?[] { "main-brat", "Iceland VLESS ~main-brat", "other" };
        Assert.Equal(1, Match(names, "vless-Iceland VLESS ~main-brat"));
    }

    [Fact]
    public void NameWithHyphen_Resolves()
    {
        var names = new string?[] { "us-east-1", "us" };
        Assert.Equal(0, Match(names, "hysteria2-us-east-1"));
    }

    [Fact]
    public void NoMatch_ReturnsMinusOne()
        => Assert.Equal(-1, Match(new string?[] { "a", "b" }, "vless-zzz"));

    [Fact]
    public void EmptyOrNullTag_ReturnsMinusOne()
    {
        Assert.Equal(-1, Match(new string?[] { "a" }, null));
        Assert.Equal(-1, Match(new string?[] { "a" }, ""));
    }

    [Fact]
    public void NullOrEmptyNames_Skipped()
    {
        var names = new string?[] { null, "", "node1" };
        Assert.Equal(2, Match(names, "vless-node1"));
    }
}
