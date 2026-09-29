using System.IO;
using System.Linq;
using VPNRouter.Core.Services.FreeConfigs;
using Xunit;

namespace VPNRouter.Tests;

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
