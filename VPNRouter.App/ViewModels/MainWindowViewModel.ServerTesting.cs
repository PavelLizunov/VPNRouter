using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.App.Localization;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private CancellationTokenSource? _serverTestCts;
    private CancellationTokenSource? _serverDeepCts;
    private VlessDeepVerifier? _deepVerifier;

    // With the tunnel up, probes are bound to the physical interface (Windows), so the numbers are real; without that they would only
    // measure the tunnel's own answer and the test stays unavailable.
    private static bool CanProbeWhileConnected =>
        OperatingSystem.IsWindows() && TcpTlsProbe.OutboundInterfaceIndex() is > 0;

    private const int ServerTestConcurrency = 20;
    private const int ServerDeepConcurrency = 5;

    [ObservableProperty] private string _serverTestProgressText = string.Empty;

    [ObservableProperty] private string _subscriptionTestProgressText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerTestButtonText))]
    private bool _isTestingServers;

    public string ServerTestButtonText => IsTestingServers
        ? Strings.ServerTestCancel
        : Strings.ServerTestAll;

    [ObservableProperty] private string _serverDeepProgressText = string.Empty;

    [ObservableProperty] private string _subscriptionDeepProgressText = string.Empty;

    [ObservableProperty] private string _serverTestImplausibleWarning = string.Empty;

    [ObservableProperty] private string _subscriptionTestImplausibleWarning = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerDeepButtonText))]
    private bool _isDeepTestingServers;

    public string ServerDeepButtonText => IsDeepTestingServers
        ? Strings.ServerDeepStop
        : Strings.ServerDeepVerify;

    [RelayCommand]
    private async Task TestServerAsync(ServerViewModel? server)
    {
        if (server == null) return;
        if (server.IsTesting) return;

        if (IsConnected && !CanProbeWhileConnected) return;

        server.IsTesting = true;
        try
        {
            var entry = server.ToEntry();
            var result = await TcpTlsProbe.ProbeServerAsync(entry);
            server.ApplyProbeResult(result);
        }
        catch (Exception ex)
        {
            server.ApplyProbeResult(new ServerProbeResult(
                ServerProbeStatus.Timeout, 0, ex.GetType().Name));
        }
        finally
        {
            server.IsTesting = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task TestAllServersAsync()
    {
        if (IsTestingServers)
        {
            _serverTestCts?.Cancel();
            return;
        }

        await TestServerCollectionAsync(
            Servers.ToList(),
            Strings.ServerTestingManual,
            setProgress: text => ServerTestProgressText = text,
            setWarning: text => ServerTestImplausibleWarning = text);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task TestAllSubscriptionServersAsync()
    {
        if (IsTestingServers)
        {
            _serverTestCts?.Cancel();
            return;
        }

        await TestServerCollectionAsync(
            SubscriptionServers.ToList(),
            Strings.ServerTestingSubscriptions,
            setProgress: text => SubscriptionTestProgressText = text,
            setWarning: text => SubscriptionTestImplausibleWarning = text);
    }

    private async Task TestServerCollectionAsync(
        IReadOnlyList<ServerViewModel> servers,
        string labelPrefix,
        Action<string> setProgress,
        Action<string> setWarning)
    {
        setWarning(string.Empty);

        if (IsConnected && !CanProbeWhileConnected)
        {
            setWarning(Strings.PingUnavailableWhenConnected);
            return;
        }

        if (servers.Count == 0)
        {
            setProgress(Strings.ServerTestNoServers);
            return;
        }

        _serverTestCts = new CancellationTokenSource();
        var ct = _serverTestCts.Token;

        IsTestingServers = true;
        setProgress($"{labelPrefix}: 0 / {servers.Count}");

        foreach (var s in servers) s.IsTesting = true;

        var sem = new SemaphoreSlim(ServerTestConcurrency);
        var done = 0;
        var total = servers.Count;

        try
        {
            var tasks = servers.Select(async server =>
            {
                try
                {
                    await sem.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    server.IsTesting = false;
                    return;
                }

                try
                {
                    if (ct.IsCancellationRequested)
                    {
                        server.IsTesting = false;
                        return;
                    }

                    var entry = server.ToEntry();
                    var result = await TcpTlsProbe.ProbeServerAsync(entry, ct);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        server.ApplyProbeResult(result);
                    });
                }
                catch (OperationCanceledException)
                {
                    server.IsTesting = false;
                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        server.ApplyProbeResult(new ServerProbeResult(
                            ServerProbeStatus.Timeout, 0, ex.GetType().Name));
                    });
                }
                finally
                {
                    sem.Release();
                    var n = Interlocked.Increment(ref done);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        setProgress($"{labelPrefix}: {n} / {total}");
                    });
                }
            });

            await Task.WhenAll(tasks);

            var responded = servers.Count(s => s.TestStatus is
                ServerProbeStatus.Ok or
                ServerProbeStatus.Slow or
                ServerProbeStatus.Implausible);

            setProgress(IsRussian
                ? $"Готово. Пинг прошёл: {responded} / {total} · полная проверка — «Глубокая проверка»"
                : $"Done. Pinged: {responded} / {total} · full check via Deep verify");

            var implausible = servers.Count(s => s.TestStatus is ServerProbeStatus.Implausible);
            if (implausible > 0 && implausible >= total / 2)
            {
                setWarning(IsRussian
                    ? "⚠ Активный VPN или прокси перехватывает соединения — реальные пинги недоступны. Отключите VPN для честных результатов или нажмите «Глубокая проверка» (она запускает sing-box отдельно и не зависит от текущего туннеля)."
                    : "⚠ Active VPN or proxy intercepting connections — real pings unavailable. Disconnect for true results or click Deep verify (it spawns sing-box independently and bypasses the current tunnel).");
            }
        }
        catch (OperationCanceledException)
        {
            setProgress(Strings.ServerTestCancelled);
            foreach (var s in servers) s.IsTesting = false;
        }
        finally
        {
            IsTestingServers = false;
            _serverTestCts?.Dispose();
            _serverTestCts = null;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DeepVerifyAllServersAsync()
    {
        if (IsDeepTestingServers)
        {
            _serverDeepCts?.Cancel();
            return;
        }

        await DeepVerifyCollectionAsync(
            Servers.ToList(),
            Strings.ServerDeepVerifyManual,
            setProgress: text => ServerDeepProgressText = text);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DeepVerifyAllSubscriptionServersAsync()
    {
        if (IsDeepTestingServers)
        {
            _serverDeepCts?.Cancel();
            return;
        }

        await DeepVerifyCollectionAsync(
            SubscriptionServers.ToList(),
            Strings.ServerDeepVerifySubscription,
            setProgress: text => SubscriptionDeepProgressText = text);
    }

    private async Task DeepVerifyCollectionAsync(
        IReadOnlyList<ServerViewModel> servers,
        string labelPrefix,
        Action<string> setProgress)
    {
        if (servers.Count == 0)
        {
            setProgress(Strings.ServerTestNoServers);
            return;
        }

        _deepVerifier ??= new VlessDeepVerifier(_logger);

        if (!_deepVerifier.IsAvailable)
        {
            setProgress(IsRussian
                ? "sing-box не найден"
                : "sing-box binary missing");
            return;
        }

        _serverDeepCts = new CancellationTokenSource();
        var ct = _serverDeepCts.Token;

        IsDeepTestingServers = true;
        setProgress($"{labelPrefix}: 0 / {servers.Count}");

        var entryToVm = new Dictionary<VlessServerEntry, ServerViewModel>();
        var entries = new List<VlessServerEntry>(servers.Count);
        foreach (var vm in servers)
        {
            vm.IsDeepTesting = true;
            var entry = vm.ToEntry();
            entries.Add(entry);
            entryToVm[entry] = vm;
        }

        var total = entries.Count;

        try
        {
            await _deepVerifier.VerifyBatchAsync(
                entries,
                onOneDone: (entry, result) =>
                {
                    if (entryToVm.TryGetValue(entry, out var vm))
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            vm.ApplyDeepResult(result);
                        });
                    }
                },
                measureBandwidth: true,
                progress: new Progress<(int done, int total)>(p =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        setProgress($"{labelPrefix}: {p.done} / {p.total}");
                    });
                }),
                ct: ct);

            var verified = servers.Count(s => s.IsDeepVerified);
            setProgress(IsRussian
                ? $"Готово. Verified: {verified} / {total}"
                : $"Done. Verified: {verified} / {total}");
            try { ServerViewModel.RefreshProviderRiskFlags(servers); }
            catch (Exception ex) { _logger.Warning(ex, "[DeepVerifyAll] RefreshProviderRiskFlags failed"); }
        }
        catch (OperationCanceledException)
        {
            setProgress(Strings.ServerTestCancelled);
            foreach (var vm in servers)
            {
                if (vm.IsDeepTesting) vm.IsDeepTesting = false;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[DeepVerifyAll] failed");
            setProgress($"Error: {ex.GetType().Name}");
        }
        finally
        {
            IsDeepTestingServers = false;
            _serverDeepCts?.Dispose();
            _serverDeepCts = null;

            foreach (var vm in servers)
            {
                if (vm.IsDeepTesting) vm.IsDeepTesting = false;
            }
        }
    }
}
