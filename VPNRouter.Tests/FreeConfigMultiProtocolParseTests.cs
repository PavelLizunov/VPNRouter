using System.IO;
using System.Linq;
using VPNRouter.Core.Services.FreeConfigs;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// 2026-09-16 review P1: leftover VlessUriParser sites after the 2.50
/// multi-protocol overhaul must use ServerUriParser.
/// </summary>
public sealed class FreeConfigMultiProtocolParseTests
{
    [Fact]
    public void TryParseSourceLine_Hysteria2_KeepsHostAndProtocol()
    {
        var raw = "hysteria2://secret@hy2.example.com:443?sni=hy2.example.com#hy2";
        var entry = FreeConfigAggregator.TryParseSourceLine(raw, "https://source.example/list");
        Assert.NotNull(entry);
        Assert.Equal("hy2.example.com", entry!.Host);
        Assert.Equal(443, entry.Port);
        Assert.Equal("hysteria2", entry.Protocol, ignoreCase: true);
        Assert.Equal(raw, entry.RawUri);
    }

    [Fact]
    public void TryParseSourceLine_Shadowsocks_KeepsHost()
    {
        var raw = "ss://aes-128-gcm:test-password@ss.example.com:8388#ss";
        var entry = FreeConfigAggregator.TryParseSourceLine(raw, "https://source.example/list");
        Assert.NotNull(entry);
        Assert.Equal("ss.example.com", entry!.Host);
        Assert.Equal(8388, entry.Port);
    }

    [Fact]
    public void TryParseSourceLine_InvalidScheme_ReturnsNull()
    {
        Assert.Null(FreeConfigAggregator.TryParseSourceLine("https://example.com", "src"));
    }

    [Fact]
    public void LeftoverCallSites_UseServerUriParser_NotVlessUriParser()
    {
        var aggregator = ReadRepoFile("VPNRouter.Core", "Services", "FreeConfigs", "FreeConfigAggregator.cs");
        Assert.Contains("ServerUriParser.Parse(raw)", aggregator);
        Assert.DoesNotContain("VlessUriParser.Parse(raw)", aggregator);

        var androidApply = ReadRepoFile("VPNRouter.Android", "AndroidApp.FreeConfigs.cs");
        Assert.Contains("ServerUriParser.Parse(entry.RawUri)", androidApply);
        Assert.DoesNotContain("VlessUriParser.Parse(entry.RawUri)", androidApply);

        var androidVerify = ReadRepoFile("VPNRouter.Android", "AndroidFreeConfigDeepVerifier.cs");
        Assert.Contains("ServerUriParser.Parse(cfg.RawUri)", androidVerify);
        Assert.DoesNotContain("VlessUriParser.Parse(cfg.RawUri)", androidVerify);
    }

    private static string ReadRepoFile(params string[] segments)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir != null;
             dir = dir.Parent)
        {
            var path = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }

        throw new FileNotFoundException(
            $"Could not locate {Path.Combine(segments)} near {AppContext.BaseDirectory}");
    }
}
