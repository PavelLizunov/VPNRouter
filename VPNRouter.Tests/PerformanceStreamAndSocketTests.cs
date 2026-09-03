using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class PerformanceStreamAndSocketTests
{
    [Fact]
    public void TcpTlsProbe_ConfiguresLingerState_ToPreventTimeWait()
    {
        var source = LoadSource("VPNRouter.Core", "Services", "TcpTlsProbe.cs");

        // Assert both ProbeTcpAsync and ProbeTlsAsync configure immediate RST linger to avoid TIME_WAIT
        var probeTcpIndex = source.IndexOf("ProbeTcpAsync(", StringComparison.Ordinal);
        var probeTlsIndex = source.IndexOf("ProbeTlsAsync(", StringComparison.Ordinal);
        Assert.True(probeTcpIndex >= 0 && probeTlsIndex > probeTcpIndex);

        var tcpBody = source[probeTcpIndex..probeTlsIndex];
        var tlsBody = source[probeTlsIndex..];

        Assert.Contains("LingerState = new LingerOption(enable: true, seconds: 0)", tcpBody);
        Assert.Contains("LingerState = new LingerOption(enable: true, seconds: 0)", tlsBody);
    }

    [Fact]
    public void MainWindowViewModel_AddServer_BatchesSaveSettingsOutsideLoop()
    {
        var source = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.cs");
        var start = source.IndexOf("private void AddServer()", StringComparison.Ordinal);
        var end = source.IndexOf("private void RemoveServer()", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        var methodBody = source[start..end];

        // Verify SaveSettings() is guarded by addedAny outside the foreach loop
        Assert.Contains("var addedAny = false;", methodBody);
        Assert.Contains("addedAny = true;", methodBody);
        Assert.Contains("if (addedAny)", methodBody);
        Assert.Contains("SaveSettings();", methodBody);

        // Verify SaveSettings() is not called directly inside the foreach loop
        var foreachIndex = methodBody.IndexOf("foreach (var line in lines)", StringComparison.Ordinal);
        var afterLoop = methodBody.IndexOf("if (addedAny)", foreachIndex, StringComparison.Ordinal);
        var loopBody = methodBody[foreachIndex..afterLoop];
        Assert.DoesNotContain("SaveSettings();", loopBody);
    }

    [Fact]
    public void ServerUriParser_ParseMultiple_ParsesMixedStreamViaEnumerateLines()
    {
        var text = string.Join("\r\n",
            "vless://00000000-0000-0000-0000-000000000001@198.51.100.1:443?security=none#node1",
            "",
            "   ",
            "hysteria2://password@198.51.100.2:443?sni=example.com#node2",
            "unsupported://invalid@198.51.100.3:443#bad",
            "tuic://00000000-0000-0000-0000-000000000002:password@198.51.100.4:443#node3");

        var entries = ServerUriParser.ParseMultiple(text);

        Assert.Equal(3, entries.Count);
        Assert.Equal("node1", entries[0].Name);
        Assert.Equal("node2", entries[1].Name);
        Assert.Equal("node3", entries[2].Name);
    }

    [Fact]
    public void VlessUriParser_ParseMultiple_ParsesStreamViaEnumerateLines()
    {
        var text = string.Join("\n",
            "vless://00000000-0000-0000-0000-000000000001@198.51.100.1:443?security=none#vless1",
            "not-vless://abc@1.2.3.4:443#bad",
            "",
            "vless://00000000-0000-0000-0000-000000000002@198.51.100.2:443?security=none#vless2");

        var entries = VlessUriParser.ParseMultiple(text);

        Assert.Equal(2, entries.Count);
        Assert.Equal("vless1", entries[0].Name);
        Assert.Equal("vless2", entries[1].Name);
    }

    private static string LoadSource(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && directory != null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(
            $"Could not locate repository source: {Path.Combine(relativeParts)}");
    }
}
