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

    [Fact]
    public void BratStability_SuccessWritesTerminalEvidenceAfterCleanup()
    {
        var source = ReadRepoFile("tools", "brat-stability.ps1");

        var cleanup = source.LastIndexOf("finally {", StringComparison.Ordinal);
        var completed = source.LastIndexOf("Write-Evidence -Kind 'RunCompleted'", StringComparison.Ordinal);
        var summary = source.LastIndexOf("Write-Output ($summary", StringComparison.Ordinal);

        Assert.True(cleanup >= 0);
        Assert.True(completed > cleanup);
        Assert.True(summary > completed);
        Assert.Contains("-not $RunFailure -and -not $CleanupFailure -and -not $DataPlaneBlocked", source, StringComparison.Ordinal);
        Assert.Contains("MeasuredFailures = $MeasuredFailureCount", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BratStability_ProtocolLoadUsesSafeCoordinatesAndColdRepeats()
    {
        var source = ReadRepoFile("tools", "brat-stability.ps1");

        Assert.Contains("'ProtocolLoad'", source, StringComparison.Ordinal);
        Assert.Contains("SelectProtocol", source, StringComparison.Ordinal);
        Assert.Contains("ProtocolClass", source, StringComparison.Ordinal);
        Assert.Contains("ProtocolOrdinal", source, StringComparison.Ordinal);
        Assert.Contains("AbsoluteOrdinal", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-GameUdpLoad", source, StringComparison.Ordinal);
        Assert.Contains("Ensure-Disconnected", source, StringComparison.Ordinal);
        Assert.Contains("MEASURED_FAILURES", source, StringComparison.Ordinal);
        Assert.Contains("'Completed', 'ReplyGap', 'CookieFailure', 'NetworkFailure'", source, StringComparison.Ordinal);
        Assert.Contains("harness-integrity failure", source, StringComparison.Ordinal);
        Assert.Contains("Start VPN", source, StringComparison.Ordinal);
        Assert.Contains("Stop VPN", source, StringComparison.Ordinal);
        Assert.Contains("Open-SubscribePage", source, StringComparison.Ordinal);
        Assert.Contains("Switch-ToSimplePage", source, StringComparison.Ordinal);
        Assert.Contains("All traffic", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerName", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerHost", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerPort", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SubscriptionUrl", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BratStability_BrowserLoadUsesThreeFixedCleanCyclesWithoutSelectionMutation()
    {
        var source = ReadRepoFile("tools", "brat-stability.ps1");
        var browserLoad = Slice(source, "function Invoke-BrowserLoad", "function Invoke-ProtocolLoad");

        Assert.Contains("'BrowserLoad'", source, StringComparison.Ordinal);
        Assert.Contains("for ($cycle = 1; $cycle -le 3; $cycle++)", browserLoad, StringComparison.Ordinal);
        Assert.Contains("LoadProfile = 'BrowserBurst'", source, StringComparison.Ordinal);
        Assert.Contains("Ensure-Disconnected", browserLoad, StringComparison.Ordinal);
        Assert.Contains("Connect-And-Wait", browserLoad, StringComparison.Ordinal);
        Assert.Contains("Get-BratState", browserLoad, StringComparison.Ordinal);
        Assert.Contains("Get-Lifecycle", browserLoad, StringComparison.Ordinal);
        Assert.True(browserLoad.IndexOf("Get-Lifecycle", StringComparison.Ordinal) <
                    browserLoad.LastIndexOf("Ensure-Disconnected", StringComparison.Ordinal));
        Assert.DoesNotContain("Open-SubscribePage", browserLoad, StringComparison.Ordinal);
        Assert.DoesNotContain("Select-ProtocolRow", browserLoad, StringComparison.Ordinal);
        Assert.DoesNotContain("Switch-ToSimplePage", browserLoad, StringComparison.Ordinal);
    }

    [Fact]
    public void BratProtocolMatrix_IsFixedCheckpointedAndRestoresOriginalSelection()
    {
        var source = ReadRepoFile("tools", "brat-protocol-matrix.ps1");
        var gitignore = ReadRepoFile(".gitignore");
        var forbidden = new[] { "New-PSSession", "Invoke-Command", "Start-Process", "Get-NetAdapter", "Get-Process", "Copy-Item" };

        Assert.Contains("VlessReality'; Count = 4", source, StringComparison.Ordinal);
        Assert.Contains("VlessWebSocket'; Count = 3", source, StringComparison.Ordinal);
        Assert.Contains("VlessXhttp'; Count = 4", source, StringComparison.Ordinal);
        Assert.Contains("Hysteria2'; Count = 4", source, StringComparison.Ordinal);
        Assert.Contains("AmneziaWG'; Count = 4", source, StringComparison.Ordinal);
        Assert.Contains("Naive'; Count = 1", source, StringComparison.Ordinal);
        Assert.Contains("CellCompleted", source, StringComparison.Ordinal);
        Assert.Contains("CellSkippedCompleted", source, StringComparison.Ordinal);
        Assert.Contains("ProtocolClass Hysteria2 -ProtocolOrdinal 0", source, StringComparison.Ordinal);
        Assert.Contains("FinalCleanupPassed", source, StringComparison.Ordinal);
        Assert.Contains("ErrorClass", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ErrorMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SubscriptionUrl", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerHost", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerPort", source, StringComparison.Ordinal);
        foreach (var token in forbidden)
            Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/artifacts/brat-protocol-matrix/", gitignore, StringComparison.Ordinal);
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
