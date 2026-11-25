using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using VPNRouter.App.ViewModels.Internals;
using VPNRouter.App.Views;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Strings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.Tests;

public sealed class BusinessAutoConnectTests
{
    private static DeepVerifyResult Ok(int latencyMs) => new(true, latencyMs, null, null);

    private static DeepVerifyResult Failed(DeepVerifyFailurePhase phase = DeepVerifyFailurePhase.ProxiedHttp) =>
        new(false, 0, null, "synthetic", phase);

    private static MainWindowViewModel CreateVm(
        out InMemorySettingsStore store, bool automatic = true)
    {
        store = new InMemorySettingsStore();
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, BusinessDefaultsTests.MultiServerConnection,
            BusinessBootstrap.ReadRules());
        settings.App.BusinessServerSelectionMode = automatic
            ? BusinessServerSelectionMode.Automatic
            : BusinessServerSelectionMode.Manual;
        store.Save(settings, AppPaths.ConfigYamlPath);
        return new MainWindowViewModel(store, null, null, null,
            businessBootstrap: static (_, _) => null)
        {
            ExternalTunnelOwned = static () => false,
        };
    }

    [AvaloniaFact]
    public async Task Picker_UsesTheDeepVerifierAndShowsItsHttpLatency()
    {
        using var vm = CreateVm(out _, automatic: false);
        var calls = 0;
        vm.BusinessServerVerifier = (entry, pool, _) =>
        {
            Interlocked.Increment(ref calls);
            Assert.Contains(pool, server => ReferenceEquals(server, entry));
            return Task.FromResult(Ok(42));
        };
        var window = new BusinessWindow { DataContext = vm };
        window.Show();
        try
        {
            await window.RefreshBusinessServersAsync();
            window.OpenServerPanel();
            var selector = window.FindControl<ComboBox>("BusinessServerSelector")!;
            var choices = selector.Items.Cast<BusinessServerChoiceViewModel>().ToArray();
            Assert.Equal(2, calls);
            Assert.All(choices, choice => Assert.Equal(
                Strings.BusinessServerLatency(42), choice.PingText));
            Assert.All(choices, choice => Assert.True(choice.IsGood));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Startup_VerifiesWithoutChangingSelectionOrSaving()
    {
        using var vm = CreateVm(out var store, automatic: true);
        var selected = vm.SelectedServer;
        var saves = store.SaveCount;
        var calls = 0;
        vm.BusinessServerVerifier = (entry, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(entry.Name == BusinessDefaultsTests.ReserveName ? Ok(90) : Ok(35));
        };

        vm.StartBusinessAutoSelectionIfConfigured();
        await vm.WaitForBusinessAutoSelectionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
        Assert.Same(selected, vm.SelectedServer);
        Assert.Equal(saves, store.SaveCount);
        Assert.Contains("Pilot", vm.BusinessAutoSelectStatusText);
        Assert.Contains("35", vm.BusinessAutoSelectStatusText);
    }

    [AvaloniaFact]
    public async Task PickerFailureForCachedWinner_ForcesNextConnectToChooseAnAvailablePeer()
    {
        using var vm = CreateVm(out var store, automatic: true);
        var reserve = vm.Servers.Single(server => server.Name == BusinessDefaultsTests.ReserveName);
        vm.BusinessServerVerifier = (entry, _, _) =>
            Task.FromResult(entry.Name == BusinessDefaultsTests.ReserveName ? Ok(10) : Ok(50));
        vm.StartBusinessAutoSelectionIfConfigured();
        await vm.WaitForBusinessAutoSelectionAsync(TestContext.Current.CancellationToken);

        // The periodic picker probe reports the cached winner as failed.
        vm.BusinessServerVerifier = (entry, _, _) => Task.FromResult(
            entry.Name == BusinessDefaultsTests.ReserveName ? Failed() : Ok(15));
        var picker = await vm.ProbeBusinessServerAsync(
            reserve, TestContext.Current.CancellationToken);
        Assert.False(picker.IsReachable);

        var saves = store.SaveCount;
        AppSettings? captured = null;
        vm.BusinessConnectStart = (settings, _, _) =>
        {
            captured = settings;
            return Task.FromResult((Task.CompletedTask,
                VPNRouter.App.ViewModels.Internals.TwoPhaseStartOutcome.Connected));
        };
        await vm.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Equal("Pilot", captured!.Vless.ActiveServer);
        Assert.True(vm.IsConnected);
        Assert.Equal(saves, store.SaveCount); // The winner is transient and never persisted.
    }

    [AvaloniaFact]
    public async Task StalePickerFailureCannotInvalidateANewerCachedWinner()
    {
        using var vm = CreateVm(out _, automatic: true);
        var reserve = vm.Servers.Single(server => server.Name == BusinessDefaultsTests.ReserveName);
        vm.BusinessServerVerifier = (entry, _, _) =>
            Task.FromResult(entry.Name == BusinessDefaultsTests.ReserveName ? Ok(10) : Ok(50));
        vm.StartBusinessAutoSelectionIfConfigured();
        await vm.WaitForBusinessAutoSelectionAsync(TestContext.Current.CancellationToken);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<DeepVerifyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.BusinessServerVerifier = async (_, _, ct) =>
        {
            started.TrySetResult();
            return await gate.Task.WaitAsync(ct);
        };
        var stale = vm.ProbeBusinessServerAsync(reserve, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        // A newer automatic outcome replaces the cached winner while the old
        // picker probe is still in flight.
        vm.BusinessServerVerifier = (entry, _, _) =>
            Task.FromResult(entry.Name == BusinessDefaultsTests.ReserveName ? Ok(11) : Ok(50));
        vm.StartBusinessAutoSelectionIfConfigured();
        await vm.WaitForBusinessAutoSelectionAsync(TestContext.Current.CancellationToken);

        gate.SetResult(Failed());
        Assert.False((await stale.WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken)).IsReachable);
        var cached = vm.GetBusinessAutoPickerResults();
        Assert.NotNull(cached);
        Assert.True(cached![BusinessDefaultsTests.ReserveName].IsReachable);
    }

    [AvaloniaFact]
    public async Task AutomaticConnect_UsesCachedFastestWinner_WithoutAnotherProbeWave()
    {
        using var vm = CreateVm(out var store, automatic: true);
        var calls = 0;
        vm.BusinessServerVerifier = (entry, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(entry.Name == BusinessDefaultsTests.ReserveName ? Ok(90) : Ok(35));
        };
        vm.StartBusinessAutoSelectionIfConfigured();
        await vm.WaitForBusinessAutoSelectionAsync(TestContext.Current.CancellationToken);
        var saves = store.SaveCount;

        AppSettings? captured = null;
        vm.BusinessConnectStart = (settings, _, _) =>
        {
            captured = settings;
            return Task.FromResult((Task.CompletedTask,
                VPNRouter.App.ViewModels.Internals.TwoPhaseStartOutcome.Connected));
        };

        await vm.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Equal(2, calls);
        Assert.NotNull(captured);
        Assert.Equal("Pilot", captured!.Vless.ActiveServer);
        Assert.False(captured.Vless.AutoSelectBestServer);
        var exact = Assert.Single(captured.Vless.GetActiveServers());
        Assert.True(exact.UseAsSingleOutbound);
        Assert.True(vm.IsConnected);
        Assert.False(vm.IsConnecting);
        Assert.Equal(saves, store.SaveCount);
    }

    [AvaloniaFact]
    public async Task VerifiedHook_PinsFastestButKeepsManualSelectionAndSavedRows()
    {
        using var vm = CreateVm(out var store, automatic: false);
        vm.SelectBusinessServer(vm.Servers[1]);
        var manual = vm.SelectedServer;
        var saves = store.SaveCount;
        var calls = 0;
        vm.BusinessServerVerifier = (entry, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(entry.Name == BusinessDefaultsTests.ReserveName ? Ok(90) : Ok(20));
        };

        AppSettings? captured = null;
        vm.BusinessConnectStart = (settings, _, _) =>
        {
            captured = settings;
            return Task.FromResult((Task.CompletedTask,
                VPNRouter.App.ViewModels.Internals.TwoPhaseStartOutcome.Connected));
        };

        var connected = await vm.ConnectVerifiedBusinessServerAsync(
            TestContext.Current.CancellationToken);

        Assert.True(connected);
        Assert.Equal(2, calls);
        Assert.Same(manual, vm.SelectedServer);
        Assert.Equal(BusinessDefaultsTests.ReserveName, store.Load(AppPaths.ConfigYamlPath).Vless.ActiveServer);
        Assert.Equal(BusinessServerSelectionMode.Manual,
            store.Load(AppPaths.ConfigYamlPath).App.BusinessServerSelectionMode);
        Assert.NotNull(captured);
        Assert.Equal("Pilot", captured!.Vless.ActiveServer);
        Assert.True(captured.Vless.IsTransientBusinessSelection);
        Assert.True(Assert.Single(captured.Vless.GetActiveServers()).UseAsSingleOutbound);
        Assert.Equal(saves, store.SaveCount);
        Assert.False(vm.HasBusinessAutoSelectStatus);
        Assert.Equal(string.Empty, vm.BusinessAutoSelectStatusText);
        Assert.False(vm.Servers.Single(server => server.Name == "Pilot").SourceEntry.UseAsSingleOutbound);
    }

    [AvaloniaFact]
    public async Task VerifiedHook_NoPositiveResult_ReportsFailureAndStartsNothing()
    {
        using var vm = CreateVm(out var store, automatic: false);
        vm.SelectBusinessServer(vm.Servers[1]);
        var selected = vm.SelectedServer;
        var saves = store.SaveCount;
        var starts = 0;
        vm.BusinessServerVerifier = (_, _, _) => Task.FromResult(Failed());
        vm.BusinessConnectStart = (_, _, _) =>
        {
            Interlocked.Increment(ref starts);
            return Task.FromResult((Task.CompletedTask,
                VPNRouter.App.ViewModels.Internals.TwoPhaseStartOutcome.Connected));
        };

        var connected = await vm.ConnectVerifiedBusinessServerAsync(
            TestContext.Current.CancellationToken);

        Assert.False(connected);
        Assert.Equal(0, starts);
        Assert.False(vm.IsConnecting);
        Assert.Same(selected, vm.SelectedServer);
        Assert.Equal(saves, store.SaveCount);
        Assert.Equal(Strings.BusinessAutoNoneWorking, vm.StatusText);
    }

    [AvaloniaFact]
    public async Task VerifiedHook_CancellationDuringPreparationDoesNotLaunchPinnedTunnel()
    {
        using var vm = CreateVm(out _, automatic: false);
        var prepareStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.BusinessServerVerifier = (_, _, _) => Task.FromResult(Ok(10));
        vm.PrepareColdStartForTest = async ct =>
        {
            prepareStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        var starts = 0;
        vm.BusinessConnectStart = (_, _, _) =>
        {
            Interlocked.Increment(ref starts);
            return Task.FromResult((Task.CompletedTask,
                VPNRouter.App.ViewModels.Internals.TwoPhaseStartOutcome.Connected));
        };

        using var cts = new CancellationTokenSource();
        var hook = vm.ConnectVerifiedBusinessServerAsync(cts.Token);
        await prepareStarted.Task.WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        cts.Cancel();

        Assert.False(await hook.WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));
        Assert.Equal(0, starts);
        Assert.False(vm.IsConnecting);
    }

    [AvaloniaFact]
    public async Task AutomaticConnect_FailedOwnStart_InvalidatesWinnerForRetry()
    {
        using var vm = CreateVm(out _, automatic: true);
        var calls = 0;
        vm.BusinessServerVerifier = (entry, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(entry.Name == BusinessDefaultsTests.ReserveName ? Ok(90) : Ok(35));
        };
        vm.StartBusinessAutoSelectionIfConfigured();
        await vm.WaitForBusinessAutoSelectionAsync(TestContext.Current.CancellationToken);

        var starts = 0;
        vm.BusinessConnectStart = (_, _, _) =>
        {
            Interlocked.Increment(ref starts);
            return Task.FromResult((Task.CompletedTask,
                VPNRouter.App.ViewModels.Internals.TwoPhaseStartOutcome.PhaseATimeout));
        };
        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.Equal(1, starts);
        Assert.False(vm.IsConnecting);

        vm.BusinessConnectStart = (_, _, _) =>
        {
            Interlocked.Increment(ref starts);
            return Task.FromResult((Task.CompletedTask,
                VPNRouter.App.ViewModels.Internals.TwoPhaseStartOutcome.Connected));
        };
        await vm.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.True(vm.IsConnected);
        Assert.True(calls > 2);
        Assert.Equal(2, starts);
    }

    [AvaloniaFact]
    public async Task VerifiedHook_SelectionChangeCancelsInFlightWork()
    {
        using var vm = CreateVm(out var store, automatic: false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<DeepVerifyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.BusinessServerVerifier = async (_, _, ct) =>
        {
            started.TrySetResult();
            return await gate.Task.WaitAsync(ct);
        };
        var starts = 0;
        vm.BusinessConnectStart = (_, _, _) =>
        {
            Interlocked.Increment(ref starts);
            return Task.FromResult((Task.CompletedTask,
                VPNRouter.App.ViewModels.Internals.TwoPhaseStartOutcome.Connected));
        };

        var hook = vm.ConnectVerifiedBusinessServerAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        vm.SelectBusinessServer(vm.Servers[1]);

        Assert.False(await hook.WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));
        Assert.Equal(0, starts);
        Assert.False(vm.IsConnecting);
    }

    [AvaloniaFact]
    public async Task ExternalOwner_IsAdoptedOrRefused_WithoutStartingPinnedTunnel()
    {
        using var vm = CreateVm(out var store, automatic: true);
        var calls = 0;
        vm.BusinessServerVerifier = (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Ok(10));
        };
        var starts = 0;
        vm.BusinessConnectStart = (_, _, _) =>
        {
            Interlocked.Increment(ref starts);
            return Task.FromResult((Task.CompletedTask,
                VPNRouter.App.ViewModels.Internals.TwoPhaseStartOutcome.Connected));
        };

        vm.ExternalTunnelOwned = () => true;
        vm.AdoptExternalTunnelForTest = () => false;
        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.False(vm.IsConnecting);
        Assert.False(vm.IsConnected);
        Assert.Equal(Strings.BusinessExternalTunnelBusy, vm.StatusText);
        Assert.Equal(0, calls);
        Assert.Equal(0, starts);

        vm.AdoptExternalTunnelForTest = () =>
        {
            vm.IsConnected = true;
            return true;
        };
        await vm.ToggleConnectionCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        Assert.False(vm.IsConnecting);
        Assert.Equal(0, starts);

        // A verified repair attempt must not claim success for the foreign tunnel.
        vm.IsConnected = false;
        var connected = await vm.ConnectVerifiedBusinessServerAsync(
            TestContext.Current.CancellationToken);
        Assert.False(connected);
        Assert.False(vm.IsConnecting);
        Assert.Equal(0, starts);
    }

    [AvaloniaFact]
    public void AutomaticPool_ExcludesChainTargetsWhosePolicyPathCannotSelectThem()
    {
        var direct = new VlessServerEntry
        {
            Name = "direct", Protocol = "vless", Server = "192.0.2.1", Port = 443,
            Uuid = "direct-uuid", Security = "tls",
        };
        var upstream = new VlessServerEntry
        {
            Name = "upstream", Protocol = "vless", Server = "192.0.2.2", Port = 443,
            Uuid = "upstream-uuid", Security = "tls", OutboundId = "hop",
        };
        var target = new VlessServerEntry
        {
            Name = "target", Protocol = "vless", Server = "192.0.2.3", Port = 443,
            Uuid = "target-uuid", Security = "tls", DetourVia = "hop",
        };

        var candidates = MainWindowViewModel.GetBusinessAutoCandidates([direct, upstream, target]);

        Assert.Contains(candidates, server => server.Name == "direct");
        Assert.DoesNotContain(candidates, server => server.Name == "target");
    }

    [AvaloniaFact]
    public void LegacyNamedChoiceWithoutExplicitMode_BecomesAutomatic()
    {
        var fresh = new AppSettings();
        BusinessDefaults.Apply(fresh, BusinessDefaultsTests.MultiServerConnection,
            BusinessBootstrap.ReadRules());
        Assert.Equal(BusinessServerSelectionMode.Automatic,
            fresh.App.BusinessServerSelectionMode);

        var legacy = new AppSettings();
        legacy.Vless.ActiveServer = BusinessDefaultsTests.ReserveName;
        legacy.Vless.Servers = [new VlessServerEntry
        {
            Name = BusinessDefaultsTests.ReserveName,
            Protocol = "vless",
            Server = "192.0.2.2",
            Port = 443,
            Uuid = "old",
            Security = "tls",
        }];
        BusinessDefaults.Apply(legacy, BusinessDefaultsTests.MultiServerConnection,
            BusinessBootstrap.ReadRules());
        Assert.Equal(BusinessServerSelectionMode.Automatic,
            legacy.App.BusinessServerSelectionMode);
        Assert.Equal(BusinessDefaultsTests.ReserveName, legacy.Vless.ActiveServer);

        legacy.App.BusinessServerSelectionMode = BusinessServerSelectionMode.Manual;
        BusinessDefaults.Apply(legacy, BusinessDefaultsTests.MultiServerConnection,
            BusinessBootstrap.ReadRules());
        Assert.Equal(BusinessServerSelectionMode.Manual,
            legacy.App.BusinessServerSelectionMode);
    }
}
