#nullable enable

namespace VPNRouter.Tests;

public sealed class BratStabilityToolingContractTests
{
    [Fact]
    public void BratVerify_StateAndProbeActions_AreFixedTargetAndRedacted()
    {
        var source = ReadRepoFile("tools", "brat-verify.ps1");

        Assert.Contains("'state', 'probe', 'lifecycle'", source, StringComparison.Ordinal);
        Assert.Contains("$BratIp          = '100.115.182.0'", source, StringComparison.Ordinal);
        Assert.Contains("$BratMachineName = 'WINBRAT'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProbeHost", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProbeUrl", source, StringComparison.Ordinal);

        var state = Slice(source, "    'state' {", "    'probe' {");
        Assert.Contains("GuiCount", state, StringComparison.Ordinal);
        Assert.Contains("CoreCount", state, StringComparison.Ordinal);
        Assert.Contains("RouteScope", state, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessId", state, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandLine", state, StringComparison.Ordinal);
        Assert.DoesNotContain("PSComputerName", state, StringComparison.Ordinal);
        Assert.Contains("C:\\Program Files\\VPNRouter\\app\\VPNRouter.App.exe", state, StringComparison.Ordinal);
        Assert.Contains("C:\\ProgramData\\VPNRouter\\bin\\sing-box.exe", state, StringComparison.Ordinal);
    }

    [Fact]
    public void BratVerify_ProbeRequiresTunnelRouteBeforeNetworkSamples()
    {
        var source = ReadRepoFile("tools", "brat-verify.ps1");
        var probe = Slice(source, "    'probe' {", "    'lifecycle' {");

        var routeGate = probe.IndexOf("if ($routeScope -ne 'Tunnel')", StringComparison.Ordinal);
        var httpSample = probe.IndexOf("New-Object System.Net.Http.HttpClient", StringComparison.Ordinal);
        var udpSample = probe.IndexOf("New-Object System.Net.Sockets.UdpClient", StringComparison.Ordinal);

        Assert.True(routeGate >= 0);
        Assert.True(httpSample > routeGate);
        Assert.True(udpSample > routeGate);
        Assert.Contains("@(64, 512, 1200, 1392)", probe, StringComparison.Ordinal);
        Assert.Contains("InvalidResponse", probe, StringComparison.Ordinal);
        Assert.Contains("SocketErrorCode", probe, StringComparison.Ordinal);
        Assert.Contains("SocketError]::TimedOut", probe, StringComparison.Ordinal);
        Assert.Contains("Dataplane verification is blocked", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void BratVerify_LifecycleNeverReturnsRawLogLines()
    {
        var source = ReadRepoFile("tools", "brat-verify.ps1");
        var lifecycle = Slice(source, "    'lifecycle' {", "    'logs' {");

        Assert.Contains("EventCounts", lifecycle, StringComparison.Ordinal);
        Assert.Contains("UnknownErrorCount", lifecycle, StringComparison.Ordinal);
        Assert.Contains("Kind = $kind", lifecycle, StringComparison.Ordinal);
        Assert.DoesNotContain("Hits =", lifecycle, StringComparison.Ordinal);
        Assert.DoesNotContain("File =", lifecycle, StringComparison.Ordinal);
        Assert.DoesNotContain("$line }", lifecycle, StringComparison.Ordinal);
    }

    [Fact]
    public void BratStability_CoordinatorDelegatesAllRemoteWorkToBratVerify()
    {
        var source = ReadRepoFile("tools", "brat-stability.ps1");
        var forbidden = new[]
        {
            "New-PSSession",
            "Invoke-Command",
            "New-ScheduledTask",
            "UIAutomation",
            "Get-NetAdapter",
            "Find-NetRoute",
            "Get-Process",
            "Get-CimInstance",
            "Copy-Item",
        };

        Assert.Contains("brat-verify.ps1", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-BratVerify", source, StringComparison.Ordinal);
        foreach (var token in forbidden)
            Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BratStability_CleanupIsUnconditionalAndEvidenceStaysIgnored()
    {
        var source = ReadRepoFile("tools", "brat-stability.ps1");
        var gitignore = ReadRepoFile(".gitignore");

        Assert.Contains("finally {", source, StringComparison.Ordinal);
        Assert.Contains("if ($MutexHeld -and $Mode -ne 'Cleanup')", source, StringComparison.Ordinal);
        Assert.Contains("Ensure-Disconnected", source, StringComparison.Ordinal);
        Assert.Contains("ErrorClass", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ErrorMessage", source, StringComparison.Ordinal);
        Assert.Contains("/artifacts/brat-stability/", gitignore, StringComparison.Ordinal);
        Assert.Contains("!tools/brat-stability.ps1", gitignore, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VPNRouter.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var path = Path.Combine(new[] { directory!.FullName }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"Repository file not found: {path}");
        return File.ReadAllText(path);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        Assert.True(end > start, $"End marker not found after {startMarker}: {endMarker}");
        return source[start..end];
    }
}
