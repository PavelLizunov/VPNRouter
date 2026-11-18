#nullable enable

#if PLATFORM_WINDOWS
using System.Net;
using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class MainWindowViewModelZapretProbeTests : IDisposable
{
    private const int FakeWinwsPid = 4242;
    private const string StrategyArgs = "--wf-tcp=443 --tag=\"{0}\"";

    private readonly string _originalDataDir;
    private readonly string _tempDataDir;
    private readonly string _originalLang;
    private readonly Func<ZapretManager, IProgress<ZapretAutoStrategy.StrategyProgress>?, Serilog.ILogger?, CancellationToken,
        Task<ZapretAutoStrategy.StrategySweepResult>> _originalProbe;

    private readonly FakeProcessRunner _runner = new();
    private readonly FakeProcessHandle _handle = new(FakeWinwsPid);
    private readonly InMemorySettingsStore _store = new();
    private readonly Func<bool> _originalAnyWinws = ZapretAutoStrategy.AnyWinwsRunning;
    private readonly Func<HttpMessageHandler> _originalHandler = ZapretAutoStrategy.CreateHandler;
    private readonly List<MainWindowViewModel> _models = new();

    private Func<ZapretManager, IProgress<ZapretAutoStrategy.StrategyProgress>?, CancellationToken,
        Task<ZapretAutoStrategy.StrategySweepResult>>? _probeImpl;

    private int _probeCalls;

    public MainWindowViewModelZapretProbeTests()
    {
        _originalDataDir = AppPaths.DataDir;
        _originalLang = VPNRouter.App.Localization.Strings.Lang;
        _originalProbe = MainWindowViewModel.InstalledProbe;
        SetProbeResponse(HttpStatusCode.ServiceUnavailable);
        ZapretAutoStrategy.AnyWinwsRunning = () => false;
        _tempDataDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-vm-zapret-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDataDir);
        AppPaths.OverrideDataDir(_tempDataDir);

        _runner.OnStart(_ => true, _ => _handle);
        MainWindowViewModel.InstalledProbe = (manager, progress, _, ct) =>
        {
            Interlocked.Increment(ref _probeCalls);
            if (_probeImpl is null)
                throw new InvalidOperationException("the sweep was not expected to run in this test");
            return _probeImpl(manager, progress, ct);
        };
    }

    public void Dispose()
    {
        MainWindowViewModel.InstalledProbe = _originalProbe;
        ZapretAutoStrategy.AnyWinwsRunning = _originalAnyWinws;
        VPNRouter.App.Localization.Strings.Lang = _originalLang;
        foreach (var vm in _models)
        {
            try { vm.Dispose(); }
            catch { }
        }
        ZapretAutoStrategy.CreateHandler = _originalHandler;
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

    private static ZapretAutoStrategy.StrategySweepResult Sweep(
        string? winner,
        string? diagnostic = null,
        IReadOnlyDictionary<string, ZapretStrategyTestResult>? perStrategy = null,
        string? probeLogPath = null) =>
        new(winner, 1, 1, diagnostic, perStrategy, probeLogPath);

    private void SweepReturns(ZapretAutoStrategy.StrategySweepResult result) =>
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

    private static void SetProbeResponse(HttpStatusCode status) =>
        ZapretAutoStrategy.CreateHandler = () => new ZapretTestHandler(_ => status);

    private static void UseRussian(MainWindowViewModel vm)
    {
        vm.IsRussian = true;
        VPNRouter.App.Localization.Strings.Lang = "ru";
    }

    // ---- seam ---------------------------------------------------------------------------------------

    [Fact]
    public void InstalledProbe_DefaultsToTheRealSweep()
    {
        Func<ZapretManager, IProgress<ZapretAutoStrategy.StrategyProgress>?, Serilog.ILogger?, CancellationToken,
            Task<ZapretAutoStrategy.StrategySweepResult>> real = ZapretAutoStrategy.RunInstalledProbeAsync;

        Assert.Equal(real, _originalProbe);
    }

    [AvaloniaFact]
    public async Task Probe_ThroughTheDefaultWiring_RunsTheRealSweepAndStartsItsWinner()
    {
        Assert.SkipUnless(ZapretAutoStrategy.IsRunningAsAdmin(), "The Windows probe requires elevation.");
        var vm = NewVm("general", "general (ALT)");
        var runner = new FakeProcessRunner().OnStart(_ => true, _ => new FakeProcessHandle(FakeWinwsPid));
        SetField(vm, "_zapret", new ZapretManager(runner: runner));
        SetProbeResponse(HttpStatusCode.OK);
        MainWindowViewModel.InstalledProbe = _originalProbe;

        await Run(vm);

        Assert.True(vm.ZapretEnabled);
        Assert.Equal("general", vm.ZapretWinningStrategy);
        Assert.Equal(2, runner.StartCalls.Count);
        Assert.All(runner.StartCalls, call => Assert.Equal(ZapretUpdater.WinwsExePath, call.ExecutablePath));
    }

    [AvaloniaFact]
    public async Task RunTestsCommand_ShowsDirectHttpsResultsWithoutStartingWinws()
    {
        var vm = NewVm("general");
        SetProbeResponse(HttpStatusCode.OK);
        await vm.RunZapretTestsCommand.ExecuteAsync(null);

        Assert.Equal(8, vm.ZapretActionOutput.Count);
        Assert.Contains("discord.com — HTTPS reachable", vm.ZapretActionOutput);
        Assert.Empty(_runner.StartCalls);
        Assert.False(vm.IsZapretActionRunning);
    }

    [AvaloniaFact]
    public async Task OtherRunningHelper_ReportsConflictWithoutStartingOrStoppingAnyProcess()
    {
        var vm = NewVm("general");
        ZapretAutoStrategy.AnyWinwsRunning = () => true;
        await Run(vm);
        Assert.Equal(VPNRouter.Core.Localization.Strings.ZapretOtherHelper, vm.ZapretStatus);
        Assert.False(vm.ZapretEnabled);
        Assert.Empty(_runner.StartCalls);
        Assert.Equal(0, _handle.KillCallCount);
        Assert.Equal(0, _probeCalls);
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

            progress!.Report(new ZapretAutoStrategy.StrategyProgress(2, 3, "general (ALT)", 0, 0));
            await Task.Yield();
            Snap();
            progress.Report(new ZapretAutoStrategy.StrategyProgress(2, 3, "", 5, 6));
            await Task.Yield();
            Snap();
            progress.Report(new ZapretAutoStrategy.StrategyProgress(3, 3, "general (ALT2)", 0, 0));
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
    public async Task Winner_StartsInstalledWinwsAndRecordsItsMeasuredScore()
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
            progress!.Report(new ZapretAutoStrategy.StrategyProgress(1, 2, "general", 0, 0));
            progress.Report(new ZapretAutoStrategy.StrategyProgress(2, 2, "", 2, 9));
            await Task.Yield();
            return Sweep("general", perStrategy: perStrategy, probeLogPath: @"C:\logs\probe.log");
        };

        await Run(vm);

        var start = Assert.Single(_runner.StartCalls);
        Assert.Equal(ZapretUpdater.WinwsExePath, start.ExecutablePath);
        Assert.Equal(new[] { "--wf-tcp=443", "--tag=general" }, start.Arguments);
        Assert.Equal(ZapretUpdater.BinDir, start.WorkingDirectory);

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
    public async Task Winner_WithoutInstalledStrategy_ShowsRepairAndStartsNothing()
    {
        var vm = NewVm("general");
        SetField(vm, "_parsedStrategies", new List<ZapretStrategy> { new("general", "--wf-tcp=443") });
        SweepReturns(Sweep("general"));

        await Run(vm);

        Assert.Empty(_runner.StartCalls);
        Assert.False(vm.ZapretEnabled);
        Assert.True(vm.IsZapretFallback);
        Assert.Equal(VPNRouter.Core.Localization.Strings.ZapretRepairPackage, vm.ZapretStatus);
        Assert.Null(ZapretProbeCache.TryLoad());
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
        Assert.Equal(VPNRouter.Core.Localization.Strings.ZapretRepairPackage, vm.ZapretStatus);
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
        Assert.Equal(VPNRouter.Core.Localization.Strings.ZapretRepairPackage, vm.ZapretStatus);
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
    [InlineData("missing_package",
        "Open Advanced → Zapret maintenance and download the package again.",
        "Откройте «Прочее» → «Обслуживание Zapret» и скачайте пакет заново.")]
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
        SweepReturns(Sweep(null, diagnostic));

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
        _probeImpl = (_, _, _) => throw new InvalidOperationException("probe failed");

        await Run(vm);

        Assert.Equal("Error: probe failed", vm.ZapretStatus);
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
        SetProbeResponse(HttpStatusCode.OK);

        await Run(vm);

        Assert.Equal(0, _probeCalls);
        Assert.Single(_runner.StartCalls);
        Assert.True(vm.ZapretEnabled);
        Assert.Equal("general (ALT)", vm.ZapretWinningStrategy);
        Assert.Equal($"Running [general (ALT)] (PID {FakeWinwsPid}, warm)", vm.ZapretStatus);
        Assert.Equal((8, 8), (vm.ZapretProbePassCount, vm.ZapretProbeTotalCount));
        Assert.Equal(vm.ZapretStrategies.IndexOf("general (ALT)"), vm.ZapretStrategyIndex);

        var cache = ZapretProbeCache.TryLoad()!;
        Assert.Equal("general (ALT)", cache.Strategy);
        Assert.Equal(4, cache.SuccessRunCount);
        Assert.Equal((8, 8), (cache.TargetsPassed, cache.TargetsTotal));
        Assert.Equal("general (ALT)", _store.LastSave!.Value.Settings.App.ZapretStrategy);
        Assert.False(vm.IsZapretProbing);
    }

    [AvaloniaFact]
    public async Task CacheHit_InRussian_ReportsTheWarmStart()
    {
        var vm = NewVm("general");
        UseRussian(vm);
        SeedCache("general");
        SetProbeResponse(HttpStatusCode.NoContent);

        await Run(vm);

        Assert.Equal($"Работает [general] (PID {FakeWinwsPid}, warm)", vm.ZapretStatus);
    }

    [AvaloniaFact]
    public async Task CacheHit_WhoseTargetProbeFails_StopsTheProcess_RecordsAFailure_AndRunsTheSweep()
    {
        var vm = NewVm("general");
        SeedCache("general", successRunCount: 3, failureCount: 0, passed: 4, total: 5);
        SetProbeResponse(HttpStatusCode.ServiceUnavailable);
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
    public async Task DriverConflictPreservesCurrentStrategyOnManualRestartAndFreshProbe()
    {
        var vm = NewVm("general");
        bool conflict = false;
        var manager = new ZapretManager(runner: _runner) { HasDriverConflict = () => conflict };
        SetField(vm, "_zapret", manager);
        manager.Start("--wf-tcp=443");
        vm.ZapretStrategyIndex = vm.ZapretStrategies.IndexOf("general");
        vm.ZapretWinningStrategy = "previous";
        vm.ZapretEnabled = true;
        conflict = true;

        await vm.StartZapretWithSelectedStrategyCommand.ExecuteAsync(null);
        await vm.ForceFreshProbeCommand.ExecuteAsync(null);

        Assert.Single(_runner.StartCalls);
        Assert.Equal(0, _handle.KillCallCount);
        Assert.Equal(0, _probeCalls);
        Assert.True(vm.ZapretEnabled);
        Assert.Equal("previous", vm.ZapretWinningStrategy);
        Assert.Equal(VPNRouter.Core.Localization.Strings.ZapretServiceOutcome(ZapretActions.ServiceApply.Conflict), vm.ZapretStatus);
    }

    [AvaloniaFact]
    public async Task BusyInstallationPreservesTheRunningStrategyOnFreshProbe()
    {
        var vm = NewVm("general");
        var manager = GetField<ZapretManager>(vm, "_zapret")!;
        manager.Start("--wf-tcp=443");
        vm.ZapretWinningStrategy = "previous";
        vm.ZapretEnabled = true;
        using var pin = ZapretRuntimeDirectory.LockInstallation(ZapretRuntimeDirectory.Root);

        await vm.ForceFreshProbeCommand.ExecuteAsync(null);

        Assert.Equal(0, _probeCalls);
        Assert.Equal(0, _handle.KillCallCount);
        Assert.Equal(_handle.Pid, manager.Pid);
        Assert.True(vm.ZapretEnabled);
        Assert.Equal("previous", vm.ZapretWinningStrategy);
        Assert.False(vm.IsZapretProbing);
        Assert.StartsWith("Error:", vm.ZapretStatus);
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void QuitOrDisposeKeepsUnconfirmedHandleUntilExplicitRetry(bool dispose, bool unknown)
    {
        var vm = NewVm("general");
        var manager = GetField<ZapretManager>(vm, "_zapret")!;
        manager.Start("--wf-tcp=443");
        var shutdowns = 0;
        vm.RequestDesktopShutdown = () => shutdowns++;
        _handle.RefuseKill = !unknown;
        _handle.ExitQueryUnavailable = unknown;
        try
        {
            if (dispose) Assert.Throws<InvalidOperationException>(vm.Dispose);
            else vm.QuitCommand.Execute(null);

            Assert.Same(manager, GetField<ZapretManager>(vm, "_zapret"));
            Assert.True(manager.HasOwnedProcess);
            if (unknown) Assert.Null(manager.Pid);
            Assert.Equal(0, _handle.DisposeCallCount);
            Assert.Equal(unknown ? 0 : 1, _handle.KillCallCount);
            Assert.Equal(0, shutdowns);
            Assert.False(vm.IsShutdownPrepared);
            Assert.Equal(VPNRouter.Core.Localization.Strings.ZapretQuitPending, vm.StatusText);
        }
        finally
        {
            _handle.RefuseKill = _handle.ExitQueryUnavailable = false;
        }

        if (dispose) vm.Dispose();
        else vm.QuitCommand.Execute(null);
        Assert.Null(GetField<ZapretManager>(vm, "_zapret"));
        Assert.Equal(1, _handle.DisposeCallCount);
        Assert.True(vm.IsShutdownPrepared);
        Assert.Equal(dispose ? 0 : 1, shutdowns);
    }

    [AvaloniaFact]
    public async Task QuitDuringProbeCancelsBeforeAllowingShutdownOnRetry()
    {
        var vm = NewVm("general");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _probeImpl = async (_, _, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Sweep(null);
        };
        var shutdowns = 0;
        vm.RequestDesktopShutdown = () => shutdowns++;
        var probe = vm.ForceFreshProbeCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        vm.QuitCommand.Execute(null);
        Assert.Equal(0, shutdowns);
        await probe;
        Assert.False(vm.IsZapretProbing);
        vm.QuitCommand.Execute(null);
        Assert.Equal(1, shutdowns);
    }

    [AvaloniaFact]
    public void DpiFolderCommandOpensInstalledPackageWithSystemExplorer()
    {
        var vm = NewVm("general");
        System.Diagnostics.ProcessStartInfo? launched = null;
        vm.LaunchFolder = info => launched = info;

        vm.OpenZapretFolderCommand.Execute(null);

        Assert.NotNull(launched);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), launched.FileName);
        Assert.False(launched.UseShellExecute);
        Assert.Equal(Environment.SystemDirectory, launched.WorkingDirectory);
        Assert.Equal(ZapretUpdater.ZapretDir, Assert.Single(launched.ArgumentList));
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
