#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ConnectionHealthFixtureCountsTests
{
    private static string? FixtureDir => System.Environment.GetEnvironmentVariable("VPNROUTER_DIAG_FIXTURES");
    private static string? ProxyEp => System.Environment.GetEnvironmentVariable("VPNROUTER_TEST_PROXY_EP");

    private sealed record Counts(Dictionary<ConnHealthCategory, int> Cats, int EofFails);

    private static Counts? CountFixture(string bundle)
    {
        var dir = FixtureDir;
        if (string.IsNullOrWhiteSpace(dir))
            return null;
        var path = Path.Combine(dir, bundle, "singbox-tail.log");
        if (!File.Exists(path))
            return null;

        IReadOnlySet<string>? eps = string.IsNullOrWhiteSpace(ProxyEp) ? null : new HashSet<string> { ProxyEp! };
        var cats = new Dictionary<ConnHealthCategory, int>();
        int eofFails = 0;
        foreach (var line in File.ReadLines(path))
        {
            var ev = ConnectionHealthClassifier.Classify(line, eps);
            if (ev is null) continue;
            cats[ev.Category] = cats.GetValueOrDefault(ev.Category) + 1;
            if (ev.Category == ConnHealthCategory.RelayOpenFail && ev.FailKind == RelayFailKind.Eof)
                eofFails++;
        }
        return new Counts(cats, eofFails);
    }

    [Fact]
    public void Bundle214717_FullTunnel_Counts()
    {
        var c = CountFixture("diag-214717");
        if (c is null) return;
        Assert.Equal(2178, c.Cats.GetValueOrDefault(ConnHealthCategory.RelayOpenFail));
        Assert.Equal(1952, c.EofFails);
        Assert.Equal(739, c.Cats.GetValueOrDefault(ConnHealthCategory.LocalClose));
        if (!string.IsNullOrWhiteSpace(ProxyEp))
            Assert.Equal(6, c.Cats.GetValueOrDefault(ConnHealthCategory.ProxyStreamError));
    }

    [Fact]
    public void Bundle205004_Split_Counts()
    {
        var c = CountFixture("diag-205004") ?? CountFixture("diag-20260619");
        if (c is null) return;
        Assert.Equal(1588, c.Cats.GetValueOrDefault(ConnHealthCategory.RelayOpenFail));
        Assert.Equal(1587, c.EofFails);
        Assert.Equal(216, c.Cats.GetValueOrDefault(ConnHealthCategory.LocalClose));
    }
}
