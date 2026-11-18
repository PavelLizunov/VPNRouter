#nullable enable

using System.Net;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

internal sealed class ZapretTestHandler(Func<HttpRequestMessage, HttpStatusCode> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(respond(request)));
    }
}

public sealed class ZapretFlowsealProbeTests : IDisposable
{
    private readonly string _previousRoot = AppPaths.DataDir;
    private readonly string _root = Directory.CreateTempSubdirectory("vpnrouter-probe-").FullName;
    private readonly Func<bool> _previousAnyWinws = ZapretAutoStrategy.AnyWinwsRunning;
    private readonly Func<HttpMessageHandler> _previousHandler = ZapretAutoStrategy.CreateHandler;
    private readonly List<FakeProcessHandle> _children = new();
    private readonly FakeProcessRunner _runner = new();
    private readonly ZapretManager _manager;

    public ZapretFlowsealProbeTests()
    {
        AppPaths.OverrideDataDir(_root);
        ZapretAutoStrategy.AnyWinwsRunning = () => false;
        ZapretAutoStrategy.CreateHandler = () => new ZapretTestHandler(_ => HttpStatusCode.ServiceUnavailable);
        _runner.OnStart(_ => true, _ =>
        {
            var child = new FakeProcessHandle(1000 + _children.Count);
            _children.Add(child);
            return child;
        });
        _manager = new ZapretManager(runner: _runner);
    }

    public void Dispose()
    {
        _manager.Dispose();
        ZapretAutoStrategy.CreateHandler = _previousHandler;
        ZapretAutoStrategy.AnyWinwsRunning = _previousAnyWinws;
        AppPaths.OverrideDataDir(_previousRoot);
        Directory.Delete(_root, recursive: true);
    }

    private void Install(params string[] names)
    {
        Directory.CreateDirectory(ZapretUpdater.BinDir);
        File.WriteAllText(ZapretUpdater.WinwsExePath, "fixture for the fake runner");
        foreach (var name in names)
            File.WriteAllText(Path.Combine(ZapretUpdater.ZapretDir, name + ".bat"),
                """start "" "%BIN%winws.exe" --wf-tcp=443 --dpi-desync=multisplit""");
    }

    private Task<ZapretAutoStrategy.StrategySweepResult> Sweep(CancellationToken ct = default) =>
        ZapretAutoStrategy.ProbeInstalledStrategiesAsync(_manager, null, null, TimeSpan.Zero, ct);

    [Fact]
    public async Task Sweep_UsesFreshConnectionsForEachInstalledCandidate_AndClosesEveryChild()
    {
        Install("general", "general (ALT4)", "general (ALT5)");
        int clients = 0;
        ZapretAutoStrategy.CreateHandler = () =>
        {
            int attempt = ++clients;
            return new ZapretTestHandler(_ => attempt == 2 ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
        };

        var result = await Sweep();

        Assert.Equal("general (ALT4)", result.Winner);
        Assert.Equal((2, 3), (result.TestedCount, result.TotalCount));
        Assert.True(result.EarlyWinner);
        Assert.Equal(0, result.PerStrategyResults!["general"].Passed);
        Assert.Equal(8, result.PerStrategyResults[result.Winner!].Passed);
        Assert.Equal(2, clients);
        Assert.Equal(2, _children.Count);
        Assert.All(_children, child => Assert.Equal((1, 1), (child.KillCallCount, child.DisposeCallCount)));
        Assert.All(_runner.StartCalls, call =>
        {
            Assert.Equal(ZapretUpdater.WinwsExePath, call.ExecutablePath);
            Assert.Equal(new[] { "--wf-tcp=443", "--dpi-desync=multisplit" }, call.Arguments);
        });
        Assert.Contains("general (ALT4): 8/8", File.ReadAllText(result.ProbeLogPath!));
    }

    [Fact]
    public async Task CanceledDuringChecks_ClosesCandidateAndDoesNotSelectAWinner()
    {
        Install("general", "general (ALT)");
        using var cancel = new CancellationTokenSource();
        ZapretAutoStrategy.CreateHandler = () => new ZapretTestHandler(_ =>
        {
            cancel.Cancel();
            return HttpStatusCode.OK;
        });

        var result = await Sweep(cancel.Token);

        Assert.Null(result.Winner);
        Assert.Equal("canceled", result.Diagnostic);
        Assert.Equal(1, Assert.Single(_children).KillCallCount);
    }

    [Fact]
    public async Task CandidateExitingDuringChecks_CannotClaimSuccessfulResponses()
    {
        Install("general");
        ZapretAutoStrategy.CreateHandler = () => new ZapretTestHandler(_ =>
        {
            _children[0].SignalExit(1);
            return HttpStatusCode.OK;
        });

        var result = await Sweep();

        Assert.Null(result.Winner);
        Assert.Equal(0, result.PerStrategyResults!["general"].Passed);
        Assert.Equal(1, Assert.Single(_children).DisposeCallCount);
    }

    [Fact]
    public async Task RunningHelperAfterCleanup_AbortsBeforeStartingAnotherCandidate()
    {
        Install("general", "general (ALT)");
        ZapretAutoStrategy.AnyWinwsRunning = () => _children.Count > 0;
        var result = await Sweep();

        Assert.Null(result.Winner);
        Assert.Equal("helper_active", result.Diagnostic);
        Assert.Single(_runner.StartCalls);
    }

    [Fact]
    public async Task MissingPackage_RequiresRepairAndNeverStartsAProcess()
    {
        var result = await Sweep();

        Assert.Equal("missing_package", result.Diagnostic);
        Assert.Null(result.Winner);
        Assert.Empty(_runner.StartCalls);
    }

    [Fact]
    public async Task RunTests_UsesOnlyBuiltInHttpsTargets_AndNeedsNoInstalledRuntime()
    {
        var requests = new List<(HttpMethod Method, Uri Uri)>();
        ZapretAutoStrategy.CreateHandler = () => new ZapretTestHandler(req =>
        {
            requests.Add((req.Method, req.RequestUri!));
            return req.RequestUri!.Host == "discord.com" ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
        });

        var lines = new List<string>();
        await foreach (var line in ZapretActions.RunTestsAsync(TestContext.Current.CancellationToken)) lines.Add(line);

        Assert.Equal(ZapretAutoStrategy.DefaultProbeTargets.Count, lines.Count);
        Assert.Equal(ZapretAutoStrategy.DefaultProbeTargets, requests.Select(r => r.Uri.GetLeftPart(UriPartial.Authority)));
        Assert.All(requests, r => Assert.Equal((HttpMethod.Head, "https"), (r.Method, r.Uri.Scheme)));
        Assert.Equal($"discord.com — {VPNRouter.Core.Localization.Strings.ZapretHttpsResult(true)}", lines[0]);
        Assert.All(lines.Skip(1), line => Assert.EndsWith(VPNRouter.Core.Localization.Strings.ZapretHttpsResult(false), line));
        Assert.Empty(_runner.StartCalls);
    }

    [Fact]
    public void HttpsProbesUseDirectTransportWithoutRedirectsOrCookies()
    {
        using var handler = ZapretAutoStrategy.CreateDirectHandler();
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.False(handler.AllowAutoRedirect);
    }
}
