using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// Server/subscription connectivity testing (v2.15.2).
/// Reuses <see cref="TcpTlsProbe"/> — the same TCP+TLS logic Free Configs
/// uses — so the user can vet their own saved servers without going to the
/// Free tab. Concurrency capped at 20 (v. 80 for Free) since typical Servers
/// lists are small and we don't want to hammer their own remotes.
/// </summary>
public partial class MainWindowViewModel
{
    private CancellationTokenSource? _serverTestCts;
    private CancellationTokenSource? _serverDeepCts;
    private VlessDeepVerifier? _deepVerifier;

    private const int ServerTestConcurrency = 20;
    private const int ServerDeepConcurrency = 5;

    /// <summary>Progress text shown in status area during a Test All run.</summary>
    [ObservableProperty] private string _serverTestProgressText = string.Empty;

    /// <summary>True while any TestAll* operation is in flight — disables other test buttons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerTestButtonText))]
    private bool _isTestingServers;

    /// <summary>"Test all" vs "Cancel" based on IsTestingServers.</summary>
    public string ServerTestButtonText => IsTestingServers
        ? (IsRussian ? "Отмена" : "Cancel")
        : (IsRussian ? "Проверить все" : "Test all");

    /// <summary>Progress text for deep verify passes.</summary>
    [ObservableProperty] private string _serverDeepProgressText = string.Empty;

    /// <summary>True while a deep-verify batch is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerDeepButtonText))]
    private bool _isDeepTestingServers;

    /// <summary>"Deep verify" vs "Stop" text for the button.</summary>
    /// v2.30.5-r1 (UX-24 fix): localize button label in Russian.
    public string ServerDeepButtonText => IsDeepTestingServers
        ? (IsRussian ? "Стоп" : "Stop")
        : (IsRussian ? "Глубокая проверка" : "Deep verify");

    // ── Single-server test (invoked per row) ──────────────────────────────

    [RelayCommand]
    private async Task TestServerAsync(ServerViewModel? server)
    {
        if (server == null) return;
        if (server.IsTesting) return;

        server.IsTesting = true;
        try
        {
            var sni = PickSni(server);
            var result = await TcpTlsProbe.ProbeAsync(
                server.Server, server.Port, sni,
                requireTls: true);
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

    // ── Batch test: all manual VLESS servers ─────────────────────────────

    [RelayCommand]
    private async Task TestAllServersAsync()
    {
        if (IsTestingServers)
        {
            _serverTestCts?.Cancel();
            return;
        }

        await TestServerCollectionAsync(
            Servers.ToList(),
            IsRussian ? "Проверка Manual-серверов" : "Testing Manual servers");
    }

    // ── Batch test: all aggregated subscription servers ──────────────────

    [RelayCommand]
    private async Task TestAllSubscriptionServersAsync()
    {
        if (IsTestingServers)
        {
            _serverTestCts?.Cancel();
            return;
        }

        await TestServerCollectionAsync(
            SubscriptionServers.ToList(),
            IsRussian ? "Проверка подписочных серверов" : "Testing subscription servers");
    }

    // ── Core batch implementation ────────────────────────────────────────

    private async Task TestServerCollectionAsync(
        IReadOnlyList<ServerViewModel> servers,
        string labelPrefix)
    {
        if (servers.Count == 0)
        {
            ServerTestProgressText = IsRussian ? "Нет серверов" : "No servers";
            return;
        }

        _serverTestCts = new CancellationTokenSource();
        var ct = _serverTestCts.Token;

        IsTestingServers = true;
        ServerTestProgressText = $"{labelPrefix}: 0 / {servers.Count}";

        // Mark every row as testing so spinners show immediately
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

                    var sni = PickSni(server);
                    var result = await TcpTlsProbe.ProbeAsync(
                        server.Server, server.Port, sni,
                        requireTls: true, ct: ct);

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
                        ServerTestProgressText = $"{labelPrefix}: {n} / {total}";
                    });
                }
            });

            await Task.WhenAll(tasks);

            // v2.30.7-r2 — AU-5 fix: previous copy "Working: 0/N" was
            // misleading after Test-all. The "ok" count excluded
            // Implausible servers (<5ms RTT — suspicious but the TCP+TLS
            // handshake DID succeed), so users with subscription pools
            // that resolve to local Reality fronts saw "Working: 0/7"
            // even when all 7 servers responded. Now count anything that
            // completed the handshake (Ok+Slow+Implausible) as "passed
            // ping", and clarify that the Deep verify is needed for full
            // validation. Failed buckets (TlsFailed/Timeout/Unreachable)
            // still NOT counted.
            var responded = servers.Count(s => s.TestStatus is
                ServerProbeStatus.Ok or
                ServerProbeStatus.Slow or
                ServerProbeStatus.Implausible);
            ServerTestProgressText = IsRussian
                ? $"Готово. Пинг прошёл: {responded} / {total} · полная проверка — «Глубокая проверка»"
                : $"Done. Pinged: {responded} / {total} · full check via Deep verify";
        }
        catch (OperationCanceledException)
        {
            ServerTestProgressText = IsRussian ? "Отменено" : "Cancelled";
            foreach (var s in servers) s.IsTesting = false;
        }
        finally
        {
            IsTestingServers = false;
            _serverTestCts?.Dispose();
            _serverTestCts = null;
        }
    }

    /// <summary>Pick the right SNI for TLS validation based on the server's security mode.</summary>
    private static string? PickSni(ServerViewModel server)
    {
        // Reality: cert is presented for the masked SNI (e.g. yahoo.com) — use ServerName.
        // TLS: cert is for the actual server hostname — also use ServerName (often = Server).
        // Fall back to Server if ServerName is empty.
        return !string.IsNullOrWhiteSpace(server.ServerName) ? server.ServerName : server.Server;
    }

    // ── Deep verify (sing-box spawn + HTTP probe + bandwidth) ─────────────

    [RelayCommand]
    private async Task DeepVerifyAllServersAsync()
    {
        if (IsDeepTestingServers)
        {
            _serverDeepCts?.Cancel();
            return;
        }

        await DeepVerifyCollectionAsync(
            Servers.ToList(),
            IsRussian ? "Deep verify Manual" : "Deep verify Manual");
    }

    [RelayCommand]
    private async Task DeepVerifyAllSubscriptionServersAsync()
    {
        if (IsDeepTestingServers)
        {
            _serverDeepCts?.Cancel();
            return;
        }

        await DeepVerifyCollectionAsync(
            SubscriptionServers.ToList(),
            IsRussian ? "Deep verify подписки" : "Deep verify subscription");
    }

    private async Task DeepVerifyCollectionAsync(
        IReadOnlyList<ServerViewModel> servers,
        string labelPrefix)
    {
        if (servers.Count == 0)
        {
            ServerDeepProgressText = IsRussian ? "Нет серверов" : "No servers";
            return;
        }

        _deepVerifier ??= new VlessDeepVerifier(_logger);

        if (!_deepVerifier.IsAvailable)
        {
            ServerDeepProgressText = IsRussian
                ? "sing-box не найден"
                : "sing-box binary missing";
            return;
        }

        _serverDeepCts = new CancellationTokenSource();
        var ct = _serverDeepCts.Token;

        IsDeepTestingServers = true;
        ServerDeepProgressText = $"{labelPrefix}: 0 / {servers.Count}";

        // Map VM → entry; remember the mapping so we can push results back.
        var entryToVm = new Dictionary<VlessServerEntry, ServerViewModel>();
        var entries = new List<VlessServerEntry>(servers.Count);
        foreach (var vm in servers)
        {
            vm.IsDeepTesting = true;
            var entry = vm.ToEntry();
            entries.Add(entry);
            entryToVm[entry] = vm;
        }

        var done = 0;
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
                        ServerDeepProgressText = $"{labelPrefix}: {p.done} / {p.total}";
                    });
                }),
                ct: ct);

            var verified = servers.Count(s => s.IsDeepVerified);
            ServerDeepProgressText = IsRussian
                ? $"Готово. Verified: {verified} / {total}"
                : $"Done. Verified: {verified} / {total}";
        }
        catch (OperationCanceledException)
        {
            ServerDeepProgressText = IsRussian ? "Отменено" : "Cancelled";
            foreach (var vm in servers)
            {
                if (vm.IsDeepTesting) vm.IsDeepTesting = false;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[DeepVerifyAll] failed");
            ServerDeepProgressText = $"Error: {ex.GetType().Name}";
        }
        finally
        {
            IsDeepTestingServers = false;
            _serverDeepCts?.Dispose();
            _serverDeepCts = null;

            // Safety: clear any lingering IsDeepTesting flags
            foreach (var vm in servers)
            {
                if (vm.IsDeepTesting) vm.IsDeepTesting = false;
            }
        }
    }
}
