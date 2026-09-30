#nullable enable

#if PLATFORM_WINDOWS
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Characterization tests for <c>MainWindowViewModel.ProbeAndStartZapretAsync</c>, the orchestrator behind the
/// "Enable bypass" button: pre-probe ipset cleanup, the cached-winner shortcut, the Flowseal sweep, starting the winner,
/// reading back whether winws is alive, the probe cache and the saved settings. They pin what the view model does with
/// each kind of sweep result before the method is split. The sweep itself is replaced through
/// <c>MainWindowViewModel.FlowsealProbe</c>; every process request goes to a fake runner, the warm-start target probe
/// talks to a loopback listener, and all files live under a temporary data directory, so nothing starts winws, touches
/// WinDivert or reaches the internet.
/// </summary>
public sealed class MainWindowViewModelZapretProbeTests : IDisposable
{
    private const int FakeWinwsPid = 4242;
    private const string StrategyArgs = "--wf-tcp=443 --tag=\"{0}\"";

    private readonly string _originalDataDir;
    private readonly string _tempDataDir;
    private readonly string _originalLang;
    private readonly Func<string, IProgress<ZapretAutoStrategy.FlowsealProgress>?, Serilog.ILogger?, CancellationToken,
        Task<ZapretAutoStrategy.FlowsealSweepResult>> _originalProbe;

    private readonly FakeProcessRunner _runner = new();
    private readonly FakeProcessHandle _handle = new(FakeWinwsPid);
    private readonly InMemorySettingsStore _store = new();
    private readonly List<LoopbackHttp> _servers = new();
    private readonly List<MainWindowViewModel> _models = new();

    private Func<string, IProgress<ZapretAutoStrategy.FlowsealProgress>?, CancellationToken,
        Task<ZapretAutoStrategy.FlowsealSweepResult>>? _probeImpl;

    private int _probeCalls;

    public MainWindowViewModelZapretProbeTests()
    {
        _originalDataDir = AppPaths.DataDir;
        _originalLang = VPNRouter.App.Localization.Strings.Lang;
        _originalProbe = MainWindowViewModel.FlowsealProbe;
        _tempDataDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-vm-zapret-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDataDir);
        AppPaths.OverrideDataDir(_tempDataDir);

        _runner.OnStart(_ => true, _ => _handle);
        MainWindowViewModel.FlowsealProbe = (_, progress, _, ct) =>
        {
            Interlocked.Increment(ref _probeCalls);
            if (_probeImpl is null)
                throw new InvalidOperationException("the sweep was not expected to run in this test");
            return _probeImpl(ZapretUpdater.ZapretDir, progress, ct);
        };
    }

    public void Dispose()
    {
        MainWindowViewModel.FlowsealProbe = _originalProbe;
        VPNRouter.App.Localization.Strings.Lang = _originalLang;
        foreach (var vm in _models)
        {
            try { vm.Dispose(); }
            catch { }
        }
        foreach (var server in _servers) server.Dispose();
        AppPaths.OverrideDataDir(_originalDataDir);
        try { Directory.Delete(_tempDataDir, recursive: true); }
        catch { }
    }

    // ---- fixture helpers --------------------------------------------------------------------------

    private static string ZapretPath(params string[] parts) =>
        Path.Combine(new[] { ZapretUpdater.ZapretDir }.Concat(parts).ToArray());

    private static void WriteZapretFile(string content, params string[] parts)
    {
        var path = ZapretPath(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteStrategy(string name) =>
        WriteZapretFile(
            $"start \"\" \"%BIN%winws.exe\" {string.Format(StrategyArgs, name)}", name + ".bat");

    private MainWindowViewModel NewVm(params string[] strategyNames)
    {
        Assert.SkipWhen(ZapretManager.IsWinwsRunning(), "a real winws is running, its pid would change the outcome");

        WriteZapretFile("stub winws for tests", "bin", "winws.exe");
        foreach (var name in strategyNames) WriteStrategy(name);

        var vm = new MainWindowViewModel(_store);
        _models.Add(vm);
        vm.IsRussian = false;
        VPNRouter.App.Localization.Strings.Lang = "en";
        SetField(vm, "_zapret", new ZapretManager(logger: null, runner: _runner));
        return vm;
    }

    private static Task Run(MainWindowViewModel vm)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "ProbeAndStartZapretAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Task)method!.Invoke(vm, null)!;
    }

    private static void SetField(MainWindowViewModel vm, string name, object? value)
    {
        var field = typeof(MainWindowViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(vm, value);
    }

    private static T? GetField<T>(MainWindowViewModel vm, string name)
    {
        var field = typeof(MainWindowViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T?)field!.GetValue(vm);
    }

    private static ZapretAutoStrategy.FlowsealSweepResult Sweep(
        string? winner,
        string? diagnostic = null,
        IReadOnlyDictionary<string, ZapretStrategyTestResult>? perStrategy = null,
        string? probeLogPath = null,
        bool earlyWinner = false,
        IReadOnlyList<string>? errorLines = null) =>
        new(winner, 1, 1, string.Empty, diagnostic, errorLines, perStrategy, probeLogPath, earlyWinner);

    private void SweepReturns(ZapretAutoStrategy.FlowsealSweepResult result) =>
        _probeImpl = (_, _, _) => Task.FromResult(result);

    private static void SeedCache(
        string strategy, int successRunCount = 3, int failureCount = 0, int daysSinceSweep = 0,
        int passed = 4, int total = 5)
    {
        Directory.CreateDirectory(AppPaths.CacheDir);
        var entry = new ZapretProbeCacheEntry
        {
            Strategy = strategy,
            LastSuccessAt = DateTime.UtcNow.AddDays(-daysSinceSweep),
            LastSweepAt = DateTime.UtcNow.AddDays(-daysSinceSweep),
            SuccessRunCount = successRunCount,
            LastFailureCount = failureCount,
            TargetsPassed = passed,
            TargetsTotal = total,
        };
        File.WriteAllText(Path.Combine(AppPaths.CacheDir, "zapret_probe.json"), JsonSerializer.Serialize(entry));
    }

    private LoopbackHttp Loopback(int status)
    {
        var server = new LoopbackHttp(status);
        _servers.Add(server);
        return server;
    }

    private static void WriteTargets(LoopbackHttp server) =>
        WriteZapretFile(
            $"Alpha = \"{server.Url}a\"\r\nBeta = \"{server.Url}b\"\r\n", "utils", "targets.txt");

    private static void UseRussian(MainWindowViewModel vm)
    {
        vm.IsRussian = true;
        VPNRouter.App.Localization.Strings.Lang = "ru";
    }

    private static string WrapperPath() => ZapretPath("_vpnrouter_silent.bat");

    /// <summary>A minimal HTTP server: answers every request with a fixed status and an empty body.</summary>
    private sealed class LoopbackHttp : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public LoopbackHttp(int status)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _ = Task.Run(() => AcceptLoopAsync(status));
        }

        public string Url { get; }

        private async Task AcceptLoopAsync(int status)
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => HandleAsync(client, status));
                }
            }
            catch { }
        }

        private static async Task HandleAsync(TcpClient client, int status)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[2048];
                    var seen = new StringBuilder();
                    while (!seen.ToString().Contains("\r\n\r\n"))
                    {
                        var n = await stream.ReadAsync(buffer, CancellationToken.None);
                        if (n == 0) return;
                        seen.Append(Encoding.ASCII.GetString(buffer, 0, n));
                    }
                    var response = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status} X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, CancellationToken.None);
                }
                catch { }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    // ---- seam ---------------------------------------------------------------------------------------

    [Fact]
    public void FlowsealProbe_DefaultsToTheRealSweep()
    {
        Func<string, IProgress<ZapretAutoStrategy.FlowsealProgress>?, Serilog.ILogger?, CancellationToken,
            Task<ZapretAutoStrategy.FlowsealSweepResult>> real = ZapretAutoStrategy.RunFlowsealProbeAsync;

        Assert.Equal(real, _originalProbe);
    }

    [AvaloniaFact]
    public async Task Probe_ThroughTheDefaultWiring_RunsTheRealSweepAndStartsItsWinner()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows() && ZapretAutoStrategy.IsRunningAsAdmin(),
            "the real Flowseal sweep needs Windows and an elevated process");
        var vm = NewVm("general", "general (ALT)");
        MainWindowViewModel.FlowsealProbe = _originalProbe;
        WriteZapretFile(
            "Write-Output '[1/2] general.bat'\r\n" +
            "Write-Output '  [Discord][HTTP] https://discord.com status=OK'\r\n" +
            "Write-Output '[2/2] general (ALT).bat'\r\n" +
            "Write-Output '  [Discord][HTTP] https://discord.com status=OK'\r\n" +
            "Write-Output 'Best config: general (ALT).bat'\r\n",
            "utils", "test zapret.ps1");

        await Run(vm);

        Assert.True(vm.ZapretEnabled);
        Assert.Equal("general (ALT)", vm.ZapretWinningStrategy);
        Assert.Equal($"Running [general (ALT)] (PID {FakeWinwsPid})", vm.ZapretStatus);
    }

    // ---- state while the sweep runs -----------------------------------------------------------------

    [AvaloniaFact]
    public async Task WhileTheSweepRuns_TheProbeFlagsAreSet_AndTheyAreResetAfterwards()
    {
        var vm = NewVm("general");
        vm.ZapretWinningStrategy = "stale";
        vm.IsZapretFallback = true;
        (bool Probing, bool Fallback, string Winning, bool SuppressToast, bool Timer, bool Cts) during = default;
        _probeImpl = (_, _, _) =>
        {
            during = (
                vm.IsZapretProbing,
                vm.IsZapretFallback,
                vm.ZapretWinningStrategy,
                GetField<bool>(vm, "_suppressZapretAvToast"),
                GetField<object>(vm, "_zapretProbeElapsedTimer") is not null,
                GetField<CancellationTokenSource>(vm, "_zapretProbeCts") is not null);
            return Task.FromResult(Sweep(null));
        };

        await Run(vm);

        Assert.Equal((true, false, string.Empty, true, true, true), during);
        Assert.False(vm.IsZapretProbing);
        Assert.False(GetField<bool>(vm, "_suppressZapretAvToast"));
        Assert.Null(GetField<object>(vm, "_zapretProbeElapsedTimer"));
        Assert.Null(GetField<CancellationTokenSource>(vm, "_zapretProbeCts"));
        Assert.Equal((0, 0, string.Empty), (vm.ZapretProbeIndex, vm.ZapretProbeTotal, vm.ZapretProbeStrategy));
    }

    [AvaloniaFact]
    public async Task Progress_NamedStrategyReports_SetIndexTotalAndNameAndZeroTheScore_ScoreReportsSetTheScore()
    {
        var vm = NewVm("general");
        var seen = new List<(int Index, int Total, string Name, int Pass, int Checks)>();
        _probeImpl = async (_, progress, _) =>
        {
            void Snap() => seen.Add((vm.ZapretProbeIndex, vm.ZapretProbeTotal, vm.ZapretProbeStrategy,
                vm.ZapretProbePassCount, vm.ZapretProbeTotalCount));

            progress!.Report(new ZapretAutoStrategy.FlowsealProgress(2, 3, "general (ALT)", 0, 0));
            await Task.Yield();
            Snap();
            progress.Report(new ZapretAutoStrategy.FlowsealProgress(2, 3, "", 5, 6));
            await Task.Yield();
            Snap();
            progress.Report(new ZapretAutoStrategy.FlowsealProgress(3, 3, "general (ALT2)", 0, 0));
            await Task.Yield();
            Snap();
            return Sweep(null);
        };

        await Run(vm);

        Assert.Equal(
            new[]
            {
                (1, 3, "general (ALT)", 0, 0),
                (1, 3, "general (ALT)", 5, 6),
                (2, 3, "general (ALT2)", 0, 0),
            },
            seen);
    }

    // ---- a winner --------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Winner_FromABat_IsStartedThroughTheSilentWrapper_AndRecordedEverywhere()
    {
        var vm = NewVm("general (ALT3)", "general");
        var savesBefore = _store.SaveCount;
        var perStrategy = new Dictionary<string, ZapretStrategyTestResult>
        {
            ["general"] = new() { Passed = 7, Total = 9 },
            ["general (ALT3)"] = new() { Passed = 2, Total = 9 },
        };
        _probeImpl = async (_, progress, _) =>
        {
            progress!.Report(new ZapretAutoStrategy.FlowsealProgress(1, 2, "general", 0, 0));
            progress.Report(new ZapretAutoStrategy.FlowsealProgress(1, 2, "", 7, 9));
            await Task.Yield();
            return Sweep("general", perStrategy: perStrategy, probeLogPath: @"C:\logs\probe.log");
        };

        await Run(vm);

        var start = Assert.Single(_runner.StartCalls);
        Assert.Equal("cmd.exe", start.ExecutablePath);
        Assert.Equal(new[] { "/c", WrapperPath() }, start.Arguments);
        Assert.Equal(ZapretUpdater.ZapretDir, start.WorkingDirectory);
        Assert.Contains(
            "\"%BIN%winws.exe\" " + string.Format(StrategyArgs, "general"),
            File.ReadAllText(WrapperPath()));

        Assert.True(vm.ZapretEnabled);
        Assert.False(vm.IsZapretFallback);
        Assert.Equal("general", vm.ZapretWinningStrategy);
        Assert.Equal($"Running [general] (PID {FakeWinwsPid})", vm.ZapretStatus);
        Assert.Equal(@"C:\logs\probe.log", vm.LastProbeLogPath);
        Assert.Equal(vm.ZapretStrategies.IndexOf("general"), vm.ZapretStrategyIndex);
        Assert.Equal(1, vm.ZapretStrategies.IndexOf("general"));
        Assert.Equal((7, 9), (vm.ZapretProbePassCount, vm.ZapretProbeTotalCount));

        var cache = ZapretProbeCache.TryLoad();
        Assert.NotNull(cache);
        Assert.Equal("general", cache!.Strategy);
        Assert.Equal(1, cache.SuccessRunCount);
        Assert.Equal((7, 9), (cache.TargetsPassed, cache.TargetsTotal));
        Assert.Equal((7, 9), (cache.PerStrategyResults["general"].Passed, cache.PerStrategyResults["general"].Total));
        Assert.Equal(2, cache.PerStrategyResults.Count);

        Assert.True(_store.SaveCount > savesBefore);
        Assert.True(_store.LastSave!.Value.Settings.App.ZapretEnabled);
        Assert.Equal("general", _store.LastSave!.Value.Settings.App.ZapretStrategy);
    }

    [AvaloniaFact]
    public async Task Winner_NameIsMatchedIgnoringBatSuffixCaseAndSpaces_ButReportedAsGiven()
    {
        var vm = NewVm("general (ALT)");
        SweepReturns(Sweep("  GENERAL (alt).BAT "));

        await Run(vm);

        Assert.Single(_runner.StartCalls);
        Assert.True(vm.ZapretEnabled);
        Assert.Equal("  GENERAL (alt).BAT ", vm.ZapretWinningStrategy);
        Assert.Equal($"Running [  GENERAL (alt).BAT ] (PID {FakeWinwsPid})", vm.ZapretStatus);
        Assert.Equal(-1, vm.ZapretStrategies.IndexOf("  GENERAL (alt).BAT "));
    }

    [AvaloniaFact]
    public async Task Winner_WithoutABat_IsStartedThroughTheLaunchBat()
    {
        var vm = NewVm("general");
        SetField(vm, "_parsedStrategies", new List<ZapretStrategy>
        {
            new("general", "--wf-tcp=443 --no-bat", BatPath: null),
        });
        SweepReturns(Sweep("general"));

        await Run(vm);

        var start = Assert.Single(_runner.StartCalls);
        Assert.Equal("cmd.exe", start.ExecutablePath);
        Assert.Equal(new[] { "/c", Path.Combine(ZapretUpdater.BinDir, "_vpnrouter_launch.bat") }, start.Arguments);
        Assert.Null(start.WorkingDirectory);
        Assert.Contains("winws.exe\" --wf-tcp=443 --no-bat", File.ReadAllText(Path.Combine(ZapretUpdater.BinDir, "_vpnrouter_launch.bat")));
        Assert.True(vm.ZapretEnabled);
        Assert.Equal($"Running [general] (PID {FakeWinwsPid})", vm.ZapretStatus);
    }

    [AvaloniaFact]
    public async Task Winner_WithoutABat_AndNoWinwsExecutable_ReportsTheStartError()
    {
        var vm = NewVm("general");
        SetField(vm, "_parsedStrategies", new List<ZapretStrategy> { new("general", "--x", BatPath: null) });
        File.Delete(ZapretUpdater.WinwsExePath);
        SweepReturns(Sweep("general"));

        await Run(vm);

        Assert.Empty(_runner.StartCalls);
        Assert.False(vm.ZapretEnabled);
        Assert.True(vm.IsZapretFallback);
        Assert.Equal("Error starting general: winws.exe not found. Download zapret first.", vm.ZapretStatus);
        Assert.Null(ZapretProbeCache.TryLoad());
        Assert.False(vm.IsZapretProbing);
    }

    [AvaloniaFact]
    public async Task Winner_WhoseLaunchThrows_ReportsTheStartError()
    {
        var vm = NewVm("general");
        var throwing = new FakeProcessRunner();
        throwing.OnStart(_ => true, _ => throw new InvalidOperationException("boom"));
        SetField(vm, "_zapret", new ZapretManager(logger: null, runner: throwing));
        SweepReturns(Sweep("general"));

        await Run(vm);

        Assert.False(vm.ZapretEnabled);
        Assert.True(vm.IsZapretFallback);
        Assert.Equal("Error starting general: boom", vm.ZapretStatus);
        Assert.Equal(string.Empty, vm.ZapretWinningStrategy);
        Assert.Null(ZapretProbeCache.TryLoad());
    }

    [AvaloniaFact]
    public async Task Winner_ThatExitsImmediately_ReportsItDidNotStart_AndWritesNothing()
    {
        var vm = NewVm("general");
        var savesBefore = _store.SaveCount;
        _handle.SignalExit(1);
        SweepReturns(Sweep("general"));

        await Run(vm);

        Assert.Single(_runner.StartCalls);
        Assert.False(vm.ZapretEnabled);
        Assert.True(vm.IsZapretFallback);
        Assert.Equal("Strategy general failed to start", vm.ZapretStatus);
        Assert.Equal(string.Empty, vm.ZapretWinningStrategy);
        Assert.Null(ZapretProbeCache.TryLoad());
        Assert.Equal(savesBefore, _store.SaveCount);
    }

    [AvaloniaFact]
    public async Task Winner_NotInTheParsedList_FallsBackWithoutStartingAnything()
    {
        var vm = NewVm("general");
        SweepReturns(Sweep("general (ALT9)"));

        await Run(vm);

        Assert.Empty(_runner.StartCalls);
        Assert.False(vm.ZapretEnabled);
        Assert.True(vm.IsZapretFallback);
        Assert.Equal("Winner general (ALT9) not found in strategy list", vm.ZapretStatus);
        Assert.Null(ZapretProbeCache.TryLoad());
    }

    [AvaloniaFact]
    public async Task Russian_StatusTexts_ForARunningWinnerAndAFailedStart()
    {
        var vm = NewVm("general");
        UseRussian(vm);
        SweepReturns(Sweep("general"));
        await Run(vm);
        Assert.Equal($"Работает [general] (PID {FakeWinwsPid})", vm.ZapretStatus);

        _handle.SignalExit(1);
        await Run(vm);
        Assert.Equal("Стратегия general не запустилась", vm.ZapretStatus);
    }

    // ---- no winner -----------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData("not_admin",
        "Administrator rights required to probe strategies. Restart VPNRouter as admin.",
        "Нужны права администратора для подбора стратегии. Перезапустите VPNRouter от админа.")]
    [InlineData("sweep_timeout",
        "Strategy probe exceeded 10 min cap. Check network and retry.",
        "Подбор стратегии превысил 10 минут. Проверьте интернет и попробуйте ещё раз.")]
    [InlineData("missing_script",
        "Flowseal script missing. Update Zapret via Advanced settings.",
        "Скрипт Flowseal не найден. Обнови Zapret через «Тонкую настройку».")]
    [InlineData("canceled", "Probe canceled.", "Подбор отменён.")]
    public async Task NoWinner_DiagnosticSelectsTheStatusText(string diagnostic, string english, string russian)
    {
        var vm = NewVm("general");
        SweepReturns(Sweep(null, diagnostic));

        await Run(vm);

        Assert.Equal(english, vm.ZapretStatus);
        Assert.True(vm.IsZapretFallback);
        Assert.False(vm.ZapretEnabled);
        Assert.Empty(_runner.StartCalls);

        UseRussian(vm);
        await Run(vm);
        Assert.Equal(russian, vm.ZapretStatus);
    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("something_else")]
    public async Task NoWinner_WithoutAKnownDiagnostic_UsesTheGenericFailureText(string? diagnostic)
    {
        var vm = NewVm("general");
        SweepReturns(Sweep(null, diagnostic, errorLines: new[] { "script said no" }));

        await Run(vm);

        Assert.Equal(Strings.ZapretOneTapAllFailedToast, vm.ZapretStatus);
        Assert.True(vm.IsZapretFallback);
        Assert.False(vm.ZapretEnabled);
        Assert.Null(ZapretProbeCache.TryLoad());
    }

    [AvaloniaFact]
    public async Task NoWinner_KeepsTheProbeLogPathOfTheSweep()
    {
        var vm = NewVm("general");
        SweepReturns(Sweep(null, probeLogPath: @"C:\logs\zapret-probe-1.log"));

        await Run(vm);

        Assert.Equal(@"C:\logs\zapret-probe-1.log", vm.LastProbeLogPath);
    }

    [AvaloniaFact]
    public async Task CancelCommand_DuringTheSweep_CancelsItsToken_AndTheCanceledDiagnosticIsShown()
    {
        var vm = NewVm("general");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _probeImpl = async (_, _, ct) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return Sweep(null, "canceled");
        };

        var run = Run(vm);
        await started.Task;
        vm.CancelZapretProbeCommand.Execute(null);
        await run;

        Assert.Equal("Probe canceled.", vm.ZapretStatus);
        Assert.True(vm.IsZapretFallback);
        Assert.False(vm.IsZapretProbing);
        Assert.Null(GetField<CancellationTokenSource>(vm, "_zapretProbeCts"));
    }

    [AvaloniaFact]
    public async Task SweepThatThrows_IsReportedAsAnError_AndTheProbeFlagsAreStillReset()
    {
        var vm = NewVm("general");
        _probeImpl = (_, _, _) => throw new InvalidOperationException("script host exploded");

        await Run(vm);

        Assert.Equal("Error: script host exploded", vm.ZapretStatus);
        Assert.True(vm.IsZapretFallback);
        Assert.False(vm.ZapretEnabled);
        Assert.False(vm.IsZapretProbing);
        Assert.False(GetField<bool>(vm, "_suppressZapretAvToast"));
        Assert.Null(GetField<CancellationTokenSource>(vm, "_zapretProbeCts"));
    }

    // ---- the cache ------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task CacheHit_StartsTheCachedStrategy_ConfirmsItWithTheTargetProbe_AndSkipsTheSweep()
    {
        var vm = NewVm("general (ALT)");
        SeedCache("general (ALT)", successRunCount: 3, passed: 4, total: 5);
        WriteTargets(Loopback(200));

        await Run(vm);

        Assert.Equal(0, _probeCalls);
        Assert.Single(_runner.StartCalls);
        Assert.True(vm.ZapretEnabled);
        Assert.Equal("general (ALT)", vm.ZapretWinningStrategy);
        Assert.Equal($"Running [general (ALT)] (PID {FakeWinwsPid}, warm)", vm.ZapretStatus);
        Assert.Equal((2, 2), (vm.ZapretProbePassCount, vm.ZapretProbeTotalCount));
        Assert.Equal(vm.ZapretStrategies.IndexOf("general (ALT)"), vm.ZapretStrategyIndex);

        var cache = ZapretProbeCache.TryLoad()!;
        Assert.Equal("general (ALT)", cache.Strategy);
        Assert.Equal(4, cache.SuccessRunCount);
        Assert.Equal((4, 5), (cache.TargetsPassed, cache.TargetsTotal));
        Assert.Equal("general (ALT)", _store.LastSave!.Value.Settings.App.ZapretStrategy);
        Assert.False(vm.IsZapretProbing);
    }

    [AvaloniaFact]
    public async Task CacheHit_InRussian_ReportsTheWarmStart()
    {
        var vm = NewVm("general");
        UseRussian(vm);
        SeedCache("general");
        WriteTargets(Loopback(204));

        await Run(vm);

        Assert.Equal($"Работает [general] (PID {FakeWinwsPid}, warm)", vm.ZapretStatus);
    }

    [AvaloniaFact]
    public async Task CacheHit_WhoseTargetProbeFails_StopsTheProcess_RecordsAFailure_AndRunsTheSweep()
    {
        var vm = NewVm("general");
        SeedCache("general", successRunCount: 3, failureCount: 0, passed: 4, total: 5);
        WriteTargets(Loopback(503));
        SweepReturns(Sweep(null));

        await Run(vm);

        Assert.Equal(1, _probeCalls);
        Assert.Equal(1, _handle.KillCallCount);
        var cache = ZapretProbeCache.TryLoad()!;
        Assert.Equal("general", cache.Strategy);
        Assert.Equal(1, cache.LastFailureCount);
        Assert.Equal(3, cache.SuccessRunCount);
        Assert.False(vm.ZapretEnabled);
        Assert.True(vm.IsZapretFallback);
    }

    [AvaloniaFact]
    public async Task CacheHit_ForAStrategyThatIsNoLongerListed_RecordsAFailure_AndRunsTheSweep()
    {
        var vm = NewVm("general");
        SeedCache("general (REMOVED)");
        SweepReturns(Sweep(null));

        await Run(vm);

        Assert.Equal(1, _probeCalls);
        Assert.Empty(_runner.StartCalls);
        Assert.Equal(1, ZapretProbeCache.TryLoad()!.LastFailureCount);
    }

    [AvaloniaFact]
    public async Task CacheHit_ThatFailsAndThenASweepWinner_ReplacesTheEntry()
    {
        var vm = NewVm("general", "general (ALT)");
        SeedCache("general (REMOVED)", successRunCount: 5);
        SweepReturns(Sweep("general (ALT)"));

        await Run(vm);

        Assert.Equal(1, _probeCalls);
        var cache = ZapretProbeCache.TryLoad()!;
        Assert.Equal("general (ALT)", cache.Strategy);
        Assert.Equal(1, cache.SuccessRunCount);
        Assert.Equal(0, cache.LastFailureCount);
        Assert.True(vm.ZapretEnabled);
    }

    [AvaloniaTheory]
    [InlineData(8, 0, 3)]
    [InlineData(0, 3, 3)]
    [InlineData(0, 0, 0)]
    public async Task StaleOrUnreliableCacheEntry_IsIgnored_AndTheSweepRuns(int daysSinceSweep, int failures, int runs)
    {
        var vm = NewVm("general");
        SeedCache("general", successRunCount: runs, failureCount: failures, daysSinceSweep: daysSinceSweep);
        SweepReturns(Sweep(null));

        await Run(vm);

        Assert.Equal(1, _probeCalls);
        Assert.Empty(_runner.StartCalls);
        Assert.Equal(Strings.ZapretOneTapAllFailedToast, vm.ZapretStatus);
    }

    [AvaloniaFact]
    public async Task ForceFreshProbeFlag_IgnoresAReliableCache()
    {
        var vm = NewVm("general");
        SeedCache("general");
        SetField(vm, "_forceFreshProbe", true);
        SweepReturns(Sweep(null));

        await Run(vm);

        Assert.Equal(1, _probeCalls);
        Assert.Empty(_runner.StartCalls);
    }

    // ---- ipset cleanup and the manager ---------------------------------------------------------------------

    [AvaloniaFact]
    public async Task OrphanedIpsetFlag_IsRestoredBeforeTheSweepStarts()
    {
        var vm = NewVm("general");
        WriteZapretFile("203.0.113.113/32\r\n", "lists", "ipset-all.txt");
        WriteZapretFile("1.2.3.0/24\r\n", "lists", "ipset-all.test-backup.txt");
        WriteZapretFile("1", "ipset_switched.flag");
        string? liveDuringSweep = null;
        bool? flagDuringSweep = null;
        _probeImpl = (_, _, _) =>
        {
            liveDuringSweep = File.ReadAllText(ZapretPath("lists", "ipset-all.txt"));
            flagDuringSweep = File.Exists(ZapretPath("ipset_switched.flag"));
            return Task.FromResult(Sweep(null));
        };

        await Run(vm);

        Assert.Equal("1.2.3.0/24\r\n", liveDuringSweep);
        Assert.False(flagDuringSweep);
        Assert.False(File.Exists(ZapretPath("ipset_switched.flag")));
        Assert.False(File.Exists(ZapretPath("lists", "ipset-all.test-backup.txt")));
    }

    [AvaloniaFact]
    public async Task IpsetFlagLeftBehindBySweep_IsRestoredAfterwards_EvenWhenTheSweepThrows()
    {
        var vm = NewVm("general");
        WriteZapretFile("203.0.113.113/32\r\n", "lists", "ipset-all.txt");
        _probeImpl = (_, _, _) =>
        {
            WriteZapretFile("9.9.9.0/24\r\n", "lists", "ipset-all.test-backup.txt");
            WriteZapretFile("1", "ipset_switched.flag");
            throw new InvalidOperationException("interrupted");
        };

        await Run(vm);

        Assert.Equal("9.9.9.0/24\r\n", File.ReadAllText(ZapretPath("lists", "ipset-all.txt")));
        Assert.False(File.Exists(ZapretPath("ipset_switched.flag")));
    }

    [AvaloniaFact]
    public async Task ManagerIsCreatedOnTheFirstCall_AndReusedOnTheNext()
    {
        var vm = NewVm("general");
        SetField(vm, "_zapret", null);
        SweepReturns(Sweep(null));

        await Run(vm);
        var first = GetField<ZapretManager>(vm, "_zapret");
        await Run(vm);
        var second = GetField<ZapretManager>(vm, "_zapret");

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(2, _probeCalls);
    }
}
#endif
