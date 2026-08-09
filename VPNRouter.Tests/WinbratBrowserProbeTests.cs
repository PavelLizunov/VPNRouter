#nullable enable

#nullable enable

using VPNRouter.Tools.WinbratBrowserProbe;

namespace VPNRouter.Tests;

public sealed class WinbratBrowserProbeTests
{
    [Fact]
    public void PageState_ParsesOnlyNonNegativeAggregate()
    {
        Assert.True(BrowserPageState.TryParse(
            "{\"fetchOk\":32,\"fetchFail\":1,\"wsOk\":4,\"wsFail\":0,\"done\":true}",
            out var state));
        Assert.Equal(new BrowserPageState(32, 1, 4, 0, true), state);

        Assert.False(BrowserPageState.TryParse(
            "{\"fetchOk\":-1,\"fetchFail\":0,\"wsOk\":0,\"wsFail\":0,\"done\":false}", out _));
        Assert.False(BrowserPageState.TryParse(
            "{\"fetchOk\":1,\"fetchFail\":0,\"wsOk\":0,\"done\":false}", out _));
        Assert.False(BrowserPageState.TryParse("not-json", out _));
    }

    [Fact]
    public void ProgressTracker_TracksFetchAndWebSocketGapsSeparately()
    {
        var tracker = new BrowserProgressTracker(TimeSpan.Zero);

        Assert.True(tracker.Observe(new(1, 0, 0, 0, false), TimeSpan.FromSeconds(1)));
        Assert.True(tracker.Observe(new(1, 0, 1, 0, false), TimeSpan.FromSeconds(3)));
        Assert.True(tracker.Observe(new(2, 0, 1, 0, false), TimeSpan.FromSeconds(5)));

        Assert.Equal(4_000, tracker.MaxFetchNoProgressMs);
        Assert.Equal(3_000, tracker.MaxWsNoProgressMs);
    }

    [Fact]
    public void ProgressTracker_FailuresDoNotHideMissingSuccessfulProgress()
    {
        var tracker = new BrowserProgressTracker(TimeSpan.Zero);

        Assert.True(tracker.Observe(new(0, 1, 0, 1, false), TimeSpan.FromSeconds(2)));
        Assert.True(tracker.Observe(new(1, 1, 1, 1, false), TimeSpan.FromSeconds(5)));

        Assert.Equal(5_000, tracker.MaxFetchNoProgressMs);
        Assert.Equal(5_000, tracker.MaxWsNoProgressMs);
    }

    [Fact]
    public void ProgressTracker_RejectsCounterAndDoneRegression()
    {
        var tracker = new BrowserProgressTracker(TimeSpan.Zero);

        Assert.True(tracker.Observe(new(2, 0, 1, 0, true), TimeSpan.FromSeconds(1)));
        Assert.False(tracker.Observe(new(1, 0, 1, 0, true), TimeSpan.FromSeconds(2)));
        Assert.False(tracker.Observe(new(2, 0, 1, 0, false), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Payload_RejectsEveryCommandLineInputWithoutLaunchingEdge()
    {
        var result = await BrowserProbe.RunAsync(["target-or-profile"], CancellationToken.None);

        Assert.Equal(BrowserProbeLifecycle.InputRejected, result.Lifecycle);
        Assert.Equal(0, result.FetchOk);
        Assert.False(result.Done);
    }

    [Fact]
    public void ResultJson_IsOneSafeFixedAggregateShape()
    {
        var result = new BrowserProbeResult(BrowserProbeLifecycle.Completed, 1, 2, 3, 4, true, 5, 6);

        Assert.Equal(
            "{\"lifecycle\":\"Completed\",\"fetchOk\":1,\"fetchFail\":2,\"wsOk\":3,\"wsFail\":4,\"done\":true,\"maxFetchNoProgressMs\":5,\"maxWsNoProgressMs\":6}",
            BrowserProbeJson.Serialize(result));
    }

    [Fact]
    public void PayloadContract_IsFixedToOwnedPageAndAggregateExpression()
    {
        Assert.Collection(BrowserProbe.BrowserCandidates,
            candidate => Assert.Equal((Path.Combine(AppContext.BaseDirectory, "chrome-win64", "chrome.exe"), "PinnedArchive"), (candidate.Path, candidate.Vendor)),
            candidate => Assert.Equal((@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "Microsoft"), (candidate.Path, candidate.Vendor)),
            candidate => Assert.Equal((@"C:\Program Files\Microsoft\Edge\Application\msedge.exe", "Microsoft"), (candidate.Path, candidate.Vendor)),
            candidate => Assert.Equal((@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google"), (candidate.Path, candidate.Vendor)),
            candidate => Assert.Equal((@"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe", "Google"), (candidate.Path, candidate.Vendor)));
        Assert.Equal("https://loadtest.vpn.ninitux.com/browser", BrowserProbe.FixedPage);
        Assert.Equal(
            "JSON.stringify({fetchOk:state.fetchOk,fetchFail:state.fetchFail,wsOk:state.wsOk,wsFail:state.wsFail,done:state.done})",
            BrowserProbe.FixedExpression);
    }

    [Fact]
    public void PayloadSource_BoundsPollAfterTheFixedPageDurationAndOwnsItsProfile()
    {
        var source = ReadRepoFile("VPNRouter.Tools", "WinbratBrowserProbe", "BrowserProbe.cs");

        Assert.Contains("Path.Combine(AppContext.BaseDirectory, \"browser-profile\")", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMinutes(11)", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\r4review\browser-probe", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadSource_MapsUnexpectedFailuresToFixedStagesWithoutExceptionText()
    {
        var source = ReadRepoFile("VPNRouter.Tools", "WinbratBrowserProbe", "BrowserProbe.cs");

        Assert.Contains("PagePollingFailure", source, StringComparison.Ordinal);
        Assert.Contains("var stage = BrowserProbeLifecycle.EdgeLaunchFailed;", source, StringComparison.Ordinal);
        Assert.Contains("stage = BrowserProbeLifecycle.DevToolsUnavailable;", source, StringComparison.Ordinal);
        Assert.Contains("stage = BrowserProbeLifecycle.PageUnavailable;", source, StringComparison.Ordinal);
        Assert.Contains("stage = BrowserProbeLifecycle.PagePollingFailure;", source, StringComparison.Ordinal);
        Assert.Contains("result = Empty(stage);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception.Message", source, StringComparison.Ordinal);
        Assert.True(source.IndexOf("catch (ProbeFailure failure)", StringComparison.Ordinal) <
                    source.IndexOf("result = Empty(stage);", StringComparison.Ordinal));
    }

    [Fact]
    public void BrowserStartInfo_IsHeadlessLoopbackFreshProfileAndFullyDrained()
    {
        if (!OperatingSystem.IsWindows()) return;
        var profile = Path.Combine(BrowserProbe.FixedProfileRoot, "run-" + new string('a', 32));

        var browser = BrowserProbe.BrowserCandidates[3];
        var start = BrowserProbe.CreateStartInfo(profile, browser.Path);

        Assert.Equal(browser.Path, start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Contains("--headless=new", start.ArgumentList);
        Assert.Contains("--disable-background-networking", start.ArgumentList);
        Assert.Contains("--disable-component-update", start.ArgumentList);
        Assert.Contains("--disable-sync", start.ArgumentList);
        Assert.Contains("--remote-debugging-address=127.0.0.1", start.ArgumentList);
        Assert.Contains("--remote-debugging-port=0", start.ArgumentList);
        Assert.Contains("--user-data-dir=" + profile, start.ArgumentList);
        Assert.Equal(BrowserProbe.FixedPage, start.ArgumentList[^1]);
    }

    [Fact]
    public void BrowserStartInfo_RejectsNonAllowlistedExecutable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var profile = Path.Combine(BrowserProbe.FixedProfileRoot, "run-" + new string('c', 32));

        Assert.Throws<InvalidOperationException>(() => BrowserProbe.CreateStartInfo(profile, @"C:\elsewhere\browser.exe"));
    }

    [Fact]
    public void ProfileValidation_AllowsOnlyOneFixedRunDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        var valid = Path.Combine(BrowserProbe.FixedProfileRoot, "run-" + new string('b', 32));

        BrowserProbe.ValidateProfilePath(valid);
        Assert.Throws<InvalidOperationException>(() => BrowserProbe.ValidateProfilePath(BrowserProbe.FixedProfileRoot));
        Assert.Throws<InvalidOperationException>(() => BrowserProbe.ValidateProfilePath(Path.Combine(valid, "child")));
        Assert.Throws<InvalidOperationException>(() => BrowserProbe.ValidateProfilePath(@"C:\r4review\elsewhere\run-" + new string('b', 32)));
    }

    [Fact]
    public void SourceContract_DrainsWithoutPersistenceAndBoundsDevToolsAndCleanup()
    {
        var source = ReadRepoFile("VPNRouter.Tools", "WinbratBrowserProbe", "BrowserProbe.cs");
        var program = ReadRepoFile("VPNRouter.Tools", "WinbratBrowserProbe", "Program.cs");

        Assert.Contains("DevToolsActivePort", source, StringComparison.Ordinal);
        Assert.Contains("url.GetString() == FixedPage", source, StringComparison.Ordinal);
        Assert.Contains("socket.IsLoopback && socket.Port == port", source, StringComparison.Ordinal);
        Assert.Contains("pollCts.CancelAfter(PollLimit)", source, StringComparison.Ordinal);
        Assert.Contains("var initialStateObserved = false;", source, StringComparison.Ordinal);
        Assert.Contains("!initialStateObserved &&", source, StringComparison.Ordinal);
        Assert.Contains("clock.Elapsed < StartupLimit", source, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(100, pollCts.Token);", source, StringComparison.Ordinal);
        Assert.Contains("initialStateObserved = true;", source, StringComparison.Ordinal);
        Assert.Contains("BrowserCandidates.FirstOrDefault(candidate => File.Exists(candidate.Path))", source, StringComparison.Ordinal);
        Assert.Contains("BrowserProbeLifecycle.BrowserMissing", source, StringComparison.Ordinal);
        Assert.Contains("new(Path.Combine(AppContext.BaseDirectory, \"chrome-win64\", \"chrome.exe\"), \"PinnedArchive\")", source, StringComparison.Ordinal);
        Assert.Contains(@"Global\VPNRouterFixedBrowserProbe", source, StringComparison.Ordinal);
        Assert.Contains("new Semaphore(1, 1, @\"Global\\VPNRouterFixedBrowserProbe\")", source, StringComparison.Ordinal);
        Assert.Contains("var guardHeld = singleRun.WaitOne(0);", source, StringComparison.Ordinal);
        Assert.Contains("if (guardHeld) singleRun.Release();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Mutex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseMutex", source, StringComparison.Ordinal);
        Assert.Contains("Stopwatch.StartNew()", source, StringComparison.Ordinal);
        Assert.Contains("edge.BeginOutputReadLine()", source, StringComparison.Ordinal);
        Assert.Contains("edge.BeginErrorReadLine()", source, StringComparison.Ordinal);
        Assert.Contains("edge.Kill(entireProcessTree: true)", source, StringComparison.Ordinal);
        Assert.Contains("if (!edge.WaitForExit(5000)) cleanupFailed = true", source, StringComparison.Ordinal);
        Assert.True(source.IndexOf("ValidateProfilePath(profile);", StringComparison.Ordinal) <
                    source.LastIndexOf("Directory.Delete(profile, recursive: true)", StringComparison.Ordinal));
        Assert.DoesNotContain("Console.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.Error", program, StringComparison.Ordinal);
        Assert.Contains("result.Lifecycle == BrowserProbeLifecycle.Completed ? 0 : 1", program, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildScript_IsFixedSourceBuildAndBoundsRecursiveCleanup()
    {
        var source = ReadRepoFile("tools", "build-winbrat-browser-probe-payload.ps1");

        Assert.Contains("param()", source, StringComparison.Ordinal);
        Assert.Contains("VPNRouter.Tools\\WinbratBrowserProbe\\VPNRouter.Tools.WinbratBrowserProbe.csproj", source, StringComparison.Ordinal);
        Assert.Contains("-r win-x64 --self-contained", source, StringComparison.Ordinal);
        Assert.Contains("-p:PublishSingleFile=true", source, StringComparison.Ordinal);
        Assert.Contains("$Output.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)", source, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $Output -Recurse -Force", source, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -Algorithm SHA256", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Command", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("New-PSSession", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowserBurstCoordinator_IsFixedHashGatedAndProvesTheSpawnedEdgeWorkload()
    {
        var source = ReadRepoFile("tools", "brat-verify.ps1");
        var browser = source[source.IndexOf("function Invoke-BrowserBurstLoad", StringComparison.Ordinal)..
                             source.IndexOf("switch ($Action)", StringComparison.Ordinal)];

        Assert.Contains("$ApprovedWinbratBrowserProbePayloadSha256", source, StringComparison.Ordinal);
        Assert.Contains("$ChromeForTestingArchive", source, StringComparison.Ordinal);
        Assert.Contains("$ApprovedChromeForTestingSha256", source, StringComparison.Ordinal);
        Assert.Contains("$ChromeForTestingEntryCount = 308", source, StringComparison.Ordinal);
        Assert.Contains("$ChromeForTestingExe = 'chrome-win64/chrome.exe'", source, StringComparison.Ordinal);
        Assert.Contains("function Test-ApprovedChromeForTestingArchive", source, StringComparison.Ordinal);
        Assert.Contains("Test-ApprovedWinbratBrowserProbePayload", source, StringComparison.Ordinal);
        Assert.Contains("WinbratBrowserProbe-win-x64.zip", browser, StringComparison.Ordinal);
        Assert.Contains("$BrowserCandidates = @(", source, StringComparison.Ordinal);
        var edgeX86 = source.IndexOf("C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe", StringComparison.Ordinal);
        var edgeX64 = source.IndexOf("C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe", StringComparison.Ordinal);
        var chromeX64 = source.IndexOf("C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", StringComparison.Ordinal);
        var chromeX86 = source.IndexOf("C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe", StringComparison.Ordinal);
        Assert.True(edgeX86 >= 0 && edgeX86 < edgeX64 && edgeX64 < chromeX64 && chromeX64 < chromeX86);
        Assert.Contains("Get-AuthenticodeSignature -FilePath $candidate.Path", browser, StringComparison.Ordinal);
        Assert.Contains("$signature.Status -eq 'Valid'", browser, StringComparison.Ordinal);
        Assert.Contains("$candidate.Vendor", browser, StringComparison.Ordinal);
        Assert.Contains("if ($portableApproved)", browser, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath $ChromeForTestingArchive -Destination $remoteChromeArchive -ToSession $s -Force", browser, StringComparison.Ordinal);
        Assert.Contains("$browserPath = Join-Path $dir ($portableExe -replace '/', '\\')", browser, StringComparison.Ordinal);
        Assert.Contains("$preflight.RouteScope -ne 'Tunnel'", browser, StringComparison.Ordinal);
        Assert.Contains("$preflight.CoreCount -ne 1", browser, StringComparison.Ordinal);
        Assert.Contains("-not $preflight.TunUp", browser, StringComparison.Ordinal);
        Assert.Contains("$edgeTree", browser, StringComparison.Ordinal);
        Assert.Contains("$tunCorrelation", browser, StringComparison.Ordinal);
        Assert.Contains("Get-NetAdapterStatistics -Name 'VPNRouter-TUN'", browser, StringComparison.Ordinal);
        Assert.Contains("([string]$_.ExecutablePath) -ieq $browserPath", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-NetTCPConnection", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("BrowserSocketNotProven", browser, StringComparison.Ordinal);
        Assert.Contains("Start-Process -FilePath $probe -WorkingDirectory $dir", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process -FilePath $probe -ArgumentList", browser, StringComparison.Ordinal);
        Assert.Contains("if (-not $edgeTree)", browser, StringComparison.Ordinal);
        Assert.Contains("if (-not $tunCorrelation)", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("$process.ExitCode", browser, StringComparison.Ordinal);
        Assert.Contains("BrowserProbeDevToolsUnavailable", browser, StringComparison.Ordinal);
        Assert.Contains("BrowserProbeLifecycleUnrecognized", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Content -LiteralPath $stderr", browser, StringComparison.Ordinal);
        Assert.Contains("if ($probeFailure -ne 'Completed')", browser, StringComparison.Ordinal);
        Assert.Contains("if (-not [bool]$metrics.Done)", browser, StringComparison.Ordinal);
        Assert.Contains("$result.FetchOk -ge 3200", browser, StringComparison.Ordinal);
        Assert.Contains("$result.WsOk -ge 2000", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("$metrics.FetchOk -lt 3200", browser, StringComparison.Ordinal);
        Assert.Contains("'BrowserMissing'", browser, StringComparison.Ordinal);
        Assert.Contains("'BrowserSignatureUnverified'", browser, StringComparison.Ordinal);
        Assert.Contains("'TunnelStateUnavailable'", browser, StringComparison.Ordinal);
        Assert.Contains("'BrowserProcessNotProven'", browser, StringComparison.Ordinal);
        Assert.Contains("'TunCorrelationNotProven'", browser, StringComparison.Ordinal);
        Assert.Contains("'BrowserProbePagePollingFailure'", browser, StringComparison.Ordinal);
        Assert.True(browser.IndexOf("if (-not $preflight.BrowserExists)", StringComparison.Ordinal) <
                    browser.IndexOf("elseif (-not $preflight.Ready", StringComparison.Ordinal));
        Assert.Contains("$result.FetchFail -eq 0", browser, StringComparison.Ordinal);
        Assert.Contains("$result.WsFail -eq 0", browser, StringComparison.Ordinal);
        Assert.Contains("$result.MaxFetchNoProgressMs -le 15000", browser, StringComparison.Ordinal);
        Assert.Contains("$result.MaxWsNoProgressMs -le 5000", browser, StringComparison.Ordinal);
        Assert.Contains("Find-NetRoute -RemoteIPAddress $endpointAddress", browser, StringComparison.Ordinal);
        Assert.Contains("$result.RouteStable", browser, StringComparison.Ordinal);
        Assert.True(browser.IndexOf("$metrics.FetchOk -lt 3200", StringComparison.Ordinal) <
                    browser.LastIndexOf("Find-NetRoute -RemoteIPAddress $endpointAddress", StringComparison.Ordinal));
        Assert.Contains("'PayloadExitNonZero'", browser, StringComparison.Ordinal);
        Assert.Contains("if ($process -and -not $process.HasExited)", browser, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $dir -Recurse -Force", browser, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "Invoke-WebRequest", "Start-BitsTransfer", "winget", "choco", "msiexec", "curl" })
            Assert.DoesNotContain(forbidden, browser, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowserBurstCoordinator_UsesSafeAggregateEvidenceAndMixedFailsClosed()
    {
        var source = ReadRepoFile("tools", "brat-verify.ps1");
        var browser = source[source.IndexOf("function Invoke-BrowserBurstLoad", StringComparison.Ordinal)..
                             source.IndexOf("switch ($Action)", StringComparison.Ordinal)];
        var loadtest = source[source.IndexOf("'loadtest' {", StringComparison.Ordinal)..];
        var coordinator = ReadRepoFile("tools", "brat-loadtest.ps1");

        Assert.Contains("foreach ($name in @('FetchOk','FetchFail','WsOk','WsFail','Done','MaxFetchNoProgressMs','MaxWsNoProgressMs'))", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("Metrics = $result", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("Metrics = $preflight", browser, StringComparison.Ordinal);
        Assert.Contains("Profile = 'Mixed'", loadtest, StringComparison.Ordinal);
        Assert.Contains("Caps = 'GameUdp+BrowserBurst'; Metrics = [ordered]@{}; Lifecycle = 'MeasurementGated'", loadtest, StringComparison.Ordinal);
        Assert.Contains("'BrowserMissing'", coordinator, StringComparison.Ordinal);
        Assert.Contains("'BrowserProbePagePollingFailure'", coordinator, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VPNRouter.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var path = Path.Combine(new[] { directory!.FullName }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path));
        return File.ReadAllText(path);
    }
}
